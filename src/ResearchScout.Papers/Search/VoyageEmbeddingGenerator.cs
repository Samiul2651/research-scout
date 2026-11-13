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

    public VoyageEmbeddingGenerator(HttpClient http, string apiKey, string model = "voyage-4-lite", int dimensions = 1024, RetryPolicy? retry = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _http = http;
        _apiKey = apiKey;
        _model = model;
        _dimensions = dimensions;
        _retry = retry ?? new RetryPolicy();
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

        // One request per batch (never one request per text).
        foreach (var batch in all.Chunk(BatchSize))
        {
            var (response, _) = await _retry.ExecuteAsync(ct => PostAsync(batch, inputType, ct), cancellationToken).ConfigureAwait(false);

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
