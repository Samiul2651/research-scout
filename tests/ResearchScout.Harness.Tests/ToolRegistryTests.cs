using System.Text.Json;
using Microsoft.Extensions.AI;
using ResearchScout.Harness.Tools;

namespace ResearchScout.Harness.Tests;

public class ToolRegistryTests
{
    [Fact]
    public void Add_And_TryGet()
    {
        var registry = new ToolRegistry().Add(LambdaTool.Echo("search_papers"));

        Assert.True(registry.TryGet("search_papers", out var tool));
        Assert.Equal("search_papers", tool.Name);
        Assert.False(registry.TryGet("nope", out _));
    }

    [Fact]
    public void DuplicateName_Throws()
    {
        var registry = new ToolRegistry().Add(LambdaTool.Echo("a"));

        Assert.Throws<ArgumentException>(() => registry.Add(LambdaTool.Echo("a")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("dots.not.allowed")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 chars
    public void InvalidName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry().Add(LambdaTool.Echo(name)));
    }

    [Fact]
    public void NonObjectSchema_Throws()
    {
        var tool = new SchemaTool(JsonDocument.Parse("""{"type":"string"}""").RootElement);

        Assert.Throws<ArgumentException>(() => new ToolRegistry().Add(tool));
    }

    [Fact]
    public void ToDeclarations_AreSchemaOnly_NotRunnable()
    {
        var declarations = new ToolRegistry().Add(LambdaTool.Echo("search_papers")).ToDeclarations();

        var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(declarations));
        Assert.Equal("search_papers", declaration.Name);
        Assert.Equal("object", declaration.JsonSchema.GetProperty("type").GetString());

        // Not an AIFunction, so FunctionInvokingChatClient (if anyone added it) could not run it behind our back.
        Assert.IsNotAssignableFrom<AIFunction>(declaration);
    }

    private sealed class SchemaTool(JsonElement schema) : ITool
    {
        public string Name => "schema_tool";
        public string Description => "x";
        public JsonElement InputSchema => schema;
        public bool NeedsApproval => false;
        public Task<ToolResult> RunAsync(JsonElement input, CancellationToken cancellationToken) => Task.FromResult(ToolResult.Ok(""));
    }
}
