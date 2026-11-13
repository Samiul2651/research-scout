using Microsoft.Extensions.AI;

namespace ResearchScout.Harness;

public sealed record AgentRunOptions
{
    /// <summary>Most model calls in one run. Stops runaway tool loops.</summary>
    public int MaxSteps { get; init; } = 12;

    /// <summary>System prompt for the run.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Max output tokens per model call.</summary>
    public int? MaxOutputTokens { get; init; } = 2048;

    /// <summary>Tool results longer than this are cut, so one large result can't blow the token budget.</summary>
    public int MaxToolResultChars { get; init; } = 20_000;

    /// <summary>
    /// Extra chat options copied into every call (for example the Llm project's pinned-model key in AdditionalProperties).
    /// The loop sets Tools, Instructions and MaxOutputTokens itself.
    /// </summary>
    public ChatOptions? BaseChatOptions { get; init; }

    /// <summary>Token and cost limits for this run.</summary>
    public RunBudget Budget { get; init; } = new();

    /// <summary>Id for this run (tracing, approval links). A new one is made if not given.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Stream the model's answer (text arrives in pieces through <see cref="IAgentObserver.OnTextDeltaAsync"/>).</summary>
    public bool Stream { get; init; }
}

/// <summary>Optional services the loop uses. Anything left null gets a safe default.</summary>
public sealed record AgentLoopServices
{
    /// <summary>Prices responses for the budget. Default: everything costs 0.</summary>
    public ICostCalculator? CostCalculator { get; init; }

    /// <summary>Default: 3 tries with backoff, generic error rules.</summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>Gets told about each step (tracing, live streaming).</summary>
    public IAgentObserver? Observer { get; init; }

    /// <summary>Spend limit shared across runs (the daily cap).</summary>
    public ISpendGuard? SpendGuard { get; init; }

    /// <summary>Asks a human before tools that need approval. Default: reject them.</summary>
    public IApprovalGate? ApprovalGate { get; init; }
}

public enum AgentRunStatus
{
    /// <summary>The model gave a final answer (a reply with no tool calls).</summary>
    Completed,

    /// <summary>Stopped after <see cref="AgentRunOptions.MaxSteps"/> model calls without a final answer.</summary>
    MaxStepsReached,

    /// <summary>Stopped because the run's token or cost budget was used up.</summary>
    BudgetExceeded,

    /// <summary>A model call failed and could not be retried (or retries ran out). See <see cref="AgentRunResult.StopReason"/>.</summary>
    ModelError,

    /// <summary>The shared spend limit (daily cap) was reached before a model call.</summary>
    SpendLimitReached,
}

/// <summary>One tool call inside a step.</summary>
public sealed record ToolCallRecord(
    string CallId,
    string ToolName,
    string InputJson,
    string Output,
    bool IsError,
    TimeSpan Duration,
    bool? Approved = null);

/// <summary>One model call plus the tool calls it asked for. Tracing (T6) saves these.</summary>
public sealed record AgentStep(
    int Number,
    string? ModelId,
    UsageDetails? Usage,
    decimal CostUsd,
    int Attempts,
    TimeSpan ModelDuration,
    string Text,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    IReadOnlyDictionary<string, object?>? ResponseProperties);

public sealed record AgentRunResult(
    Guid RunId,
    AgentRunStatus Status,
    string FinalText,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AgentStep> Steps,
    UsageDetails Usage,
    decimal CostUsd,
    string? StopReason = null);
