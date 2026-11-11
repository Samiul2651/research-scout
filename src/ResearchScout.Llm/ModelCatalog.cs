using Microsoft.Extensions.AI;

namespace ResearchScout.Llm;

/// <summary>
/// The models from config, filtered to providers that have an API key.
/// Answers "what can the dashboard offer?", "is this pick allowed?" and "what does this call cost?".
/// </summary>
public sealed class ModelCatalog
{
    private readonly Dictionary<(string Provider, string ModelId), ModelOption> _available;

    public ModelCatalog(LlmOptions options, IReadOnlyDictionary<string, string> apiKeys)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(apiKeys);

        foreach (var model in options.Models)
        {
            if (!LlmProviders.All.Contains(model.Provider))
            {
                throw new InvalidOperationException(
                    $"Model '{model.ModelId}' has unknown provider '{model.Provider}'. Use one of: {string.Join(", ", LlmProviders.All)}.");
            }
        }

        _available = options.Models
            .Where(m => apiKeys.ContainsKey(m.Provider))
            .ToDictionary(m => (m.Provider, m.ModelId));

        Available = [.. _available.Values.OrderBy(m => m.Provider, StringComparer.Ordinal).ThenBy(m => m.ModelId, StringComparer.Ordinal)];

        var configuredDefault = new LlmSelection(options.DefaultProvider, options.DefaultModelId);
        Default = IsAvailable(configuredDefault)
            ? configuredDefault
            : Available.Select(m => new LlmSelection(m.Provider, m.ModelId)).FirstOrDefault();
    }

    /// <summary>Models that can be used right now (their provider has a key).</summary>
    public IReadOnlyList<ModelOption> Available { get; }

    /// <summary>The configured default if available, else the first available model, else null (no keys at all).</summary>
    public LlmSelection? Default { get; }

    public bool IsAvailable(LlmSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return _available.ContainsKey((selection.Provider, selection.ModelId));
    }

    public ModelOption? Find(LlmSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return _available.GetValueOrDefault((selection.Provider, selection.ModelId));
    }

    /// <summary>
    /// Cost of one call in USD:
    /// input tokens × input price ÷ 1,000,000 + output tokens × output price ÷ 1,000,000.
    /// Returns 0 when usage is missing or the model is unknown.
    /// </summary>
    public decimal CostUsd(LlmSelection selection, UsageDetails? usage)
    {
        var model = Find(selection);
        if (model is null || usage is null)
        {
            return 0m;
        }

        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;
        return (input * model.InputUsdPerMillion / 1_000_000m) + (output * model.OutputUsdPerMillion / 1_000_000m);
    }
}
