using System.Globalization;
using System.Text.Json;
using ResearchScout.Harness.Tools;

namespace ResearchScout.Cli;

/// <summary>
/// Two harmless tools for trying the agent loop before the real paper tools exist.
/// Ask something like "What is 17.5 times 3, and what time is it in UTC?" to see both used.
/// </summary>
internal static class DemoTools
{
    public static ToolRegistry Registry() => new ToolRegistry()
        .Add(new CurrentTimeTool())
        .Add(new CalculatorTool());

    private sealed class CurrentTimeTool : ITool
    {
        public string Name => "get_current_time";
        public string Description => "Returns the current date and time in UTC (ISO 8601).";
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;
        public bool NeedsApproval => false;

        public Task<ToolResult> RunAsync(JsonElement input, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Ok(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
    }

    private sealed class CalculatorTool : ITool
    {
        public string Name => "calculate";
        public string Description => "Does one arithmetic operation on two numbers: add, subtract, multiply or divide.";

        public JsonElement InputSchema { get; } = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "a": { "type": "number", "description": "First number" },
                "b": { "type": "number", "description": "Second number" },
                "op": { "type": "string", "enum": ["add", "subtract", "multiply", "divide"] }
              },
              "required": ["a", "b", "op"]
            }
            """).RootElement;

        public bool NeedsApproval => false;

        public Task<ToolResult> RunAsync(JsonElement input, CancellationToken cancellationToken)
        {
            if (!input.TryGetProperty("a", out var aEl) || !aEl.TryGetDecimal(out var a)
                || !input.TryGetProperty("b", out var bEl) || !bEl.TryGetDecimal(out var b)
                || !input.TryGetProperty("op", out var opEl))
            {
                return Task.FromResult(ToolResult.Error("Need numbers 'a' and 'b' and an 'op'."));
            }

            ToolResult result = opEl.GetString() switch
            {
                "add" => ToolResult.Ok((a + b).ToString(CultureInfo.InvariantCulture)),
                "subtract" => ToolResult.Ok((a - b).ToString(CultureInfo.InvariantCulture)),
                "multiply" => ToolResult.Ok((a * b).ToString(CultureInfo.InvariantCulture)),
                "divide" when b == 0 => ToolResult.Error("Cannot divide by zero."),
                "divide" => ToolResult.Ok((a / b).ToString(CultureInfo.InvariantCulture)),
                var op => ToolResult.Error($"Unknown op '{op}'. Use add, subtract, multiply or divide."),
            };

            return Task.FromResult(result);
        }
    }
}
