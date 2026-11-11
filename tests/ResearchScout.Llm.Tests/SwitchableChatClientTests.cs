using Microsoft.Extensions.AI;

namespace ResearchScout.Llm.Tests;

public class SwitchableChatClientTests
{
    private static readonly LlmSelection Claude = new(LlmProviders.Anthropic, "claude-haiku-5-5");
    private static readonly LlmSelection Gemini = new(LlmProviders.Gemini, "gemini-3.5-flash-lite");
    private static readonly ChatMessage[] Hi = [new(ChatRole.User, "hi")];

    private static (SwitchableChatClient Client, LlmSelector Selector, FakeChatClientFactory Factory) Create()
    {
        var catalog = new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Anthropic, LlmProviders.Gemini));
        var selector = new LlmSelector(catalog, new InMemoryLlmSettingsStore());
        var factory = new FakeChatClientFactory();
        return (new SwitchableChatClient(selector, factory), selector, factory);
    }

    [Fact]
    public async Task Switch_TakesEffectOnNextCall()
    {
        var (client, selector, _) = Create();

        var first = await client.GetResponseAsync(Hi);
        await selector.TrySetAsync(Gemini);
        var second = await client.GetResponseAsync(Hi);

        Assert.Equal(Claude.ToString(), first.Text);
        Assert.Equal(Gemini.ToString(), second.Text);
    }

    [Fact]
    public async Task Response_IsStampedWithProviderAndModel()
    {
        var (client, _, _) = Create();

        var response = await client.GetResponseAsync(Hi);

        Assert.Equal(LlmProviders.Anthropic, response.AdditionalProperties![SwitchableChatClient.ProviderKey]);
        Assert.Equal("claude-haiku-5-5", response.AdditionalProperties[SwitchableChatClient.ModelKey]);
    }

    [Fact]
    public async Task PinnedSelection_WinsOverCurrentPick()
    {
        // A run that started on Claude must stay on Claude even if the admin switches mid-run.
        var (client, selector, _) = Create();
        var pinned = new ChatOptions { AdditionalProperties = new() { [SwitchableChatClient.PinnedSelectionKey] = Claude } };

        await selector.TrySetAsync(Gemini);
        var response = await client.GetResponseAsync(Hi, pinned);

        Assert.Equal(Claude.ToString(), response.Text);
    }

    [Fact]
    public async Task ProviderClient_IsCreatedOnceAndReused()
    {
        var (client, _, factory) = Create();

        await client.GetResponseAsync(Hi);
        await client.GetResponseAsync(Hi);
        await client.GetResponseAsync(Hi);

        var created = Assert.Single(factory.Created);
        Assert.Equal(3, created.Calls);
    }

    [Fact]
    public async Task Streaming_UsesCurrentPickAndStampsUpdates()
    {
        var (client, selector, _) = Create();
        await selector.TrySetAsync(Gemini);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(Hi))
        {
            updates.Add(update);
        }

        var only = Assert.Single(updates);
        Assert.Equal(Gemini.ToString(), only.Text);
        Assert.Equal(LlmProviders.Gemini, only.AdditionalProperties![SwitchableChatClient.ProviderKey]);
    }

    [Fact]
    public async Task Dispose_DisposesProviderClients_AndBlocksFurtherCalls()
    {
        var (client, _, factory) = Create();
        await client.GetResponseAsync(Hi);

        client.Dispose();

        Assert.True(factory.Created.Single().Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetResponseAsync(Hi));
    }
}
