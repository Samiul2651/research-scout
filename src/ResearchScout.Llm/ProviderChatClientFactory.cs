using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using OpenAI;

namespace ResearchScout.Llm;

/// <summary>Builds one provider SDK client for a provider + model. Swappable in tests.</summary>
public interface IChatClientFactory
{
    IChatClient Create(LlmSelection selection);
}

/// <summary>
/// Builds <see cref="IChatClient"/>s from the official provider SDKs.
///
/// Why each provider uses the SDK it does:
/// <list type="bullet">
/// <item>Claude: official <c>Anthropic</c> SDK (has its own IChatClient adapter).</item>
/// <item>Gemini: official <c>Google.GenAI</c> SDK. Its adapter keeps Gemini 3 thought signatures, which tool calls require.</item>
/// <item>DeepSeek: the <c>Anthropic</c> SDK pointed at DeepSeek's Anthropic-format URL. The OpenAI adapter in
/// Microsoft.Extensions.AI drops DeepSeek's <c>reasoning_content</c> on the next request, which makes
/// thinking-mode tool calls fail with HTTP 400.</item>
/// <item>OpenAI: <c>Microsoft.Extensions.AI.OpenAI</c>.</item>
/// </list>
///
/// SDK auto-retries are turned off where the SDK allows it: the harness does its own retries (T5),
/// so SDK retries would multiply the attempts and the cost.
/// </summary>
public sealed class ProviderChatClientFactory(LlmOptions options, IReadOnlyDictionary<string, string> apiKeys) : IChatClientFactory
{
    /// <summary>DeepSeek's Anthropic-compatible endpoint.</summary>
    public const string DeepSeekAnthropicBaseUrl = "https://api.deepseek.com/anthropic";

    public IChatClient Create(LlmSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (!apiKeys.TryGetValue(selection.Provider, out var apiKey))
        {
            throw new InvalidOperationException(
                $"No API key for provider '{selection.Provider}'. Set {LlmProviders.ApiKeyEnvVar(selection.Provider)}.");
        }

        var baseUrl = options.BaseUrls.TryGetValue(selection.Provider, out var url) && !string.IsNullOrWhiteSpace(url) ? url : null;

        return selection.Provider switch
        {
            LlmProviders.Anthropic => CreateAnthropic(apiKey, baseUrl, selection.ModelId),
            LlmProviders.DeepSeek => CreateAnthropic(apiKey, baseUrl ?? DeepSeekAnthropicBaseUrl, selection.ModelId),
            LlmProviders.Gemini => CreateGemini(apiKey, baseUrl, selection.ModelId),
            LlmProviders.OpenAi => CreateOpenAi(apiKey, baseUrl, selection.ModelId),
            _ => throw new ArgumentException($"Unknown provider '{selection.Provider}'.", nameof(selection)),
        };
    }

    private IChatClient CreateAnthropic(string apiKey, string? baseUrl, string modelId)
    {
        var clientOptions = new ClientOptions
        {
            ApiKey = apiKey,
            Timeout = options.RequestTimeout,
            MaxRetries = 0,
        };

        if (baseUrl is not null)
        {
            clientOptions.BaseUrl = baseUrl;
        }

        return new AnthropicClient(clientOptions).AsIChatClient(modelId);
    }

    private IChatClient CreateGemini(string apiKey, string? baseUrl, string modelId)
    {
        var httpOptions = new Google.GenAI.Types.HttpOptions
        {
            Timeout = (int)options.RequestTimeout.TotalMilliseconds,
            RetryOptions = new Google.GenAI.Types.HttpRetryOptions { Attempts = 1 },
        };

        if (baseUrl is not null)
        {
            httpOptions.BaseUrl = baseUrl;
        }

        return new Google.GenAI.Client(apiKey: apiKey, httpOptions: httpOptions).AsIChatClient(modelId);
    }

    private IChatClient CreateOpenAi(string apiKey, string? baseUrl, string modelId)
    {
        var clientOptions = new OpenAIClientOptions
        {
            NetworkTimeout = options.RequestTimeout,
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };

        if (baseUrl is not null)
        {
            clientOptions.Endpoint = new Uri(baseUrl);
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions).GetChatClient(modelId).AsIChatClient();
    }
}
