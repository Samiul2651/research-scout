using System.Text.Json;
using Microsoft.Extensions.AI;
using ResearchScout.Harness.Tools;

namespace ResearchScout.Harness.Tests;

/// <summary>A fake model that returns scripted replies in order and records what it was sent.</summary>
/// Script entries are <see cref="ChatResponse"/>s to return or <see cref="Exception"/>s to throw.
internal sealed class ScriptedChatClient(params object[] script) : IChatClient
{
    private readonly Queue<object> _script = new(script);

    /// <summary>A copy of the message list sent on each call.</summary>
    public List<List<ChatMessage>> Requests { get; } = [];

    public List<ChatOptions?> Options { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add([.. messages]);
        Options.Add(options);
        var next = _script.Count > 0 ? _script.Dequeue() : throw new InvalidOperationException("Script ran out of replies.");
        return next is Exception ex ? Task.FromException<ChatResponse>(ex) : Task.FromResult((ChatResponse)next);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    // ---- reply builders ----

    public static ChatResponse Text(string text, int inputTokens = 10, int outputTokens = 5) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
        };

    public static ChatResponse Calls(params FunctionCallContent[] calls) => CallsWithUsage(10, 5, calls);

    public static ChatResponse CallsWithUsage(int inputTokens, int outputTokens, params FunctionCallContent[] calls) =>
        new(new ChatMessage(ChatRole.Assistant, [.. calls]))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
        };

    public static FunctionCallContent Call(string id, string name, object? args = null) =>
        new(id, name, args is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args)));
}

/// <summary>A tool whose behaviour is a lambda. Records the inputs it got.</summary>
internal sealed class LambdaTool(string name, Func<JsonElement, ToolResult> run, bool needsApproval = false) : ITool
{
    public string Name { get; } = name;
    public string Description => $"Test tool {Name}";
    public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""").RootElement;
    public bool NeedsApproval { get; } = needsApproval;
    public List<JsonElement> Inputs { get; } = [];

    public Task<ToolResult> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        Inputs.Add(input.Clone());
        return Task.FromResult(run(input));
    }

    public static LambdaTool Echo(string name = "echo") =>
        new(name, input => ToolResult.Ok("echo:" + (input.TryGetProperty("q", out var q) ? q.GetString() : "")));
}

/// <summary>Charges a fixed amount per response.</summary>
internal sealed class FixedCostCalculator(decimal perResponse) : ICostCalculator
{
    public decimal CostUsd(ChatResponse response) => perResponse;
}

internal static class TestRetry
{
    /// <summary>Real retry rules, but no sleeping; records the waits.</summary>
    public static RetryPolicy NoWait(List<TimeSpan>? waits = null, int maxAttempts = 3) => new()
    {
        MaxAttempts = maxAttempts,
        Random = () => 1.0,
        Delay = (wait, _) =>
        {
            waits?.Add(wait);
            return Task.CompletedTask;
        },
    };
}
