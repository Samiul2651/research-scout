namespace ResearchScout.Harness;

/// <summary>Identifies one run for observers (tracing, live streaming).</summary>
public sealed record AgentRunContext(Guid RunId, string UserMessage, DateTimeOffset StartedAt);

/// <summary>
/// Gets told what the loop is doing. Used for tracing (T6) and live streaming to the web page (T15).
/// All methods have empty default bodies, so implement only what you need.
/// Observers run inline: keep them fast, and do not throw unless the run should stop.
/// </summary>
public interface IAgentObserver
{
    ValueTask OnRunStartedAsync(AgentRunContext run, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>A piece of answer text as it streams in (only when streaming is on).</summary>
    ValueTask OnTextDeltaAsync(AgentRunContext run, string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>A tool is about to run.</summary>
    ValueTask OnToolStartedAsync(AgentRunContext run, int stepNumber, string toolName, string inputJson, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    ValueTask OnStepCompletedAsync(AgentRunContext run, AgentStep completedStep, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask OnRunFinishedAsync(AgentRunContext run, AgentRunResult result, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Sends every event to several observers, in order.</summary>
public sealed class CompositeObserver(params IAgentObserver[] observers) : IAgentObserver
{
    public async ValueTask OnRunStartedAsync(AgentRunContext run, CancellationToken cancellationToken)
    {
        foreach (var o in observers)
        {
            await o.OnRunStartedAsync(run, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask OnTextDeltaAsync(AgentRunContext run, string text, CancellationToken cancellationToken)
    {
        foreach (var o in observers)
        {
            await o.OnTextDeltaAsync(run, text, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask OnToolStartedAsync(AgentRunContext run, int stepNumber, string toolName, string inputJson, CancellationToken cancellationToken)
    {
        foreach (var o in observers)
        {
            await o.OnToolStartedAsync(run, stepNumber, toolName, inputJson, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask OnStepCompletedAsync(AgentRunContext run, AgentStep completedStep, CancellationToken cancellationToken)
    {
        foreach (var o in observers)
        {
            await o.OnStepCompletedAsync(run, completedStep, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask OnRunFinishedAsync(AgentRunContext run, AgentRunResult result, CancellationToken cancellationToken)
    {
        foreach (var o in observers)
        {
            await o.OnRunFinishedAsync(run, result, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A spend limit shared by many runs (the live demo's daily cap). Checked before every model call.
/// </summary>
public interface ISpendGuard
{
    /// <summary>Returns why no more spending is allowed right now, or null if it is fine.</summary>
    Task<string?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Adds the cost of a model call that just happened.</summary>
    Task RecordAsync(decimal costUsd, CancellationToken cancellationToken);
}
