using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResearchScout.Harness;
using ResearchScout.Llm;

namespace ResearchScout.Infra;

/// <summary>
/// Saves every run and step to Postgres. Plugs into the loop as an <see cref="IAgentObserver"/>.
/// A database problem is logged and ignored: losing a trace is better than failing the user's question.
/// </summary>
public sealed partial class PostgresTraceStore(IDbContextFactory<ScoutDbContext> dbFactory, ILogger<PostgresTraceStore>? logger = null)
    : IAgentObserver
{
    private const int MaxPreviewChars = 2000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ILogger _logger = logger ?? NullLogger<PostgresTraceStore>.Instance;

    public async ValueTask OnRunStartedAsync(AgentRunContext run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await SafeAsync("start run", async db =>
        {
            db.Runs.Add(new RunEntity
            {
                Id = run.RunId,
                UserMessage = run.UserMessage,
                Status = "Running",
                StartedAt = run.StartedAt,
            });
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async ValueTask OnStepCompletedAsync(AgentRunContext run, AgentStep completedStep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(completedStep);

        var (provider, model) = ProviderAndModel(completedStep);
        await SafeAsync("save step", async db =>
        {
            db.Steps.Add(new StepEntity
            {
                RunId = run.RunId,
                Number = completedStep.Number,
                Provider = provider,
                Model = model ?? completedStep.ModelId,
                InputTokens = completedStep.Usage?.InputTokenCount ?? 0,
                OutputTokens = completedStep.Usage?.OutputTokenCount ?? 0,
                CostUsd = completedStep.CostUsd,
                Attempts = completedStep.Attempts,
                DurationMs = (int)completedStep.ModelDuration.TotalMilliseconds,
                TextPreview = Preview(completedStep.Text),
                ToolCallsJson = JsonSerializer.Serialize(
                    completedStep.ToolCalls.Select(c => new
                    {
                        c.CallId,
                        Tool = c.ToolName,
                        Input = c.InputJson,
                        Output = Preview(c.Output),
                        c.IsError,
                        c.Approved,
                        Ms = (int)c.Duration.TotalMilliseconds,
                    }),
                    Json),
                CreatedAt = DateTimeOffset.UtcNow,
            });

            // Keep the run's provider/model up to date from the first step that has one.
            if (provider is not null)
            {
                await db.Runs.Where(r => r.Id == run.RunId && r.Provider == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Provider, provider).SetProperty(r => r.Model, model), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async ValueTask OnRunFinishedAsync(AgentRunContext run, AgentRunResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(result);

        await SafeAsync("finish run", db => db.Runs.Where(r => r.Id == run.RunId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, result.Status.ToString())
                .SetProperty(r => r.FinalText, result.FinalText)
                .SetProperty(r => r.StopReason, result.StopReason)
                .SetProperty(r => r.InputTokens, result.Usage.InputTokenCount ?? 0)
                .SetProperty(r => r.OutputTokens, result.Usage.OutputTokenCount ?? 0)
                .SetProperty(r => r.CostUsd, result.CostUsd)
                .SetProperty(r => r.FinishedAt, DateTimeOffset.UtcNow),
            CancellationToken.None)).ConfigureAwait(false);
    }

    private static (string? Provider, string? Model) ProviderAndModel(AgentStep step)
    {
        var props = step.ResponseProperties;
        var provider = props?.GetValueOrDefault(SwitchableChatClient.ProviderKey) as string;
        var model = props?.GetValueOrDefault(SwitchableChatClient.ModelKey) as string;
        return (provider, model);
    }

    private static string? Preview(string? text) =>
        text is null || text.Length <= MaxPreviewChars ? text : text[..MaxPreviewChars] + "…";

    private async Task SafeAsync(string what, Func<ScoutDbContext, Task> work)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            await work(db).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Tracing must never break a run.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTraceFailed(_logger, ex, what);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trace store could not {What}")]
    private static partial void LogTraceFailed(ILogger logger, Exception exception, string what);
}
