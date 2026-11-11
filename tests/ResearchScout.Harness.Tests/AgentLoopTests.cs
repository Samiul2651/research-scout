using Microsoft.Extensions.AI;
using ResearchScout.Harness.Tools;
using static ResearchScout.Harness.Tests.ScriptedChatClient;

namespace ResearchScout.Harness.Tests;

public class AgentLoopTests
{
    private static List<FunctionResultContent> ToolResultsIn(List<ChatMessage> request) =>
        [.. request.Where(m => m.Role == ChatRole.Tool).SelectMany(m => m.Contents).OfType<FunctionResultContent>()];

    [Fact]
    public async Task NoToolCalls_CompletesInOneStep()
    {
        var chat = new ScriptedChatClient(Text("Final answer"));

        var result = await new AgentLoop(chat, new ToolRegistry()).RunAsync("hi");

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("Final answer", result.FinalText);
        Assert.Single(result.Steps);
    }

    [Fact]
    public async Task ToolCall_RunsTool_SendsResultBack_ThenFinishes()
    {
        var echo = LambdaTool.Echo();
        var chat = new ScriptedChatClient(Calls(Call("c1", "echo", new { q = "rag" })), Text("Done"));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(echo)).RunAsync("hi");

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("rag", Assert.Single(echo.Inputs).GetProperty("q").GetString());

