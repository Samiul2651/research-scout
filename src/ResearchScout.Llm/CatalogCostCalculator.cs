using Microsoft.Extensions.AI;
using ResearchScout.Harness;

namespace ResearchScout.Llm;

/// <summary>
/// Prices a response with <see cref="ModelCatalog"/>, using the provider/model that
/// <see cref="SwitchableChatClient"/> stamped on it. Returns 0 for responses without a stamp.
/// </summary>
public sealed class CatalogCostCalculator(ModelCatalog catalog) : ICostCalculator
{
    public decimal CostUsd(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var props = response.AdditionalProperties;
        if (props is null
            || !props.TryGetValue(SwitchableChatClient.ProviderKey, out var provider)
            || !props.TryGetValue(SwitchableChatClient.ModelKey, out var model)
            || provider is not string p
            || model is not string m)
        {
            return 0m;
        }

        return catalog.CostUsd(new LlmSelection(p, m), response.Usage);
    }
}
