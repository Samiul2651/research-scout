using System.Text.Json;
using System.Text.Json.Serialization;
using ResearchScout.Harness.Tools;

namespace ResearchScout.Scout.Tools;

/// <summary>Base for Scout tools: schema from a JSON string, typed input helpers, JSON output.</summary>
public abstract class ScoutTool : ITool
{
    protected static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private JsonElement? _schema;

    public abstract string Name { get; }

    public abstract string Description { get; }

    public virtual bool NeedsApproval => false;

    public JsonElement InputSchema => _schema ??= JsonDocument.Parse(SchemaJson).RootElement.Clone();

    /// <summary>The input JSON Schema as text.</summary>
    protected abstract string SchemaJson { get; }

    public abstract Task<ToolResult> RunAsync(JsonElement input, CancellationToken cancellationToken);

    protected static ToolResult Json(object value) => ToolResult.Ok(JsonSerializer.Serialize(value, OutputJson));

    protected static string? StrArg(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim() is { Length: > 0 } s ? s : null
            : null;

    protected static int IntArg(JsonElement input, string name, int fallback, int min, int max) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? Math.Clamp(i, min, max)
            : fallback;

    protected static List<string> StrListArg(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0)]
            : [];

    protected static string Clip(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";
}
