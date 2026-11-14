using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ResearchScout.Harness.Tools;

namespace ResearchScout.Harness;

/// <summary>
/// The agent loop: ask the model → run the tools it asked for → send the results back → repeat.
///
/// Stops when:
/// <list type="bullet">
/// <item>the model answers without tool calls (<see cref="AgentRunStatus.Completed"/>),</item>
/// <item><see cref="AgentRunOptions.MaxSteps"/> model calls were made (<see cref="AgentRunStatus.MaxStepsReached"/>),</item>
/// <item>the run's token or cost budget is used up (<see cref="AgentRunStatus.BudgetExceeded"/>),</item>
/// <item>the shared daily cap is reached (<see cref="AgentRunStatus.SpendLimitReached"/>),</item>
/// <item>a model call fails and retries can't fix it (<see cref="AgentRunStatus.ModelError"/>).</item>
/// </list>
///
/// Works with any <see cref="IChatClient"/>. It never uses FunctionInvokingChatClient: every tool call goes
/// through <see cref="RunToolAsync"/>, so this class decides what runs (including the approval gate) and records it.
/// </summary>
public sealed class AgentLoop
{
    // Same settings as Microsoft.Extensions.AI uses, but compact: tool input is stored in traces.
    private static readonly JsonSerializerOptions CompactJson = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    private static readonly IAgentObserver NoObserver = new CompositeObserver();

    private readonly IChatClient _chat;
    private readonly ToolRegistry _tools;
    private readonly ICostCalculator _cost;
    private readonly RetryPolicy _retry;
    private readonly IAgentObserver _observer;
    private readonly ISpendGuard? _spendGuard;
    private readonly IApprovalGate _approvalGate;

    public AgentLoop(IChatClient chat, ToolRegistry tools, AgentLoopServices? services = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(tools);

        _chat = chat;
        _tools = tools;
        _cost = services?.CostCalculator ?? ZeroCostCalculator.Instance;
        _retry = services?.RetryPolicy ?? new RetryPolicy();
        _observer = services?.Observer ?? NoObserver;
        _spendGuard = services?.SpendGuard;
        _approvalGate = services?.ApprovalGate ?? DenyAllApprovalGate.Instance;
    }

    public async Task<AgentRunResult> RunAsync(
        string userMessage,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        options ??= new AgentRunOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSteps, 1);

        var run = new AgentRunContext(options.RunId ?? Guid.NewGuid(), userMessage, DateTimeOffset.UtcNow);
        var chatOptions = BuildChatOptions(options);
        var messages = new List<ChatMessage> { new(ChatRole.User, userMessage) };
        var steps = new List<AgentStep>();
        var totalUsage = new UsageDetails();
        var budget = new BudgetTracker(options.Budget);
        var lastText = string.Empty;

        await _observer.OnRunStartedAsync(run, cancellationToken).ConfigureAwait(false);

        async Task<AgentRunResult> Finish(AgentRunStatus status, string? reason = null)
        {
            var result = new AgentRunResult(run.RunId, status, lastText, messages, steps, totalUsage, budget.CostUsd, reason);
            await _observer.OnRunFinishedAsync(run, result, CancellationToken.None).ConfigureAwait(false);
            return result;
        }

        async Task AddStep(AgentStep step)
        {
            steps.Add(step);
            await _observer.OnStepCompletedAsync(run, step, cancellationToken).ConfigureAwait(false);
        }

        for (var stepNumber = 1; stepNumber <= options.MaxSteps; stepNumber++)
        {
            if (_spendGuard is not null && await _spendGuard.CheckAsync(cancellationToken).ConfigureAwait(false) is { } blocked)
            {
                return await Finish(AgentRunStatus.SpendLimitReached, blocked).ConfigureAwait(false);
            }

            var clock = Stopwatch.StartNew();
            ChatResponse response;
            int attempts;
            try
            {
                var streamed = false;
                (response, attempts) = await _retry.ExecuteAsync(
                    ct => CallModelAsync(run, messages, chatOptions, options.Stream, () => streamed = true, ct),
                    cancellationToken,
                    canRetry: () => !streamed).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // Any provider failure ends the run with a readable reason instead of an exception.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return await Finish(AgentRunStatus.ModelError, $"Model call failed at step {stepNumber}: {ex.GetType().Name}: {ex.Message}")
                    .ConfigureAwait(false);
            }

            var modelDuration = clock.Elapsed;

            // Keep the assistant messages exactly as returned: they can carry provider data
            // (thinking blocks, Gemini thought signatures) that must go back on the next call.
            messages.AddMessages(response);

            if (response.Usage is not null)
            {
                totalUsage.Add(response.Usage);
            }

            var cost = _cost.CostUsd(response);
            budget.Add(response.Usage, cost);
            if (_spendGuard is not null)
            {
                await _spendGuard.RecordAsync(cost, CancellationToken.None).ConfigureAwait(false);
            }

            lastText = response.Text;

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Where(c => !c.InformationalOnly) // already handled by the provider (server-side tools)
                .ToList();

            if (calls.Count == 0)
            {
                await AddStep(NewStep(stepNumber, response, cost, attempts, modelDuration, [])).ConfigureAwait(false);
                return await Finish(AgentRunStatus.Completed).ConfigureAwait(false);
            }

            // The model wants more work, but the next call would go over budget: stop before running tools.
            if (budget.ExceededReason() is { } reason)
            {
                await AddStep(NewStep(stepNumber, response, cost, attempts, modelDuration, [])).ConfigureAwait(false);
                return await Finish(AgentRunStatus.BudgetExceeded, reason).ConfigureAwait(false);
            }

            var records = new List<ToolCallRecord>(calls.Count);
            var results = new List<AIContent>(calls.Count);

            // One at a time, in the order the model asked. (Parallel calls are a stretch goal.)
            foreach (var call in calls)
            {
                var record = await RunToolAsync(run, stepNumber, call, options.MaxToolResultChars, cancellationToken).ConfigureAwait(false);
                records.Add(record);
                results.Add(new FunctionResultContent(call.CallId, record.IsError ? "Error: " + record.Output : record.Output));
            }

            messages.Add(new ChatMessage(ChatRole.Tool, results));
            await AddStep(NewStep(stepNumber, response, cost, attempts, modelDuration, records)).ConfigureAwait(false);
        }

