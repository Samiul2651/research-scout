using Microsoft.Extensions.AI;

namespace ResearchScout.Harness;

/// <summary>Prices one model response in USD. The Llm project implements it with the model catalog.</summary>
public interface ICostCalculator
{
    decimal CostUsd(ChatResponse response);
}

/// <summary>Always 0. Used when no calculator is given (token limits still work).</summary>
public sealed class ZeroCostCalculator : ICostCalculator
{
    public static readonly ZeroCostCalculator Instance = new();

    public decimal CostUsd(ChatResponse response) => 0m;
}

/// <summary>Limits for one agent run. Null means no limit.</summary>
public sealed record RunBudget
{
    /// <summary>Input + output tokens across all model calls in the run.</summary>
    public long? MaxTotalTokens { get; init; } = 200_000;

    /// <summary>USD across all model calls in the run.</summary>
    public decimal? MaxCostUsd { get; init; } = 0.05m;

    public static readonly RunBudget Unlimited = new() { MaxTotalTokens = null, MaxCostUsd = null };
}

/// <summary>
/// Tracks spend during one run and says when to stop.
/// Checked after each model call, so the last call can go slightly over the limit; the run then stops before the next call.
/// </summary>
public sealed class BudgetTracker(RunBudget budget)
{
    public long TotalTokens { get; private set; }

    public decimal CostUsd { get; private set; }

    public void Add(UsageDetails? usage, decimal costUsd)
    {
        TotalTokens += (usage?.InputTokenCount ?? 0) + (usage?.OutputTokenCount ?? 0);
        CostUsd += costUsd;
    }

    /// <summary>Why the run must stop, or null if it can go on.</summary>
    public string? ExceededReason()
    {
        if (budget.MaxTotalTokens is { } maxTokens && TotalTokens >= maxTokens)
        {
            return $"Token budget used: {TotalTokens:N0} of {maxTokens:N0} tokens.";
        }

        if (budget.MaxCostUsd is { } maxCost && CostUsd >= maxCost)
        {
            return $"Cost budget used: ${CostUsd:0.0000} of ${maxCost:0.0000}.";
        }

        return null;
    }
}
