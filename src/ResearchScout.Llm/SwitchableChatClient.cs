using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ResearchScout.Llm;

/// <summary>
/// The one <see cref="IChatClient"/> the harness gets. On every call it asks <see cref="LlmSelector"/> which
/// provider/model is current and forwards to that provider's client, so a switch in the dashboard applies to the next call.
///
/// Note: one agent run should stay on one model. Messages from one provider (for example Gemini thought signatures)
/// are not valid for another. The agent loop (T4) reads <see cref="ResolveAsync"/> once at the start of a run and
/// pins it with <see cref="PinnedSelectionKey"/> in <see cref="ChatOptions.AdditionalProperties"/>.
/// </summary>
public sealed class SwitchableChatClient(LlmSelector selector, IChatClientFactory factory) : IChatClient
{
    /// <summary>Set this key to an <see cref="LlmSelection"/> in ChatOptions.AdditionalProperties to force a model for the call.</summary>
    public const string PinnedSelectionKey = "scout.llm.selection";

    /// <summary>Added to every response's AdditionalProperties so tracing and the cost budget know which model answered.</summary>
    public const string ProviderKey = "scout.llm.provider";

    /// <inheritdoc cref="ProviderKey"/>
    public const string ModelKey = "scout.llm.model";

    private readonly ConcurrentDictionary<LlmSelection, Lazy<IChatClient>> _clients = new();
    private bool _disposed;

    /// <summary>The provider/model the next call will use (unless pinned).</summary>
    public Task<LlmSelection> ResolveAsync(CancellationToken cancellationToken = default) =>
        selector.GetCurrentAsync(cancellationToken);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (selection, inner) = await GetInnerAsync(options, cancellationToken).ConfigureAwait(false);

        var response = await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        response.AdditionalProperties ??= [];
        response.AdditionalProperties[ProviderKey] = selection.Provider;
        response.AdditionalProperties[ModelKey] = selection.ModelId;
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (selection, inner) = await GetInnerAsync(options, cancellationToken).ConfigureAwait(false);

        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            update.AdditionalProperties ??= [];
            update.AdditionalProperties[ProviderKey] = selection.Provider;
            update.AdditionalProperties[ModelKey] = selection.ModelId;
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var client in _clients.Values.Where(c => c.IsValueCreated))
        {
            client.Value.Dispose();
        }

        _clients.Clear();
    }

    private async Task<(LlmSelection Selection, IChatClient Inner)> GetInnerAsync(ChatOptions? options, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var selection = options?.AdditionalProperties?.TryGetValue(PinnedSelectionKey, out var pinned) == true && pinned is LlmSelection p
            ? p
            : await selector.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        // One SDK client per provider/model, created once and reused (they hold HTTP connections).
        var inner = _clients.GetOrAdd(selection, s => new Lazy<IChatClient>(() => factory.Create(s))).Value;
        return (selection, inner);
    }
}
