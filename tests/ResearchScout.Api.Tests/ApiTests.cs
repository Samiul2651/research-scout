using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ResearchScout.Harness.Tests;
using ResearchScout.Infra;
using ResearchScout.Infra.Tests;
using ResearchScout.Llm;
using ResearchScout.Papers.Tests;
using ResearchScout.Scout;
using static ResearchScout.Harness.Tests.ScriptedChatClient;

namespace ResearchScout.Api.Tests;

/// <summary>An IChatClient whose behaviour each test sets, so one app instance can serve many tests.</summary>
internal sealed class SwappableChatClient : IChatClient
{
    public IChatClient Inner { get; set; } = new ScriptedChatClient();

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Inner.GetResponseAsync(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Starts the real app in memory with a test database, a scripted model and fake embeddings.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string AdminPassword = "test-admin-pw";
    private readonly PostgresFixture _db = new();

    internal SwappableChatClient Model { get; } = new();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public IDbContextFactory<ScoutDbContext> Db => _db.Factory;

    public async Task InitializeAsync()
    {
        if (PostgresFixture.AdminConnectionString is null)
        {
            return;
        }

        await _db.InitializeAsync();
        var dbUrl = new NpgsqlConnectionStringBuilder(PostgresFixture.AdminConnectionString) { Database = _db.Factory.CreateDbContext().Database.GetDbConnection().Database }.ConnectionString;

        Environment.SetEnvironmentVariable("DATABASE_URL", dbUrl);
        Environment.SetEnvironmentVariable("ADMIN_PASSWORD", AdminPassword);

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.ConfigureTestServices(services =>
            {
                services.AddSingleton(sp =>
                {
                    var llmOptions = sp.GetRequiredService<LlmOptions>();
                    var settings = ScoutSettings.FromEnvironment(sp.GetRequiredService<IConfiguration>()) with { DatabaseUrl = dbUrl };
                    return new ScoutServices(
                        settings,
                        llmOptions,
                        new Dictionary<string, string> { ["anthropic"] = "fake", ["gemini"] = "fake" },
                        new FixedFactory(Model),
                        new HashEmbedder());
                });
                services.AddSingleton(sp => new DailySpendGuard(sp.GetRequiredService<ScoutServices>().DbFactory, 0.50m));
            });
        });
    }

    public async Task DisposeAsync()
    {
        if (PostgresFixture.AdminConnectionString is null)
        {
            return;
        }

        await Factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    private sealed class FixedFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient Create(LlmSelection selection) => client;
    }
}

