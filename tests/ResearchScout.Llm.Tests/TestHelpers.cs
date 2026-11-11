using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ResearchScout.Llm.Tests;

internal static class TestHelpers
{
    public static LlmOptions Options() => new()
    {
        DefaultProvider = LlmProviders.Anthropic,
        DefaultModelId = "claude-haiku-5-5",
        Models =
        [
            new ModelOption { Provider = LlmProviders.Anthropic, ModelId = "claude-haiku-5-5", InputUsdPerMillion = 1m, OutputUsdPerMillion = 5m },
            new ModelOption { Provider = LlmProviders.Gemini, ModelId = "gemini-3.5-flash-lite", InputUsdPerMillion = 0.30m, OutputUsdPerMillion = 2.50m },
            new ModelOption { Provider = LlmProviders.DeepSeek, ModelId = "deepseek-flash", InputUsdPerMillion = 0.14m, OutputUsdPerMillion = 0.28m },
        ],
    };

    public static Dictionary<string, string> Keys(params string[] providers) =>
        providers.ToDictionary(p => p, p => "key-" + p, StringComparer.Ordinal);
}

/// <summary>Answers with its own name so tests can see which provider client handled a call.</summary>
internal sealed class FakeChatClient(LlmSelection selection) : IChatClient
{
    public LlmSelection Selection { get; } = selection;
    public int Calls { get; private set; }
    public bool Disposed { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Selection.ToString())));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, Selection.ToString());
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => Disposed = true;
}

internal sealed class FakeChatClientFactory : IChatClientFactory
{
    public List<FakeChatClient> Created { get; } = [];

    public IChatClient Create(LlmSelection selection)
    {
        var client = new FakeChatClient(selection);
        Created.Add(client);
        return client;
    }
}
