using System.Text.Json;
using Microsoft.Extensions.AI;
using ResearchScout.Harness.Tools;

namespace ResearchScout.Harness.Tests;

/// <summary>A fake model that returns scripted replies in order and records what it was sent.</summary>
internal sealed class ScriptedChatClient(params ChatResponse[] replies) : IChatClient
{
    private readonly Queue<ChatResponse> _replies = new(replies);

    /// <summary>A copy of the message list sent on each call.</summary>
    public List<List<ChatMessage>> Requests { get; } = [];

    public List<ChatOptions?> Options { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add([.. messages]);
        Options.Add(options);
        return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : throw new InvalidOperationException("Script ran out of replies."));
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

    public static ChatResponse Calls(params FunctionCallContent[] calls) =>
        new(new ChatMessage(ChatRole.Assistant, [.. calls]))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
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
