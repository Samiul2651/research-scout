using Microsoft.Extensions.AI;

namespace ResearchScout.Llm.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void OnlyOffersModelsWhoseProviderHasAKey()
    {
        var catalog = new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Gemini, LlmProviders.DeepSeek));

        Assert.Equal(["deepseek", "gemini"], catalog.Available.Select(m => m.Provider));
        Assert.False(catalog.IsAvailable(new LlmSelection(LlmProviders.Anthropic, "claude-haiku-5-5")));
    }

    [Fact]
    public void Default_IsConfiguredDefault_WhenAvailable()
    {
        var catalog = new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Anthropic, LlmProviders.Gemini));

        Assert.Equal(new LlmSelection(LlmProviders.Anthropic, "claude-haiku-5-5"), catalog.Default);
    }

    [Fact]
    public void Default_FallsBackToFirstAvailable_WhenConfiguredDefaultHasNoKey()
    {
        var catalog = new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Gemini));

        Assert.Equal(new LlmSelection(LlmProviders.Gemini, "gemini-3.5-flash-lite"), catalog.Default);
    }

    [Fact]
    public void Default_IsNull_WhenNoKeys()
    {
        var catalog = new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys());

        Assert.Null(catalog.Default);
        Assert.Empty(catalog.Available);
    }

    [Fact]
    public void UnknownProviderInConfig_Throws()
    {
        var options = TestHelpers.Options();
        options.Models.Add(new ModelOption { Provider = "mistral", ModelId = "x", InputUsdPerMillion = 1, OutputUsdPerMillion = 1 });

        Assert.Throws<InvalidOperationException>(() => new ModelCatalog(options, TestHelpers.Keys(LlmProviders.Anthropic)));
    }

    [Fact]
    public void CostUsd_UsesPerMillionPrices()
    {
        var catalog = new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Anthropic));
        var usage = new UsageDetails { InputTokenCount = 2_000, OutputTokenCount = 500 };

        var cost = catalog.CostUsd(new LlmSelection(LlmProviders.Anthropic, "claude-haiku-5-5"), usage);

        // input:  2,000 tokens × $1 per million = $0.002
        // output:   500 tokens × $5 per million = $0.0025
        // total = $0.0045
        Assert.Equal(0.0045m, cost);
    }

    [Fact]
    public void CostUsd_IsZero_ForUnknownModelOrMissingUsage()
    {
        var catalog = new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Anthropic));

        Assert.Equal(0m, catalog.CostUsd(new LlmSelection(LlmProviders.Anthropic, "nope"), new UsageDetails { InputTokenCount = 10 }));
        Assert.Equal(0m, catalog.CostUsd(new LlmSelection(LlmProviders.Anthropic, "claude-haiku-5-5"), null));
    }
}
