using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using ResearchScout.Harness;

namespace ResearchScout.Papers.Search;

/// <summary>
/// Tells the embedder whether text is a search query or a document. Voyage embeds them differently,
/// which improves retrieval. Pass via <see cref="EmbeddingGenerationOptions.AdditionalProperties"/>.
/// </summary>
public static class EmbeddingInputType
{
    public const string Key = "input_type";
    public const string Query = "query";
    public const string Document = "document";

    public static EmbeddingGenerationOptions Options(string inputType) =>
        new() { AdditionalProperties = new() { [Key] = inputType } };
}

/// <summary>
/// Voyage AI embeddings (https://docs.voyageai.com/reference/embeddings-api) as a standard Microsoft.Extensions.AI
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>. Voyage has no official .NET SDK, so this is a small typed client;
/// any other IEmbeddingGenerator (Gemini, OpenAI) can replace it without touching the search code.
/// </summary>
public sealed class VoyageEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public static readonly Uri DefaultEndpoint = new("https://api.voyageai.com/v1/embeddings");

    /// <summary>Texts per request. Voyage allows up to 1,000; smaller batches keep each request well under its token limit.</summary>
    public const int BatchSize = 128;

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly RetryPolicy _retry;
    private readonly bool _freeTier;

    /// <param name="freeTier">
    /// Voyage accounts without a payment method are limited to 3 requests and 10K tokens per minute.
    /// When true, batches are kept small, requests are paced to stay under those limits, and a 429 waits a full minute.
    /// Null reads the VOYAGE_FREE_TIER environment variable ("true" or "1").
    /// </param>
    public VoyageEmbeddingGenerator(HttpClient http, string apiKey, string model = "voyage-4-lite", int dimensions = 1024, RetryPolicy? retry = null, bool? freeTier = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _http = http;
        _apiKey = apiKey;
        _model = model;
        _dimensions = dimensions;

        var useFreeTier = freeTier ?? Environment.GetEnvironmentVariable("VOYAGE_FREE_TIER") is "true" or "1" or "True" or "TRUE";
        if (useFreeTier)
        {
            _freeTier = true;
            _retry = retry ?? new RetryPolicy { MaxAttempts = 5, BaseDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(65) };
        }
        else
        {
            _retry = retry ?? new RetryPolicy();
        }

        Metadata = new EmbeddingGeneratorMetadata("voyage", DefaultEndpoint, model, dimensions);
    }

    public EmbeddingGeneratorMetadata Metadata { get; }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var inputType = options?.AdditionalProperties?.GetValueOrDefault(EmbeddingInputType.Key) as string ?? EmbeddingInputType.Document;
        var all = values.ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>(all.Count) { Usage = new UsageDetails { InputTokenCount = 0 } };

        // One request per batch (never one request per text). Free tier: smaller batches, sized by tokens.
        var batches = !_freeTier
            ? all.Chunk(BatchSize)
            : VoyageRateLimiter.SplitByTokens(all, VoyageRateLimiter.MaxTokensPerRequest);

        foreach (var batch in batches)
        {
            var (response, _) = await _retry.ExecuteAsync(
                async ct =>
                {
                    if (_freeTier)
                    {
                        await VoyageRateLimiter.Shared.WaitForTurnAsync(VoyageRateLimiter.EstimateTokens(batch), ct).ConfigureAwait(false);
                    }

                    return await PostAsync(batch, inputType, ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var item in response.Data.OrderBy(d => d.Index))
            {
                result.Add(new Embedding<float>(item.Embedding) { ModelId = _model });
            }

            result.Usage!.InputTokenCount += response.Usage?.TotalTokens ?? 0;
        }

        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(Metadata) ? Metadata
            : serviceKey is null && serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    public void Dispose()
    {
    }

    private async Task<VoyageResponse> PostAsync(string[] batch, string inputType, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DefaultEndpoint)
        {
            Content = JsonContent.Create(new VoyageRequest(batch, _model, inputType, _dimensions)),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Voyage returned {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<VoyageResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Empty response from Voyage.");
    }

    private sealed record VoyageRequest(
        [property: JsonPropertyName("input")] string[] Input,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input_type")] string InputType,
        [property: JsonPropertyName("output_dimension")] int OutputDimension);

    private sealed record VoyageResponse(
        [property: JsonPropertyName("data")] List<VoyageItem> Data,
        [property: JsonPropertyName("usage")] VoyageUsage? Usage);

    private sealed record VoyageItem(
        [property: JsonPropertyName("embedding")] float[] Embedding,
        [property: JsonPropertyName("index")] int Index);

    private sealed record VoyageUsage([property: JsonPropertyName("total_tokens")] int TotalTokens);
}

/// <summary>
/// Keeps Voyage calls under the free-tier limits (3 requests and 10K tokens per rolling minute), with a safety margin.
/// One shared instance per process, so parallel callers share the same budget.
/// </summary>
internal sealed class VoyageRateLimiter : IDisposable
{
    public static readonly VoyageRateLimiter Shared = new();

    public const int RequestsPerMinute = 3;
    public const int TokensPerMinute = 7_000; // Voyage's limit is 10K; keep a wide margin because our count is an estimate.
    public const int MaxTokensPerRequest = 3_000; // Small requests: text with code or math turns into more tokens than plain English.

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(62);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<(DateTimeOffset At, int Tokens)> _recent = new();

    /// <summary>Rough token count: 1 token per 3 characters, rounded up. Plain English is nearer 4; code and math are nearer 2-3.</summary>
    public static int EstimateTokens(IEnumerable<string> texts) => texts.Sum(t => (t.Length / 3) + 1);

    /// <summary>Groups texts into batches that each stay under <paramref name="maxTokens"/> (a single huge text still gets its own batch).</summary>
    public static IEnumerable<string[]> SplitByTokens(IReadOnlyList<string> texts, int maxTokens)
    {
        var batch = new List<string>();
        var tokens = 0;
        foreach (var text in texts)
        {
            var t = EstimateTokens([text]);
            if (batch.Count > 0 && (tokens + t > maxTokens || batch.Count >= VoyageEmbeddingGenerator.BatchSize))
            {
                yield return batch.ToArray();
                batch.Clear();
                tokens = 0;
            }

            batch.Add(text);
            tokens += t;
        }

        if (batch.Count > 0)
        {
            yield return batch.ToArray();
        }
    }

    /// <summary>Waits until sending a request of <paramref name="tokens"/> keeps the last minute under both limits, then records it.</summary>
    public async Task WaitForTurnAsync(int tokens, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = DateTimeOffset.UtcNow;
                while (_recent.Count > 0 && now - _recent.Peek().At > Window)
                {
                    _recent.Dequeue();
                }

                var usedTokens = _recent.Sum(r => r.Tokens);
                var fitsRequests = _recent.Count < RequestsPerMinute;
                var fitsTokens = _recent.Count == 0 || usedTokens + Math.Min(tokens, TokensPerMinute) <= TokensPerMinute;
                if (fitsRequests && fitsTokens)
                {
                    _recent.Enqueue((now, tokens));
                    return;
                }

                // Wait until the oldest request leaves the window, then check again.
                var wait = Window - (now - _recent.Peek().At) + TimeSpan.FromMilliseconds(250);
                var seconds = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                Console.Error.WriteLine("Voyage free tier: waiting " + seconds + "s to stay under the rate limit...");
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
