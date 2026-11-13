namespace ResearchScout.Infra;

#pragma warning disable CA1002, CA2227 // EF Core entity classes: settable collections are normal here.

/// <summary>One agent run (table: runs).</summary>
public sealed class RunEntity
{
    public Guid Id { get; set; }
    public required string UserMessage { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }

    /// <summary>"Running" until finished, then an <c>AgentRunStatus</c> name.</summary>
    public required string Status { get; set; }

    public string? FinalText { get; set; }
    public string? StopReason { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<StepEntity> Steps { get; set; } = [];
}

/// <summary>One model call in a run plus its tool calls (table: steps).</summary>
public sealed class StepEntity
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public int Number { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public int Attempts { get; set; }
    public int DurationMs { get; set; }

    /// <summary>First part of the model's text for this step.</summary>
    public string? TextPreview { get; set; }

    /// <summary>JSON array of tool calls (name, input, output preview, error flag, duration, approval).</summary>
    public required string ToolCallsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Key/value settings changed at runtime, e.g. the current LLM (table: app_settings).</summary>
public sealed class AppSettingEntity
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Total LLM spend per UTC day, for the live demo's daily cap (table: daily_spend).</summary>
public sealed class DailySpendEntity
{
    public DateOnly Day { get; set; }
    public decimal SpentUsd { get; set; }
}

/// <summary>Cached external API responses (Semantic Scholar etc.) (table: api_cache).</summary>
public sealed class ApiCacheEntity
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}

#pragma warning restore CA1002, CA2227
