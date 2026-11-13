using System.Net;
using ResearchScout.Harness.Tools;
using static ResearchScout.Harness.Tests.ScriptedChatClient;

namespace ResearchScout.Harness.Tests;

public class AgentLoopServicesTests
{
    private static LambdaTool EmailTool() => new("draft_email", _ => ToolResult.Ok("drafted"), needsApproval: true);

    [Fact]
    public async Task ApprovalNeeded_Approved_ToolRuns()
    {
        var email = EmailTool();
        var gate = new RecordingApprovalGate(approve: true);
        var chat = new ScriptedChatClient(Calls(Call("c1", "draft_email", new { q = "prof" })), Text("sent for review"));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(email), new AgentLoopServices { ApprovalGate = gate })
            .RunAsync("hi", new AgentRunOptions { RunId = Guid.Parse("11111111-1111-1111-1111-111111111111") });

        var request = Assert.Single(gate.Requests);
        Assert.Equal("draft_email", request.ToolName);
        Assert.Equal("""{"q":"prof"}""", request.InputJson);
        Assert.Equal(result.RunId, request.RunId);
        Assert.Single(email.Inputs);
        Assert.True(result.Steps[0].ToolCalls[0].Approved);
    }

    [Fact]
    public async Task ApprovalNeeded_Rejected_ToolDoesNotRun_ModelIsTold()
    {
        var email = EmailTool();
        var chat = new ScriptedChatClient(Calls(Call("c1", "draft_email")), Text("ok, not sending"));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(email),
            new AgentLoopServices { ApprovalGate = new RecordingApprovalGate(approve: false, reason: "wrong professor") }).RunAsync("hi");

        Assert.Empty(email.Inputs);
        var record = result.Steps[0].ToolCalls[0];
        Assert.True(record.IsError);
        Assert.False(record.Approved);
        Assert.Contains("did not approve", record.Output, StringComparison.Ordinal);
        Assert.Contains("wrong professor", record.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovalNeeded_NoGateConfigured_IsRejectedByDefault()
    {
        var email = EmailTool();
        var chat = new ScriptedChatClient(Calls(Call("c1", "draft_email")), Text("ok"));

        await new AgentLoop(chat, new ToolRegistry().Add(email)).RunAsync("hi");

        Assert.Empty(email.Inputs);
    }

    [Fact]
    public async Task ToolsWithoutApproval_DoNotAskTheGate()
    {
        var gate = new RecordingApprovalGate(approve: false);
        var chat = new ScriptedChatClient(Calls(Call("c1", "echo")), Text("ok"));

        await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo()), new AgentLoopServices { ApprovalGate = gate }).RunAsync("hi");

        Assert.Empty(gate.Requests);
    }

    [Fact]
    public async Task SpendGuard_BlocksBeforeTheCallThatWouldGoOver()
    {
        var guard = new FakeSpendGuard(cap: 0.02m);
        var chat = new ScriptedChatClient(Calls(Call("c1", "echo")), Calls(Call("c2", "echo")), Text("never"));
        var services = new AgentLoopServices { SpendGuard = guard, CostCalculator = new FixedCostCalculator(0.01m) };

        var result = await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo()), services)
            .RunAsync("hi", new AgentRunOptions { Budget = RunBudget.Unlimited });

        // call 1: spent $0.01, call 2: spent $0.02 → check before call 3 blocks
        Assert.Equal(AgentRunStatus.SpendLimitReached, result.Status);
        Assert.Equal(0.02m, guard.Spent);
        Assert.Equal(2, chat.Requests.Count);
        Assert.Contains("Daily limit", result.StopReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpendGuard_AlreadyOver_NoModelCallAtAll()
    {
        var guard = new FakeSpendGuard(cap: 0m);
        var chat = new ScriptedChatClient(Text("never"));

        var result = await new AgentLoop(chat, new ToolRegistry(), new AgentLoopServices { SpendGuard = guard }).RunAsync("hi");

        Assert.Equal(AgentRunStatus.SpendLimitReached, result.Status);
        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task Observer_SeesEventsInOrder()
    {
        var observer = new RecordingObserver();
        var chat = new ScriptedChatClient(Calls(Call("c1", "echo")), Text("done"));

        await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo()), new AgentLoopServices { Observer = observer }).RunAsync("hi");

        Assert.Equal(["start", "tool:echo", "step:1", "step:2", "finish:Completed"], observer.Events);
    }

    [Fact]
    public async Task Streaming_SendsTextDeltas_AndGivesSameResult()
    {
        var observer = new RecordingObserver();
        var chat = new ScriptedChatClient(Calls(Call("c1", "echo", new { q = "x" })), Text("final words"));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo()), new AgentLoopServices { Observer = observer })
            .RunAsync("hi", new AgentRunOptions { Stream = true });

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("final words", result.FinalText);
        Assert.Contains("text:final words", observer.Events);
        Assert.Equal("echo:x", result.Steps[0].ToolCalls[0].Output);
        // Usage survives streaming: 2 replies × 10 input tokens = 20, 2 × 5 output tokens = 10.
        Assert.Equal(20, result.Usage.InputTokenCount);
        Assert.Equal(10, result.Usage.OutputTokenCount);
    }

    [Fact]
    public async Task Streaming_FailureBeforeAnyText_IsRetried()
    {
        var chat = new ScriptedChatClient(new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable), Text("ok"));

        var result = await new AgentLoop(chat, new ToolRegistry(), new AgentLoopServices { RetryPolicy = TestRetry.NoWait() })
            .RunAsync("hi", new AgentRunOptions { Stream = true });

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(2, result.Steps[0].Attempts);
    }

    [Fact]
    public async Task Streaming_FailureAfterTextWasShown_IsNotRetried()
    {
        var failure = new StreamFailure("Partial ans", new HttpRequestException("cut", null, HttpStatusCode.ServiceUnavailable));
        var chat = new ScriptedChatClient(failure, Text("would duplicate"));

        var result = await new AgentLoop(chat, new ToolRegistry(), new AgentLoopServices { RetryPolicy = TestRetry.NoWait() })
            .RunAsync("hi", new AgentRunOptions { Stream = true });

        Assert.Equal(AgentRunStatus.ModelError, result.Status);
        Assert.Single(chat.Requests);
    }
}
