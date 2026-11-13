using Microsoft.EntityFrameworkCore;
using ResearchScout.Harness;
using ResearchScout.Harness.Tests;
using ResearchScout.Harness.Tools;
using ResearchScout.Llm;
using static ResearchScout.Harness.Tests.ScriptedChatClient;

namespace ResearchScout.Infra.Tests;

public class PostgresStoreTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [PostgresFact]
    public async Task LlmSettings_SaveAndOverwrite()
    {
        var store = new PostgresLlmSettingsStore(db.Factory);

        await store.SetAsync(new LlmSelection("anthropic", "claude-haiku-5-5"));
        await store.SetAsync(new LlmSelection("gemini", "gemini-3.5-flash-lite"));

        Assert.Equal(new LlmSelection("gemini", "gemini-3.5-flash-lite"), await store.GetAsync());
    }

    [PostgresFact]
    public async Task DailySpend_ParallelRecords_AreAllCounted()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero));
        var guard = new DailySpendGuard(db.Factory, dailyCapUsd: 0.50m, clock);

        // 50 runs recording $0.01 at the same time must add up to exactly $0.50 (no lost updates).
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => guard.RecordAsync(0.01m, CancellationToken.None)));

        Assert.Equal(0.50m, await guard.GetTodaySpentAsync());
        Assert.Contains("Daily demo limit reached", await guard.CheckAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task DailySpend_NewUtcDay_StartsFromZero()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 2, 1, 23, 59, 0, TimeSpan.Zero));
        var guard = new DailySpendGuard(db.Factory, dailyCapUsd: 0.10m, clock);

        await guard.RecordAsync(0.20m, CancellationToken.None);
        Assert.NotNull(await guard.CheckAsync(CancellationToken.None));

        clock.Now = clock.Now.AddMinutes(2); // now Feb 2 UTC
        Assert.Null(await guard.CheckAsync(CancellationToken.None));
    }

    [PostgresFact]
    public async Task TraceStore_SavesRunStepsAndToolCalls()
    {
        var store = new PostgresTraceStore(db.Factory);
        var reply = Calls(Call("c1", "echo", new { q = "rag" }));
        reply.AdditionalProperties = new()
        {
            [SwitchableChatClient.ProviderKey] = "anthropic",
            [SwitchableChatClient.ModelKey] = "claude-haiku-5-5",
        };
        var chat = new ScriptedChatClient(reply, Text("All done"));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo()),
            new AgentLoopServices { Observer = store, CostCalculator = new FixedCostCalculator(0.001m) }).RunAsync("find rag papers");

        var run = await new TraceReader(db.Factory).GetRunAsync(result.RunId);
        Assert.NotNull(run);
        Assert.Equal("Completed", run.Status);
        Assert.Equal("All done", run.FinalText);
        Assert.Equal("anthropic", run.Provider);
        Assert.Equal(0.002m, run.CostUsd);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal([1, 2], run.Steps.Select(s => s.Number));
        // jsonb reformats the text, so compare parsed values.
        var call = System.Text.Json.JsonDocument.Parse(run.Steps[0].ToolCallsJson).RootElement[0];
        Assert.Equal("echo", call.GetProperty("tool").GetString());
        Assert.Equal("echo:rag", call.GetProperty("output").GetString());
        Assert.Equal("""{"q":"rag"}""", call.GetProperty("input").GetString());
    }

    [PostgresFact]
    public async Task TraceReader_ListsNewestFirst()
    {
        var store = new PostgresTraceStore(db.Factory);
        var loop1 = new AgentLoop(new ScriptedChatClient(Text("a")), new ToolRegistry(), new AgentLoopServices { Observer = store });
        var loop2 = new AgentLoop(new ScriptedChatClient(Text("b")), new ToolRegistry(), new AgentLoopServices { Observer = store });

        var first = await loop1.RunAsync("first");
        await Task.Delay(10);
        var second = await loop2.RunAsync("second");

        var recent = await new TraceReader(db.Factory).ListRecentAsync(50);
        Assert.True(recent.ToList().FindIndex(r => r.Id == second.RunId) < recent.ToList().FindIndex(r => r.Id == first.RunId));
    }
}

public class TraceStoreFailureTests
{
    [Fact]
    public async Task DatabaseDown_RunStillCompletes()
    {
        // Port 1 has nothing listening: every trace write fails, and the run must not care.
        var deadDb = ScoutDb.CreateFactory("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1");
        var store = new PostgresTraceStore(deadDb);

        var result = await new AgentLoop(new ScriptedChatClient(Text("fine")), new ToolRegistry(), new AgentLoopServices { Observer = store })
            .RunAsync("hi");

        Assert.Equal(AgentRunStatus.Completed, result.Status);
    }
}
