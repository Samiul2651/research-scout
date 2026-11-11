using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace ResearchScout.Harness.Tools;

/// <summary>
/// The set of tools one agent can use. Checks each tool when it is added, so a bad name or schema
/// fails at startup instead of as a provider error in the middle of a run.
/// </summary>
public sealed partial class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public IReadOnlyCollection<ITool> Tools => _tools.Values;

    /// <exception cref="ArgumentException">Bad name, duplicate name, or the schema is not an object schema.</exception>
    public ToolRegistry Add(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (!ValidName().IsMatch(tool.Name ?? string.Empty))
        {
            throw new ArgumentException(
                $"Tool name '{tool.Name}' is invalid. Use 1-64 letters, digits, '_' or '-'.", nameof(tool));
        }

        if (string.IsNullOrWhiteSpace(tool.Description))
        {
            throw new ArgumentException($"Tool '{tool.Name}' needs a description; the model relies on it.", nameof(tool));
        }

        if (tool.InputSchema.ValueKind != JsonValueKind.Object
            || !tool.InputSchema.TryGetProperty("type", out var type)
            || type.GetString() != "object")
        {
            throw new ArgumentException($"Tool '{tool.Name}' input schema must be a JSON object schema with \"type\": \"object\".", nameof(tool));
        }

        if (!_tools.TryAdd(tool.Name!, tool))
        {
            throw new ArgumentException($"A tool named '{tool.Name}' is already registered.", nameof(tool));
        }

        return this;
    }

    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);

    /// <summary>
    /// Tool declarations for <see cref="ChatOptions.Tools"/>. These are declarations only (name, description, schema):
    /// nothing in Microsoft.Extensions.AI can run them, so the harness loop stays in charge of every call.
    /// </summary>
    public IList<AITool> ToDeclarations() =>
        [.. _tools.Values.Select(t => AIFunctionFactory.CreateDeclaration(t.Name, t.Description, t.InputSchema))];

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ValidName();
}
