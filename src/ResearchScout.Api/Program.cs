using System.Globalization;
using System.Net.ServerSentEvents;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using ResearchScout.Api;
using ResearchScout.Harness;
using ResearchScout.Infra;
using ResearchScout.Llm;
using ResearchScout.Scout;

var builder = WebApplication.CreateBuilder(args);

// Render (and most hosts) tell the app which port to use.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection(LlmOptions.SectionName).Get<LlmOptions>() ?? new LlmOptions());
builder.Services.AddSingleton(sp => new ScoutServices(
    ScoutSettings.FromEnvironment(sp.GetRequiredService<IConfiguration>()),
    sp.GetRequiredService<LlmOptions>(),
    LlmProviders.ApiKeysFromEnvironment()));
builder.Services.AddSingleton(sp => new DailySpendGuard(
    sp.GetRequiredService<ScoutServices>().DbFactory,
    decimal.TryParse(Environment.GetEnvironmentVariable("DAILY_SPEND_CAP_USD"), NumberStyles.Number, CultureInfo.InvariantCulture, out var cap) ? cap : 0.50m));
builder.Services.AddSingleton<PendingApprovals>();
builder.Services.AddSingleton(new AdminAuth(Environment.GetEnvironmentVariable("ADMIN_PASSWORD")));
builder.Services.AddSingleton(new RunSlots(int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_RUNS"), out var slots) ? slots : 3));

// Client IP comes from X-Forwarded-For behind the host's proxy (needed for per-IP rate limits).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// Rate limits: a floor under the daily $ cap. Per client IP.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("ask", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
    o.AddPolicy("admin", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// CORS only for origins listed in ALLOWED_ORIGINS (e.g. a portfolio site). The built-in page is same-origin.
var allowedOrigins = (Environment.GetEnvironmentVariable("ALLOWED_ORIGINS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(allowedOrigins).WithMethods("GET", "POST").WithHeaders("Content-Type")));

var app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseRateLimiter();

// Create tables and load the keyword index before taking traffic.
var scout = app.Services.GetRequiredService<ScoutServices>();
var indexed = await scout.InitializeAsync();
app.Logger.LogStartup(indexed, scout.UsingInMemoryVectors);

// ---------- public ----------

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/status", async (ScoutServices s, DailySpendGuard guard, CancellationToken ct) =>
{
    var current = await s.Selector.GetCurrentAsync(ct);
    await using var db = await s.DbFactory.CreateDbContextAsync(ct);
    return Results.Ok(new
    {
        model = s.Catalog.Find(current)?.DisplayName ?? current.ToString(),
        dailySpentUsd = await guard.GetTodaySpentAsync(ct),
        dailyCapUsd = guard.DailyCapUsd,
        papers = await db.Papers.CountAsync(ct),
    });
});

app.MapGet("/papers", async (ScoutServices s, CancellationToken ct) =>
{
    await using var db = await s.DbFactory.CreateDbContextAsync(ct);
    var papers = await db.Papers.AsNoTracking().OrderByDescending(p => p.IngestedAt)
        .Select(p => new { id = p.Id, p.Title, p.Year, p.Url, pages = p.PageCount }).Take(200).ToListAsync(ct);
    return Results.Ok(papers);
});

app.MapPost("/ask", (AskRequest request, HttpContext http, ScoutServices s, DailySpendGuard guard, PendingApprovals approvals, RunSlots runSlots) =>
{
    var question = request.Question?.Trim();
    if (string.IsNullOrEmpty(question) || question.Length > AskRequest.MaxLength)
    {
        return Results.BadRequest(new { error = $"Ask a question of 1-{AskRequest.MaxLength} characters." });
    }

    if (!runSlots.TryTake())
    {
        return Results.Json(new { error = "The demo is busy. Please try again in a minute." }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var runId = Guid.NewGuid();
    var channel = Channel.CreateUnbounded<SseItem<object>>(new UnboundedChannelOptions { SingleReader = true });
    var events = new SseRunObserver(channel.Writer);
    var ct = http.RequestAborted; // closing the tab cancels the run

    _ = Task.Run(async () =>
    {
        try
        {
            var model = await s.Selector.GetCurrentAsync(ct);
            await events.Write("run", new { runId, model = s.Catalog.Find(model)?.DisplayName ?? model.ToString() }, ct);

            var loop = s.CreateLoop(runId, new WebApprovalGate(approvals, events), events, guard);
            await loop.RunAsync(question, s.CreateRunOptions(runId, model, stream: true), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Visitor left; nothing to send.
        }
#pragma warning disable CA1031 // Any failure becomes an "error" event instead of a broken stream.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            app.Logger.LogRunFailed(ex, runId);
            await events.Write("error", new { message = "Something went wrong on the server. Please try again." }, CancellationToken.None);
        }
        finally
        {
            runSlots.Release();
            channel.Writer.TryComplete();
        }
    }, CancellationToken.None);

    return TypedResults.ServerSentEvents(channel.Reader.ReadAllAsync(ct));
}).RequireRateLimiting("ask");

app.MapPost("/runs/{runId:guid}/approvals/{callId}", (Guid runId, string callId, ApprovalAnswer answer, PendingApprovals approvals) =>
    approvals.TryResolve(runId, callId, answer.Approve ? ApprovalDecision.Approve : ApprovalDecision.Reject(answer.Reason))
        ? Results.Ok(new { ok = true })
        : Results.NotFound(new { error = "Nothing is waiting for this approval (answered already or timed out)." }));

app.MapGet("/runs/{runId:guid}/trace", async (Guid runId, ScoutServices s, CancellationToken ct) =>
{
    var run = await new TraceReader(s.DbFactory).GetRunAsync(runId, ct);
    return run is null ? Results.NotFound() : Results.Ok(new
    {
        run.Id,
        run.UserMessage,
        run.Provider,
        run.Model,
        run.Status,
        run.StopReason,
        run.FinalText,
        run.InputTokens,
        run.OutputTokens,
        run.CostUsd,
        run.StartedAt,
        run.FinishedAt,
        steps = run.Steps.Select(st => new
        {
            st.Number,
            st.Model,
            st.InputTokens,
            st.OutputTokens,
            st.CostUsd,
            st.Attempts,
            st.DurationMs,
            st.TextPreview,
            toolCalls = System.Text.Json.JsonDocument.Parse(st.ToolCallsJson).RootElement,
        }),
    });
});

// ---------- admin (X-Admin-Password) ----------

var admin = app.MapGroup("/admin").RequireRateLimiting("admin").AddEndpointFilter(async (ctx, next) =>
{
    var auth = ctx.HttpContext.RequestServices.GetRequiredService<AdminAuth>();
    if (!auth.Enabled)
    {
        return Results.Json(new { error = "Admin is disabled (ADMIN_PASSWORD not set)." }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return auth.IsAdmin(ctx.HttpContext.Request) ? await next(ctx) : Results.Unauthorized();
});

admin.MapGet("/llm", async (ScoutServices s, CancellationToken ct) =>
{
    var current = await s.Selector.GetCurrentAsync(ct);
    return Results.Ok(new
    {
        current = new { current.Provider, current.ModelId },
        available = s.Catalog.Available.Select(m => new
        {
            m.Provider,
            m.ModelId,
            displayName = m.DisplayName ?? m.ModelId,
            m.InputUsdPerMillion,
            m.OutputUsdPerMillion,
        }),
    });
});

admin.MapPut("/llm", async (LlmSelection selection, ScoutServices s, CancellationToken ct) =>
    await s.Selector.TrySetAsync(selection, ct)
        ? Results.Ok(new { current = selection })
        : Results.BadRequest(new { error = "That provider/model is not available (unknown, or its API key is not set)." }));

await app.RunAsync();

/// <summary>Body of POST /ask.</summary>
internal sealed record AskRequest(string? Question)
{
    public const int MaxLength = 1000;
}

/// <summary>Body of POST /runs/{runId}/approvals/{callId}.</summary>
internal sealed record ApprovalAnswer(bool Approve, string? Reason);

/// <summary>Limits how many agent runs happen at once (the free host has 512 MB RAM).</summary>
internal sealed class RunSlots(int max) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(max, max);

    public bool TryTake() => _slots.Wait(0);

    public void Release() => _slots.Release();

    public void Dispose() => _slots.Dispose();
}

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Ready. {Chunks} chunks in the keyword index. In-memory vectors: {InMemory}")]
    public static partial void LogStartup(this ILogger logger, int chunks, bool inMemory);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId} failed")]
    public static partial void LogRunFailed(this ILogger logger, Exception exception, Guid runId);
}

/// <summary>Lets WebApplicationFactory find the app in tests.</summary>
public partial class Program;
