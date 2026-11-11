using System.ClientModel;
using Anthropic.Exceptions;
using Anthropic.Models;
using ResearchScout.Harness;

namespace ResearchScout.Llm;

/// <summary>
/// Knows each provider SDK's exception types, so the harness <see cref="RetryPolicy"/> retries only temporary failures.
/// Retry: rate limits (429), timeouts (408), server errors (5xx, Anthropic 529 overloaded), network errors.
/// Don't retry: bad requests, auth errors, unknown model, and spend limits (retrying can't fix those).
/// </summary>
public static class ProviderErrorClassifier
{
    public static bool IsTransient(Exception exception) => exception switch
    {
        // A hard spending cap on the Anthropic account. It is a 429 subtype, but waiting won't help.
        AnthropicEnforcedSpendLimitReachedException => false,

        // Claude and DeepSeek (both via the Anthropic SDK)
        AnthropicApiException e => RetryPolicy.IsTransientStatus((int)e.StatusCode),
        AnthropicIOException => true,

        // Gemini
        Google.GenAI.ApiException e => RetryPolicy.IsTransientStatus(e.StatusCode),

        // OpenAI (System.ClientModel). Status 0 means no response arrived.
        ClientResultException e => e.Status == 0 || RetryPolicy.IsTransientStatus(e.Status),

        _ => RetryPolicy.DefaultIsTransient(exception),
    };

    /// <summary>The harness retry policy with this classifier plugged in.</summary>
    public static RetryPolicy CreateRetryPolicy() => new() { IsTransient = IsTransient };
}