public class ApiTests(ApiFixture app) : IClassFixture<ApiFixture>
{
    private static async Task<List<(string Type, JsonElement Data)>> ReadEvents(HttpResponseMessage response, Func<string, JsonElement, Task>? onEvent = null)
    {
        var events = new List<(string, JsonElement)>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        string? type = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                type = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal) && type is not null)
            {
                var data = JsonDocument.Parse(line[5..].Trim()).RootElement.Clone();
                events.Add((type, data));
                if (onEvent is not null)
                {
                    await onEvent(type, data);
                }
            }
        }

        return events;
    }

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    /// <summary>POST that returns as soon as headers arrive, so events can be read while the run is still going (like a browser).</summary>
    private static Task<HttpResponseMessage> PostStream(HttpClient client, string url, object body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = Json(body) }, HttpCompletionOption.ResponseHeadersRead);

    [PostgresFact]
    public async Task Health_IsOk()
    {
        var response = await app.Factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [PostgresFact]
    public async Task HomePage_IsServed()
    {
        var html = await app.Factory.CreateClient().GetStringAsync("/");

        Assert.Contains("Research Scout", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Ask_StreamsEvents_AndSavesATrace()
    {
        app.Model.Inner = new ScriptedChatClient(Text("Hello from the scripted model."));
        var client = app.Factory.CreateClient();

        using var response = await PostStream(client, "/ask", new { question = "Say hello" });
        var events = await ReadEvents(response);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("run", events[0].Type);
        Assert.Contains(events, e => e.Type == "text");
        var done = events.Single(e => e.Type == "done").Data;
        Assert.Equal("Completed", done.GetProperty("status").GetString());
        Assert.Equal("Hello from the scripted model.", done.GetProperty("finalText").GetString());

        var runId = done.GetProperty("runId").GetString();
        var trace = await client.GetFromJsonAsync<JsonElement>($"/runs/{runId}/trace");
        Assert.Equal("Completed", trace.GetProperty("status").GetString());
        Assert.Equal(1, trace.GetProperty("steps").GetArrayLength());
    }

    [PostgresFact]
    public async Task Ask_ApprovalFlow_RejectStopsTheTool()
    {
        app.Model.Inner = new ScriptedChatClient(
            Calls(Call("call-1", "draft_email", new { authorId = "7", professorName = "Dr. X", subject = "Hi", body = "Hello", citedPaperIds = new[] { "p1" } })),
            Text("Okay, I did not save the draft."));
        var client = app.Factory.CreateClient();

        using var response = await PostStream(client, "/ask", new { question = "Draft an email to Dr. X" });
        var events = await ReadEvents(response, async (type, data) =>
        {
            if (type == "approval")
            {
                var answer = await client.PostAsync(
                    $"/runs/{data.GetProperty("runId").GetString()}/approvals/{data.GetProperty("callId").GetString()}",
                    Json(new { approve = false, reason = "not now" }));
                Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            }
        });

        var approval = events.Single(e => e.Type == "approval").Data;
        Assert.Equal("draft_email", approval.GetProperty("tool").GetString());

        var step = events.First(e => e.Type == "step").Data;
        var tool = step.GetProperty("tools")[0];
        Assert.False(tool.GetProperty("approved").GetBoolean());
        Assert.Contains("not now", tool.GetProperty("output").GetString(), StringComparison.Ordinal);
        Assert.Equal("Completed", events.Single(e => e.Type == "done").Data.GetProperty("status").GetString());

        await using var db = await app.Db.CreateDbContextAsync();
        Assert.Equal(0, await db.EmailDrafts.CountAsync());
    }

    [PostgresFact]
    public async Task Approval_ForUnknownRun_Is404()
    {
        var response = await app.Factory.CreateClient().PostAsync($"/runs/{Guid.NewGuid()}/approvals/x", Json(new { approve = true }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [PostgresFact]
    public async Task Ask_EmptyOrTooLong_IsRejected()
    {
        var client = app.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/ask", Json(new { question = "  " }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/ask", Json(new { question = new string('x', 1001) }))).StatusCode);
    }

    [PostgresFact]
    public async Task Admin_RequiresTheRightPassword()
    {
        var client = app.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/llm")).StatusCode);

        client.DefaultRequestHeaders.Add("X-Admin-Password", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/llm")).StatusCode);
    }

    [PostgresFact]
    public async Task Admin_SwitchesModel_AndStatusShowsIt()
    {
        var client = app.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Password", ApiFixture.AdminPassword);

        var list = await client.GetFromJsonAsync<JsonElement>("/admin/llm");
        Assert.Equal(2, list.GetProperty("available").GetArrayLength()); // anthropic + gemini have keys; deepseek doesn't

        var bad = await client.PutAsync("/admin/llm", Json(new { provider = "deepseek", modelId = "deepseek-flash" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await client.PutAsync("/admin/llm", Json(new { provider = "gemini", modelId = "gemini-3.5-flash-lite" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/status");
        Assert.Equal("Gemini 3.5 Flash-Lite", status.GetProperty("model").GetString());

        // Put it back for other tests.
        await client.PutAsync("/admin/llm", Json(new { provider = "anthropic", modelId = "claude-haiku-5-5" }));
    }

    [PostgresFact]
    public async Task DailyCapReached_RunStopsBeforeCallingTheModel()
    {
        await using (var db = await app.Db.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO daily_spend (day, spent_usd) VALUES ({DateOnly.FromDateTime(DateTime.UtcNow)}, {1.00m}) ON CONFLICT (day) DO UPDATE SET spent_usd = 1.00");
        }

        var scripted = new ScriptedChatClient(Text("should not be called"));
        app.Model.Inner = scripted;

        try
        {
            using var response = await PostStream(app.Factory.CreateClient(), "/ask", new { question = "hi" });
            var done = (await ReadEvents(response)).Single(e => e.Type == "done").Data;

            Assert.Equal("SpendLimitReached", done.GetProperty("status").GetString());
            Assert.Contains("Daily demo limit", done.GetProperty("stopReason").GetString(), StringComparison.Ordinal);
            Assert.Empty(scripted.Requests);
        }
        finally
        {
            await using var db = await app.Db.CreateDbContextAsync();
            await db.DailySpend.ExecuteDeleteAsync();
        }
    }
}
