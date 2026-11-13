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

    /// <summary>
    /// Streams the next scripted reply as updates. A <see cref="StreamFailure"/> entry streams some text, then throws.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add([.. messages]);
        Options.Add(options);
        var next = _script.Count > 0 ? _script.Dequeue() : throw new InvalidOperationException("Script ran out of replies.");
        await Task.Yield();

        switch (next)
        {
            case StreamFailure failure:
                yield return new ChatResponseUpdate(ChatRole.Assistant, failure.TextBeforeFailure);
                throw failure.Error;
            case Exception ex:
                throw ex;
            default:
                foreach (var update in ((ChatResponse)next).ToChatResponseUpdates())
                {
                    yield return update;
                }

                break;
        }
    }

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

/// <summary>Script entry: stream this text, then fail with this error.</summary>
internal sealed record StreamFailure(string TextBeforeFailure, Exception Error);

/// <summary>Records every observer event as a short string.</summary>
internal sealed class RecordingObserver : IAgentObserver
{
    public List<string> Events { get; } = [];

    public ValueTask OnRunStartedAsync(AgentRunContext run, CancellationToken cancellationToken) => Log("start");

    public ValueTask OnTextDeltaAsync(AgentRunContext run, string text, CancellationToken cancellationToken) => Log("text:" + text);

    public ValueTask OnToolStartedAsync(AgentRunContext run, int stepNumber, string toolName, string inputJson, CancellationToken cancellationToken) =>
        Log($"tool:{toolName}");

    public ValueTask OnStepCompletedAsync(AgentRunContext run, AgentStep completedStep, CancellationToken cancellationToken) =>
        Log($"step:{completedStep.Number}");

    public ValueTask OnRunFinishedAsync(AgentRunContext run, AgentRunResult result, CancellationToken cancellationToken) =>
        Log($"finish:{result.Status}");

    private ValueTask Log(string e)
    {
        Events.Add(e);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Approves or rejects by a fixed rule and records requests.</summary>
internal sealed class RecordingApprovalGate(bool approve, string? reason = null) : IApprovalGate
{
    public List<ApprovalRequest> Requests { get; } = [];

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(approve ? ApprovalDecision.Approve : ApprovalDecision.Reject(reason));
    }
}

/// <summary>In-memory shared cap.</summary>
internal sealed class FakeSpendGuard(decimal cap) : ISpendGuard
{
    public decimal Spent { get; private set; }

    public Task<string?> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Spent >= cap ? $"Daily limit of ${cap} reached." : null);

    public Task RecordAsync(decimal costUsd, CancellationToken cancellationToken)
    {
        Spent += costUsd;
        return Task.CompletedTask;
    }
}
