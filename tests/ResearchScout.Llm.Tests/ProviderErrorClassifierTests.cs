using System.Net;
using Anthropic.Exceptions;
using Anthropic.Models;
using Microsoft.Extensions.AI;

namespace ResearchScout.Llm.Tests;

public class ProviderErrorClassifierTests
{
    private static readonly HttpRequestException Inner = new("http");

    [Fact]
    public void Anthropic_RateLimit_IsTransient() =>
        Assert.True(ProviderErrorClassifier.IsTransient(
            new AnthropicRateLimitException(Inner) { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "" }));

    [Fact]
    public void Anthropic_BadRequest_IsNotTransient() =>
        Assert.False(ProviderErrorClassifier.IsTransient(
            new AnthropicBadRequestException(Inner) { StatusCode = HttpStatusCode.BadRequest, ResponseBody = "" }));

    [Fact]
    public void Anthropic_SpendLimit_IsNotTransient_EvenThoughItIsA429() =>
        Assert.False(ProviderErrorClassifier.IsTransient(
            new AnthropicEnforcedSpendLimitReachedException(Inner) { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "" }));

    [Fact]
    public void Anthropic_NetworkError_IsTransient() =>
        Assert.True(ProviderErrorClassifier.IsTransient(new AnthropicIOException("reset", Inner)));

    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(403, false)]
    public void Gemini_UsesStatusCode(int status, bool expected) =>
        Assert.Equal(expected, ProviderErrorClassifier.IsTransient(new Google.GenAI.ApiException("x", status, "s", null!)));

    [Fact]
    public void Unknown_FallsBackToHarnessDefault()
    {
        Assert.True(ProviderErrorClassifier.IsTransient(new TimeoutException()));
        Assert.False(ProviderErrorClassifier.IsTransient(new InvalidOperationException()));
    }
}

public class CatalogCostCalculatorTests
{
    [Fact]
    public void PricesStampedResponse()
    {
        var calculator = new CatalogCostCalculator(new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Gemini)));
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))
        {
            Usage = new UsageDetails { InputTokenCount = 1_000_000, OutputTokenCount = 100_000 },
            AdditionalProperties = new()
            {
                [SwitchableChatClient.ProviderKey] = LlmProviders.Gemini,
                [SwitchableChatClient.ModelKey] = "gemini-3.5-flash-lite",
            },
        };

        // input:  1,000,000 × $0.30 / 1M = $0.30
        // output:   100,000 × $2.50 / 1M = $0.25
        Assert.Equal(0.55m, calculator.CostUsd(response));
    }

    [Fact]
    public void UnstampedResponse_CostsZero()
    {
        var calculator = new CatalogCostCalculator(new ModelCatalog(TestHelpers.Options(), TestHelpers.Keys(LlmProviders.Gemini)));

        Assert.Equal(0m, calculator.CostUsd(new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))));
    }
}
