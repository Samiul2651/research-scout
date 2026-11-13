using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Threading.Channels;
using ResearchScout.Harness;

namespace ResearchScout.Api;

/// <summary>
/// Turns loop events into Server-Sent Events for one run. The loop writes into a channel; the /ask
/// endpoint streams the channel to the browser.
///
/// Events: run, text, tool, step, approval, done, error.
/// </summary>
internal sealed class SseRunObserver(ChannelWriter<SseItem<object>> events) : IAgentObserver
{
    public ValueTask OnTextDeltaAsync(AgentRunContext run, string text, CancellationToken cancellationToken) =>
        Write("text", new { delta = text }, cancellationToken);

    public ValueTask OnToolStartedAsync(AgentRunContext run, int stepNumber, string toolName, string inputJson, CancellationToken cancellationToken) =>
        Write("tool", new { step = stepNumber, name = toolName, input = inputJson }, cancellationToken);

    public ValueTask OnStepCompletedAsync(AgentRunContext run, AgentStep completedStep, CancellationToken cancellationToken) =>
        Write("step", new
        {
            number = completedStep.Number,
            inputTokens = completedStep.Usage?.InputTokenCount ?? 0,
            outputTokens = completedStep.Usage?.OutputTokenCount ?? 0,
            costUsd = completedStep.CostUsd,
            ms = (int)completedStep.ModelDuration.TotalMilliseconds,
            tools = completedStep.ToolCalls.Select(c => new
            {
                name = c.ToolName,
                input = c.InputJson,
                output = c.Output.Length > 600 ? c.Output[..600] + "…" : c.Output,
                isError = c.IsError,
                approved = c.Approved,
            }),
        }, cancellationToken);

    public ValueTask OnRunFinishedAsync(AgentRunContext run, AgentRunResult result, CancellationToken cancellationToken) =>
        Write("done", new
        {
            runId = result.RunId,
            status = result.Status.ToString(),
            finalText = result.FinalText,
            // Provider error details stay in the trace; visitors get a plain message.
            stopReason = result.Status == AgentRunStatus.ModelError
                ? "The AI model could not answer right now. Please try again in a few minutes."
                : result.StopReason,
            costUsd = result.CostUsd,
            inputTokens = result.Usage.InputTokenCount ?? 0,
            outputTokens = result.Usage.OutputTokenCount ?? 0,
        }, CancellationToken.None);

    public ValueTask Write(string eventType, object data, CancellationToken cancellationToken) =>
        events.WriteAsync(new SseItem<object>(data, eventType), cancellationToken);
}

/// <summary>
/// Approval requests waiting for the browser. The loop's <see cref="WebApprovalGate"/> registers a request and waits;
/// POST /runs/{runId}/approvals/{callId} resolves it. Unanswered requests are rejected after <see cref="Timeout"/>.
/// </summary>
internal sealed class PendingApprovals
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(Guid RunId, string CallId), TaskCompletionSource<ApprovalDecision>> _pending = new();

    public async Task<ApprovalDecision> WaitAsync(Guid runId, string callId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[(runId, callId)] = tcs;
        try
        {
            return await tcs.Task.WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return ApprovalDecision.Reject("No answer within 5 minutes.");
        }
        finally
        {
            _pending.TryRemove((runId, callId), out _);
        }
    }

    /// <summary>False if nothing is waiting for that run/call (already answered, timed out, or wrong id).</summary>
    public bool TryResolve(Guid runId, string callId, ApprovalDecision decision) =>
        _pending.TryGetValue((runId, callId), out var tcs) && tcs.TrySetResult(decision);
}

/// <summary>Sends an "approval" event to the browser and waits for its answer.</summary>
internal sealed class WebApprovalGate(PendingApprovals pending, SseRunObserver events) : IApprovalGate
{
    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Register before telling the browser, so a very fast click can't arrive first.
        var wait = pending.WaitAsync(request.RunId, request.CallId, cancellationToken);
        await events.Write("approval", new
        {
            runId = request.RunId,
            callId = request.CallId,
            tool = request.ToolName,
            input = request.InputJson,
            expiresInSeconds = (int)PendingApprovals.Timeout.TotalSeconds,
        }, cancellationToken).ConfigureAwait(false);

        return await wait.ConfigureAwait(false);
    }
}
