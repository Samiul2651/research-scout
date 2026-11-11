using Microsoft.Extensions.AI;

namespace ResearchScout.Llm.Tests;

/// <summary>Builds real SDK clients but never calls the network.</summary>
public class ProviderChatClientFactoryTests
{
    private static ProviderChatClientFactory Factory(params string[] providersWithKeys) =>
        new(TestHelpers.Options(), TestHelpers.Keys(providersWithKeys));

    [Theory]
    [InlineData(LlmProviders.Anthropic, "claude-haiku-5-5")]
    [InlineData(LlmProviders.Gemini, "gemini-3.5-flash-lite")]
    [InlineData(LlmProviders.DeepSeek, "deepseek-flash")]
    [InlineData(LlmProviders.OpenAi, "some-openai-model")]
    public void CreatesClientForEachProvider(string provider, string model)
    {
        using var client = Factory(LlmProviders.All.ToArray()).Create(new LlmSelection(provider, model));

        var metadata = client.GetService<ChatClientMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal(model, metadata.DefaultModelId);
    }

    [Fact]
    public void DeepSeek_UsesDeepSeekAnthropicEndpoint()
    {
        using var client = Factory(LlmProviders.DeepSeek).Create(new LlmSelection(LlmProviders.DeepSeek, "deepseek-flash"));

        var metadata = client.GetService<ChatClientMetadata>();
        Assert.Equal("api.deepseek.com", metadata?.ProviderUri?.Host);
    }

    [Fact]
    public void MissingKey_Throws_AndNamesTheEnvVar()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Factory(LlmProviders.Anthropic).Create(new LlmSelection(LlmProviders.Gemini, "gemini-3.5-flash-lite")));

        Assert.Contains("GEMINI_API_KEY", ex.Message, StringComparison.Ordinal);
    }
}
