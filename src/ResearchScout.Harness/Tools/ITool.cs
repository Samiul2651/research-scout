using System.Text.Json;

namespace ResearchScout.Harness.Tools;

/// <summary>
/// A tool the agent can call. Implementations live outside the harness (for example in ResearchScout.Scout).
/// </summary>
public interface ITool
{
    /// <summary>Name the model uses to call the tool. Letters, digits, '_' and '-' only, at most 64 characters.</summary>
    string Name { get; }

    /// <summary>Tells the model what the tool does and when to use it. The model reads this, so be specific.</summary>
    string Description { get; }

    /// <summary>JSON Schema for the input. Must be an object schema (<c>{"type":"object", ...}</c>).</summary>
    JsonElement InputSchema { get; }

    /// <summary>True if a human must approve each call before it runs (approval gate, T7).</summary>
    bool NeedsApproval { get; }

    /// <summary>
    /// Runs the tool. Return <see cref="ToolResult.Error"/> for expected failures (bad input, not found).
    /// Unexpected exceptions are caught by the loop and sent to the model as an error result.
    /// </summary>
    Task<ToolResult> RunAsync(JsonElement input, CancellationToken cancellationToken);
}

/// <summary>What a tool sends back to the model.</summary>
public sealed record ToolResult(string Content, bool IsError = false)
{
    public static ToolResult Ok(string content) => new(content);

    public static ToolResult Error(string message) => new(message, IsError: true);
}