        // Second request = user, assistant (with the call), tool (with the result).
        var second = chat.Requests[1];
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool], second.Select(m => m.Role));
        var toolResult = Assert.Single(ToolResultsIn(second));
        Assert.Equal("c1", toolResult.CallId);
        Assert.Equal("echo:rag", toolResult.Result);

        var record = Assert.Single(result.Steps[0].ToolCalls);
        Assert.Equal("""{"q":"rag"}""", record.InputJson);
        Assert.False(record.IsError);
    }

    [Fact]
    public async Task AssistantMessage_IsSentBackUnchanged()
    {
        // Provider data (thinking, thought signatures) lives on the assistant message, so it must be the same object.
        var firstReply = Calls(Call("c1", "echo"));
        var chat = new ScriptedChatClient(firstReply, Text("Done"));

        await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo())).RunAsync("hi");

        Assert.Same(firstReply.Messages[0], chat.Requests[1][1]);
    }

    [Fact]
    public async Task UnknownTool_SendsErrorToModel_AndKeepsGoing()
    {
        var chat = new ScriptedChatClient(Calls(Call("c1", "made_up")), Text("Sorry"));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo())).RunAsync("hi");

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        var sent = (string)Assert.Single(ToolResultsIn(chat.Requests[1])).Result!;
        Assert.StartsWith("Error: Unknown tool 'made_up'", sent, StringComparison.Ordinal);
        Assert.Contains("echo", sent, StringComparison.Ordinal);
        Assert.True(result.Steps[0].ToolCalls[0].IsError);
    }

    [Fact]
    public async Task ToolThrows_SendsMessageWithoutStackTrace_AndKeepsGoing()
    {
        var broken = new LambdaTool("broken", _ => throw new InvalidOperationException("database down"));
        var chat = new ScriptedChatClient(Calls(Call("c1", "broken")), Text("Could not do it"));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(broken)).RunAsync("hi");

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        var sent = (string)Assert.Single(ToolResultsIn(chat.Requests[1])).Result!;
        Assert.Equal("Error: Tool 'broken' failed: database down", sent);
        Assert.DoesNotContain(" at ", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolReturnsError_IsMarkedAsError()
    {
        var notFound = new LambdaTool("lookup", _ => ToolResult.Error("paper not found"));
        var chat = new ScriptedChatClient(Calls(Call("c1", "lookup")), Text("ok"));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(notFound)).RunAsync("hi");

        Assert.Equal("Error: paper not found", Assert.Single(ToolResultsIn(chat.Requests[1])).Result);
        Assert.True(result.Steps[0].ToolCalls[0].IsError);
    }

    [Fact]
    public async Task BadArgumentsFromProvider_SendsErrorToModel()
    {
        var call = Call("c1", "echo");
        call.Exception = new FormatException("unexpected end of JSON");
        var echo = LambdaTool.Echo();
        var chat = new ScriptedChatClient(Calls(call), Text("ok"));

        await new AgentLoop(chat, new ToolRegistry().Add(echo)).RunAsync("hi");

        Assert.Empty(echo.Inputs); // tool never ran
        Assert.Contains("Could not read the arguments", (string)ToolResultsIn(chat.Requests[1])[0].Result!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralCallsInOneReply_AllRun_ResultsInSameOrder()
    {
        var echo = LambdaTool.Echo();
        var chat = new ScriptedChatClient(
            Calls(Call("c1", "echo", new { q = "one" }), Call("c2", "echo", new { q = "two" })),
            Text("Done"));

        await new AgentLoop(chat, new ToolRegistry().Add(echo)).RunAsync("hi");

        var results = ToolResultsIn(chat.Requests[1]);
        Assert.Equal(["c1", "c2"], results.Select(r => r.CallId));
        Assert.Equal(["echo:one", "echo:two"], results.Select(r => (string)r.Result!));
        Assert.Single(chat.Requests[1], m => m.Role == ChatRole.Tool); // one tool message holding both results
    }

    [Fact]
    public async Task InformationalCalls_AreNotRun()
    {
        var echo = LambdaTool.Echo();
        var informational = Call("c1", "echo");
        informational.InformationalOnly = true;
        var chat = new ScriptedChatClient(Calls(informational));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(echo)).RunAsync("hi");

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Empty(echo.Inputs);
    }

    [Fact]
    public async Task MaxSteps_StopsTheLoop()
    {
        var chat = new ScriptedChatClient(Calls(Call("c1", "echo")), Calls(Call("c2", "echo")), Calls(Call("c3", "echo")));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo()))
            .RunAsync("hi", new AgentRunOptions { MaxSteps = 2 });

        Assert.Equal(AgentRunStatus.MaxStepsReached, result.Status);
        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(2, result.Steps.Count);
    }

    [Fact]
    public async Task Usage_IsSummedAcrossSteps()
    {
        var chat = new ScriptedChatClient(Calls(Call("c1", "echo")), Text("Done", inputTokens: 30, outputTokens: 7));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo())).RunAsync("hi");

        // step 1: 10 in / 5 out, step 2: 30 in / 7 out → 40 in / 12 out
        Assert.Equal(40, result.Usage.InputTokenCount);
        Assert.Equal(12, result.Usage.OutputTokenCount);
    }

    [Fact]
    public async Task LongToolResult_IsCut()
    {
        var big = new LambdaTool("big", _ => ToolResult.Ok(new string('x', 50)));
        var chat = new ScriptedChatClient(Calls(Call("c1", "big")), Text("ok"));

        await new AgentLoop(chat, new ToolRegistry().Add(big)).RunAsync("hi", new AgentRunOptions { MaxToolResultChars = 10 });

        var sent = (string)ToolResultsIn(chat.Requests[1])[0].Result!;
        Assert.StartsWith("xxxxxxxxxx\n[cut: 40 more characters]", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatOptions_HaveToolsPromptAndBaseProperties()
    {
        var chat = new ScriptedChatClient(Text("ok"));
        var baseOptions = new ChatOptions { AdditionalProperties = new() { ["pin"] = "claude" } };

        await new AgentLoop(chat, new ToolRegistry().Add(LambdaTool.Echo()))
            .RunAsync("hi", new AgentRunOptions { SystemPrompt = "Cite pages.", MaxOutputTokens = 500, BaseChatOptions = baseOptions });

        var sent = chat.Options[0]!;
        Assert.Equal("Cite pages.", sent.Instructions);
        Assert.Equal(500, sent.MaxOutputTokens);
        Assert.Equal("echo", Assert.Single(sent.Tools!).Name);
        Assert.Equal("claude", sent.AdditionalProperties!["pin"]);
        Assert.Null(baseOptions.Tools); // the caller's options object is not changed
    }

    [Fact]
    public async Task Cancellation_StopsTheRun()
    {
        using var cts = new CancellationTokenSource();
        var slow = new LambdaTool("slow", _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return ToolResult.Ok("never");
        });
        var chat = new ScriptedChatClient(Calls(Call("c1", "slow")), Text("never"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new AgentLoop(chat, new ToolRegistry().Add(slow)).RunAsync("hi", cancellationToken: cts.Token));
        Assert.Single(chat.Requests);
    }
}