        return await Finish(AgentRunStatus.MaxStepsReached, $"Stopped after {options.MaxSteps} model calls without a final answer.")
            .ConfigureAwait(false);
    }

    private async Task<ChatResponse> CallModelAsync(
        AgentRunContext run,
        List<ChatMessage> messages,
        ChatOptions chatOptions,
        bool stream,
        Action markStreamed,
        CancellationToken cancellationToken)
    {
        if (!stream)
        {
            return await _chat.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
        }

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _chat.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            var text = update.Text;
            if (!string.IsNullOrEmpty(text))
            {
                markStreamed();
                await _observer.OnTextDeltaAsync(run, text, cancellationToken).ConfigureAwait(false);
            }
        }

        var response = updates.ToChatResponse();

        // ToChatResponse does not copy the updates' AdditionalProperties onto the response. Callers stamp
        // data there (for example which model answered, which the cost calculator needs), so carry it over.
        foreach (var props in updates.Select(u => u.AdditionalProperties).OfType<AdditionalPropertiesDictionary>())
        {
            response.AdditionalProperties ??= [];
            foreach (var (key, value) in props)
            {
                response.AdditionalProperties[key] = value;
            }
        }

        return response;
    }

    private ChatOptions BuildChatOptions(AgentRunOptions options)
    {
        var chatOptions = options.BaseChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.Tools = _tools.Tools.Count > 0 ? _tools.ToDeclarations() : null;
        chatOptions.Instructions = options.SystemPrompt;
        chatOptions.MaxOutputTokens = options.MaxOutputTokens;
        return chatOptions;
    }

    /// <summary>Runs one tool call. Never throws except for cancellation: every failure becomes an error result for the model.</summary>
    private async Task<ToolCallRecord> RunToolAsync(
        AgentRunContext run, int stepNumber, FunctionCallContent call, int maxResultChars, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var inputJson = "{}";
        bool? approved = null;

        ToolCallRecord Done(string output, bool isError) =>
            new(call.CallId, call.Name, inputJson, Truncate(output, maxResultChars), isError, clock.Elapsed, approved);

        // The provider adapter could not parse the arguments the model wrote.
        if (call.Exception is not null)
        {
            return Done($"Could not read the arguments for '{call.Name}': {call.Exception.Message}", isError: true);
        }

        if (!_tools.TryGet(call.Name, out var tool))
        {
            var known = string.Join(", ", _tools.Tools.Select(t => t.Name));
            return Done($"Unknown tool '{call.Name}'. Available tools: {known}.", isError: true);
        }

        JsonElement input;
        try
        {
            input = JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, object?>(), CompactJson);
            inputJson = input.GetRawText();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Done($"Could not read the arguments for '{call.Name}': {ex.Message}", isError: true);
        }

        if (tool.NeedsApproval)
        {
            var decision = await _approvalGate
                .RequestAsync(new ApprovalRequest(run.RunId, stepNumber, call.CallId, call.Name, inputJson), cancellationToken)
                .ConfigureAwait(false);
            approved = decision.Approved;

            if (!decision.Approved)
            {
                var why = string.IsNullOrWhiteSpace(decision.Reason) ? "" : $" Reason: {decision.Reason}";
                return Done($"The user did not approve this call to '{call.Name}'.{why} Do not retry it; tell the user instead.", isError: true);
            }
        }

        await _observer.OnToolStartedAsync(run, stepNumber, call.Name, inputJson, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await tool.RunAsync(input, cancellationToken).ConfigureAwait(false);
            return Done(result.Content, result.IsError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the whole run was cancelled
        }
#pragma warning disable CA1031 // A tool failure must never crash the loop; the model gets the message instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Message only, no stack trace: it goes to the model (and maybe the user).
            return Done($"Tool '{call.Name}' failed: {ex.Message}", isError: true);
        }
    }

    private static AgentStep NewStep(
        int number, ChatResponse response, decimal cost, int attempts, TimeSpan modelDuration, IReadOnlyList<ToolCallRecord> calls) =>
        new(number, response.ModelId, response.Usage, cost, attempts, modelDuration, response.Text, calls, response.AdditionalProperties);

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), $"\n[cut: {text.Length - maxChars} more characters]");
}
