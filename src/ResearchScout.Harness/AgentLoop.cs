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
/// <item>a model call fails and retries can't fix it (<see cref="AgentRunStatus.ModelError"/>).</item>
/// </list>
///
/// Works with any <see cref="IChatClient"/>. It never uses FunctionInvokingChatClient: every tool call goes
/// through <see cref="RunToolAsync"/>, so this class decides what runs and records it.
/// </summary>
public sealed class AgentLoop(
    IChatClient chat,
    ToolRegistry tools,
    ICostCalculator? costCalculator = null,
    RetryPolicy? retryPolicy = null)
{
    // Same settings as Microsoft.Extensions.AI uses, but compact: tool input is stored in traces.
    private static readonly JsonSerializerOptions CompactJson = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    private readonly ICostCalculator _cost = costCalculator ?? ZeroCostCalculator.Instance;
    private readonly RetryPolicy _retry = retryPolicy ?? new RetryPolicy();

    public async Task<AgentRunResult> RunAsync(
        string userMessage,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        options ??= new AgentRunOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSteps, 1);

        var chatOptions = BuildChatOptions(options);
        var messages = new List<ChatMessage> { new(ChatRole.User, userMessage) };
        var steps = new List<AgentStep>();
        var totalUsage = new UsageDetails();
        var budget = new BudgetTracker(options.Budget);
        var lastText = string.Empty;

        AgentRunResult Finish(AgentRunStatus status, string? reason = null) =>
            new(status, lastText, messages, steps, totalUsage, budget.CostUsd, reason);

        for (var stepNumber = 1; stepNumber <= options.MaxSteps; stepNumber++)
        {
            var clock = Stopwatch.StartNew();
            ChatResponse response;
            int attempts;
            try
            {
                (response, attempts) = await _retry.ExecuteAsync(
                    ct => chat.GetResponseAsync(messages, chatOptions, ct), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // Any provider failure ends the run with a readable reason instead of an exception.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return Finish(AgentRunStatus.ModelError, $"Model call failed at step {stepNumber}: {ex.GetType().Name}: {ex.Message}");
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
            lastText = response.Text;

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Where(c => !c.InformationalOnly) // already handled by the provider (server-side tools)
                .ToList();

            if (calls.Count == 0)
            {
                steps.Add(NewStep(stepNumber, response, cost, attempts, modelDuration, []));
                return Finish(AgentRunStatus.Completed);
            }

            // The model wants more work, but the next call would go over budget: stop before running tools.
            if (budget.ExceededReason() is { } reason)
            {
                steps.Add(NewStep(stepNumber, response, cost, attempts, modelDuration, []));
                return Finish(AgentRunStatus.BudgetExceeded, reason);
            }

            var records = new List<ToolCallRecord>(calls.Count);
            var results = new List<AIContent>(calls.Count);

            // One at a time, in the order the model asked. (Parallel calls are a stretch goal.)
            foreach (var call in calls)
            {
                var record = await RunToolAsync(call, options.MaxToolResultChars, cancellationToken).ConfigureAwait(false);
                records.Add(record);
                results.Add(new FunctionResultContent(call.CallId, record.IsError ? "Error: " + record.Output : record.Output));
            }

            messages.Add(new ChatMessage(ChatRole.Tool, results));
            steps.Add(NewStep(stepNumber, response, cost, attempts, modelDuration, records));
        }

        return Finish(AgentRunStatus.MaxStepsReached, $"Stopped after {options.MaxSteps} model calls without a final answer.");
    }

    private ChatOptions BuildChatOptions(AgentRunOptions options)
    {
        var chatOptions = options.BaseChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.Tools = tools.Tools.Count > 0 ? tools.ToDeclarations() : null;
        chatOptions.Instructions = options.SystemPrompt;
        chatOptions.MaxOutputTokens = options.MaxOutputTokens;
        return chatOptions;
    }

    /// <summary>Runs one tool call. Never throws except for cancellation: every failure becomes an error result for the model.</summary>
    private async Task<ToolCallRecord> RunToolAsync(FunctionCallContent call, int maxResultChars, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var inputJson = "{}";

        ToolCallRecord Done(string output, bool isError) =>
            new(call.CallId, call.Name, inputJson, Truncate(output, maxResultChars), isError, clock.Elapsed);

        // The provider adapter could not parse the arguments the model wrote.
        if (call.Exception is not null)
        {
            return Done($"Could not read the arguments for '{call.Name}': {call.Exception.Message}", isError: true);
        }

        if (!tools.TryGet(call.Name, out var tool))
        {
            var known = string.Join(", ", tools.Tools.Select(t => t.Name));
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
