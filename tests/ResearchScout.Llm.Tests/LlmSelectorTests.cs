namespace ResearchScout.Llm.Tests;

public class LlmSelectorTests
{
    private static readonly LlmSelection Claude = new(LlmProviders.Anthropic, "claude-haiku-5-5");
    private static readonly LlmSelection Gemini = new(LlmProviders.Gemini, "gemini-3.5-flash-lite");

    [Fact]
    public async Task NothingSaved_ReturnsDefault()
    {
        var selector = new LlmSelector(new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Anthropic)), new InMemoryLlmSettingsStore());

        Assert.Equal(Claude, await selector.GetCurrentAsync());
    }

    [Fact]
    public async Task TrySet_AvailableModel_IsSavedAndReturned()
    {
        var selector = new LlmSelector(new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Anthropic, LlmProviders.Gemini)), new InMemoryLlmSettingsStore());

        Assert.True(await selector.TrySetAsync(Gemini));
        Assert.Equal(Gemini, await selector.GetCurrentAsync());
    }

    [Fact]
    public async Task TrySet_ProviderWithoutKey_IsRejected()
    {
        var store = new InMemoryLlmSettingsStore();
        var selector = new LlmSelector(new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Anthropic)), store);

        Assert.False(await selector.TrySetAsync(Gemini));
        Assert.Null(await store.GetAsync());
    }

    [Fact]
    public async Task TrySet_ModelNotInConfig_IsRejected()
    {
        var selector = new LlmSelector(new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Anthropic)), new InMemoryLlmSettingsStore());

        Assert.False(await selector.TrySetAsync(new LlmSelection(LlmProviders.Anthropic, "made-up-model")));
    }

    [Fact]
    public async Task SavedPickNoLongerAvailable_FallsBackToDefault()
    {
        // Saved Gemini earlier, but the Gemini key has since been removed.
        var store = new InMemoryLlmSettingsStore();
        await store.SetAsync(Gemini);
        var selector = new LlmSelector(new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Anthropic)), store);

        Assert.Equal(Claude, await selector.GetCurrentAsync());
    }

    [Fact]
    public async Task NoKeysAtAll_Throws()
    {
        var selector = new LlmSelector(new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys()), new InMemoryLlmSettingsStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() => selector.GetCurrentAsync());
    }
}
