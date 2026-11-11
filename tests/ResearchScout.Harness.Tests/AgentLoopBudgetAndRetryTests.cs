using System.Net;
using ResearchScout.Harness.Tools;
using static ResearchScout.Harness.Tests.ScriptedChatClient;

namespace ResearchScout.Harness.Tests;

public class AgentLoopBudgetAndRetryTests
{
    [Fact]
    public async Task TransientModelError_IsRetried_AndAttemptsAreRecorded()
    {
        var chat = new ScriptedChatClient(new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests), Text("ok"));

        var result = await new AgentLoop(chat, new ToolRegistry(), retryPolicy: TestRetry.NoWait()).RunAsync("hi");

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(2, result.Steps[0].Attempts);
    }

    [Fact]
    public async Task PermanentModelError_EndsRunWithReason_AndKeepsEarlierSteps()
    {
        var chat = new ScriptedChatClient(
            Calls(Call("c1", "echo")),
            new HttpRequestException("bad key", null, HttpStatusCode.Unauthorized));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo()), retryPolicy: TestRetry.NoWait()).RunAsync("hi");

        Assert.Equal(AgentRunStatus.ModelError, result.Status);
        Assert.Contains("step 2", result.StopReason, StringComparison.Ordinal);
        Assert.Contains("bad key", result.StopReason, StringComparison.Ordinal);
        Assert.Single(result.Steps); // step 1 is kept for the trace
    }

    [Fact]
    public async Task TokenBudget_StopsBeforeRunningMoreTools()
    {
        var echo = LambdaTool.Echo();
        var chat = new ScriptedChatClient(CallsWithUsage(900, 200, Call("c1", "echo")), Text("never sent"));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(echo))
            .RunAsync("hi", new AgentRunOptions { Budget = new RunBudget { MaxTotalTokens = 1_000, MaxCostUsd = null } });

        // 900 in + 200 out = 1,100 tokens ≥ limit of 1,000
        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Status);
        Assert.Contains("1,100 of 1,000", result.StopReason, StringComparison.Ordinal);
        Assert.Empty(echo.Inputs);
        Assert.Single(chat.Requests);
    }

    [Fact]
    public async Task CostBudget_StopsTheRun()
    {
        var chat = new ScriptedChatClient(Calls(Call("c1", "echo")), Calls(Call("c2", "echo")), Text("never sent"));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo()), new FixedCostCalculator(0.02m))
            .RunAsync("hi", new AgentRunOptions { Budget = new RunBudget { MaxCostUsd = 0.03m, MaxTotalTokens = null } });

        // step 1: $0.02 (under $0.03) → tools run; step 2: total $0.04 ≥ $0.03 → stop
        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Status);
        Assert.Equal(0.04m, result.CostUsd);
        Assert.Equal(2, chat.Requests.Count);
    }

    [Fact]
    public async Task FinalAnswer_IsKept_EvenIfThatCallWentOverBudget()
    {
        var chat = new ScriptedChatClient(Text("Here is the answer", inputTokens: 5_000, outputTokens: 1_000));

        var result = await new AgentLoop(chat, new ToolRegistry())
            .RunAsync("hi", new AgentRunOptions { Budget = new RunBudget { MaxTotalTokens = 100 } });

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("Here is the answer", result.FinalText);
    }

    [Fact]
    public async Task Cost_IsRecordedPerStep_AndSummed()
    {
        var chat = new ScriptedChatClient(Calls(Call("c1", "echo")), Text("done"));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo()), new FixedCostCalculator(0.001m)).RunAsync("hi");

        Assert.All(result.Steps, s => Assert.Equal(0.001m, s.CostUsd));
        Assert.Equal(0.002m, result.CostUsd); // 2 steps × $0.001
    }
}
