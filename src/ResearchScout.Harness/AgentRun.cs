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
}

public enum AgentRunStatus
{
    /// <summary>The model gave a final answer (a reply with no tool calls).</summary>
    Completed,

    /// <summary>Stopped after <see cref="AgentRunOptions.MaxSteps"/> model calls without a final answer.</summary>
    MaxStepsReached,
}

/// <summary>One tool call inside a step.</summary>
public sealed record ToolCallRecord(
    string CallId,
    string ToolName,
    string InputJson,
    string Output,
    bool IsError,
    TimeSpan Duration);

/// <summary>One model call plus the tool calls it asked for. Tracing (T6) saves these.</summary>
public sealed record AgentStep(
    int Number,
    string? ModelId,
    UsageDetails? Usage,
    TimeSpan ModelDuration,
    string Text,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    IReadOnlyDictionary<string, object?>? ResponseProperties);

public sealed record AgentRunResult(
    AgentRunStatus Status,
    string FinalText,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AgentStep> Steps,
    UsageDetails Usage);
