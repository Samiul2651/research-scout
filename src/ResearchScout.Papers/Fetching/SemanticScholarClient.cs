using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ResearchScout.Harness;

namespace ResearchScout.Papers.Fetching;

/// <summary>
/// Semantic Scholar Graph API (https://api.semanticscholar.org/api-docs/graph). Free; an API key
/// (SEMANTIC_SCHOLAR_API_KEY) raises the rate limit but is optional. Responses are cached.
/// Semantic Scholar has no official .NET SDK, so this is a small typed HttpClient.
/// </summary>
public sealed class SemanticScholarClient
{
    public static readonly Uri DefaultBaseUrl = new("https://api.semanticscholar.org/graph/v1/");

    private const string AuthorFields = "name,affiliations,hIndex,paperCount,url";
    private const string PaperFields = "title,year,abstract,venue,authors,externalIds,openAccessPdf,url";
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(7);

    /// <summary>Longest wait taken from a 429's Retry-After header before the retry policy's own wait.</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly IApiCache _cache;
    private readonly RetryPolicy _retry;
    private readonly TimeSpan _requestGap;
    private readonly Lock _paceLock = new();
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

    /// <param name="requestGap">
    /// Smallest time between two requests sent to Semantic Scholar (cached answers don't count). Without an API key all
    /// callers share one small pool, so bursts get 429s; ScoutServices passes about 1 second. Default: no spacing.
    /// </param>
    public SemanticScholarClient(HttpClient http, IApiCache cache, string? apiKey = null, RetryPolicy? retry = null, TimeSpan? requestGap = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _http.BaseAddress ??= DefaultBaseUrl;
        if (!string.IsNullOrWhiteSpace(apiKey) && !_http.DefaultRequestHeaders.Contains("x-api-key"))
        {
            _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        }

        _cache = cache;

        // Unauthenticated callers share one small pool, so 429s come in bursts: wait longer and try more often.
        // Without a key: about 3s, 6s, 12s, 24s, 30s between 6 tries. With a key: about 2s, 4s, 8s between 4 tries.
        var hasKey = !string.IsNullOrWhiteSpace(apiKey);
        _retry = retry ?? (hasKey
            ? new RetryPolicy { MaxAttempts = 4, BaseDelay = TimeSpan.FromSeconds(2) }
            : new RetryPolicy { MaxAttempts = 6, BaseDelay = TimeSpan.FromSeconds(3), MaxDelay = TimeSpan.FromSeconds(30) });
        _requestGap = requestGap ?? TimeSpan.Zero;
    }

    /// <summary>Authors whose name matches.</summary>
    public async Task<IReadOnlyList<AuthorInfo>> SearchAuthorsAsync(string name, int limit, CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync(
            $"author/search?query={Uri.EscapeDataString(name)}&limit={limit}&fields={AuthorFields}", cancellationToken).ConfigureAwait(false);
        return [.. Data(json).Select(ParseAuthor)];
    }

    /// <summary>Papers matching a topic, newest-first filter by <paramref name="fromYear"/>.</summary>
    public async Task<IReadOnlyList<PaperInfo>> SearchPapersAsync(string query, int fromYear, int limit, CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync(
            $"paper/search?query={Uri.EscapeDataString(query)}&year={fromYear}-&limit={limit}&fields={PaperFields}",
            cancellationToken).ConfigureAwait(false);
        return [.. Data(json).Select(ParsePaper)];
    }

    /// <summary>Paper authors' ids from a paper search (needed to look up affiliations).</summary>
    public async Task<IReadOnlyList<(string AuthorId, string Name)>> SearchPaperAuthorsAsync(
        string query, int fromYear, int limit, CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync(
            $"paper/search?query={Uri.EscapeDataString(query)}&year={fromYear}-&limit={limit}&fields=authors",
            cancellationToken).ConfigureAwait(false);

        return [.. Data(json)
            .SelectMany(p => p.TryGetProperty("authors", out var a) ? a.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Select(a => (Id: Str(a, "authorId"), Name: Str(a, "name")))
            .Where(a => a.Id is not null && a.Name is not null)
            .Select(a => (a.Id!, a.Name!))
            .Distinct()];
    }

    /// <summary>Several authors in one request (POST author/batch).</summary>
    public async Task<IReadOnlyList<AuthorInfo>> GetAuthorsAsync(IReadOnlyList<string> authorIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorIds);
        if (authorIds.Count == 0)
        {
            return [];
        }

        var body = JsonSerializer.Serialize(new { ids = authorIds });
        var cacheKey = "s2:author/batch:" + string.Join(',', authorIds.Order(StringComparer.Ordinal));
        var json = await SendCachedAsync(cacheKey, () => new HttpRequestMessage(HttpMethod.Post, $"author/batch?fields={AuthorFields}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }, cancellationToken).ConfigureAwait(false);

        return [.. json.RootElement.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object).Select(ParseAuthor)];
    }

    /// <summary>Most papers read per author (10 pages of 100). Covers even very prolific researchers.</summary>
    public const int MaxAuthorPapers = 1000;

    /// <summary>
    /// An author's papers from <paramref name="fromYear"/> on, newest first. Reads every page of the author's
    /// paper list (100 per page, up to <see cref="MaxAuthorPapers"/>), because the API does not sort by year:
    /// stopping at the first page could miss recent papers of prolific authors.
    /// </summary>
    public async Task<IReadOnlyList<PaperInfo>> GetAuthorPapersAsync(string authorId, int fromYear, CancellationToken cancellationToken)
    {
        var all = new List<PaperInfo>();
        for (var offset = 0; offset < MaxAuthorPapers; offset += 100)
        {
            using var json = await GetJsonAsync(
                $"author/{Uri.EscapeDataString(authorId)}/papers?offset={offset}&limit=100&fields={PaperFields}", cancellationToken).ConfigureAwait(false);
            all.AddRange(Data(json).Select(ParsePaper));

            // "next" is only present when there are more papers.
            if (!json.RootElement.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.Number)
            {
                break;
            }
        }

        return [.. all.Where(p => p.Year >= fromYear).OrderByDescending(p => p.Year)];
    }

    /// <summary>
    /// One paper. Accepts a Semantic Scholar id or a prefixed id: "arXiv:2106.15928", "DOI:10.1145/...".
    /// Returns null if not found.
    /// </summary>
    public async Task<PaperInfo?> GetPaperAsync(string paperId, CancellationToken cancellationToken)
    {
        try
        {
            var json = await GetJsonAsync($"paper/{Uri.EscapeDataString(paperId)}?fields={PaperFields}", cancellationToken).ConfigureAwait(false);
            return ParsePaper(json.RootElement);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken cancellationToken) =>
        SendCachedAsync("s2:" + relativeUrl, () => new HttpRequestMessage(HttpMethod.Get, relativeUrl), cancellationToken);

    private async Task<JsonDocument> SendCachedAsync(string cacheKey, Func<HttpRequestMessage> makeRequest, CancellationToken cancellationToken)
    {
        var cached = await _cache.GetAsync(cacheKey, CacheAge, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return JsonDocument.Parse(cached);
        }

        var (text, _) = await _retry.ExecuteAsync(async ct =>
        {
            await PaceAsync(ct).ConfigureAwait(false);
            using var request = makeRequest();
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // If Semantic Scholar says how long to wait, wait that long (capped) before the retry policy's own wait.
                if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter?.Delta is { } retryAfter)
                {
                    await Task.Delay(retryAfter < MaxRetryAfter ? retryAfter : MaxRetryAfter, ct).ConfigureAwait(false);
                }

                throw new HttpRequestException($"Semantic Scholar returned {(int)response.StatusCode}: {Short(content)}", null, response.StatusCode);
            }

            return content;
        }, cancellationToken).ConfigureAwait(false);

        await _cache.SetAsync(cacheKey, text, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(text);
    }

    /// <summary>
    /// Reserves the next free time slot (at least <see cref="_requestGap"/> after the previous one) and waits for it.
    /// Parallel callers get consecutive slots, so requests never come closer together than the gap.
    /// </summary>
    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        if (_requestGap <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan wait;
        lock (_paceLock)
        {
            var now = DateTimeOffset.UtcNow;
            var slot = _nextRequestAt > now ? _nextRequestAt : now;
            _nextRequestAt = slot + _requestGap;
            wait = slot - now;
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private static JsonElement[] Data(JsonDocument json) =>
        json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? [.. data.EnumerateArray()]
            : [];

    private static AuthorInfo ParseAuthor(JsonElement a) => new(
        Str(a, "authorId") ?? "",
        Str(a, "name") ?? "",
        a.TryGetProperty("affiliations", out var aff) && aff.ValueKind == JsonValueKind.Array
            ? [.. aff.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0)]
            : [],
        Int(a, "hIndex"),
        Int(a, "paperCount"),
        Str(a, "url"));

    private static PaperInfo ParsePaper(JsonElement p)
    {
        string? arxiv = null;
        if (p.TryGetProperty("externalIds", out var ids) && ids.ValueKind == JsonValueKind.Object)
        {
            arxiv = Str(ids, "ArXiv");
        }

        // Prefer arXiv's own PDF: publisher "open access" links often block scripts (403) or fail TLS.
        // Fall back to the open-access link only for papers that are not on arXiv.
        string? pdf = null;
        if (arxiv is not null)
        {
            pdf = $"https://arxiv.org/pdf/{arxiv}";
        }
        else if (p.TryGetProperty("openAccessPdf", out var oa) && oa.ValueKind == JsonValueKind.Object)
        {
            pdf = Str(oa, "url");
        }

        return new PaperInfo(
            Str(p, "paperId") ?? "",
            Str(p, "title") ?? "(untitled)",
            Int(p, "year"),
            Str(p, "abstract"),
            Str(p, "venue"),
            p.TryGetProperty("authors", out var au) && au.ValueKind == JsonValueKind.Array
                ? [.. au.EnumerateArray().Select(x => Str(x, "name") ?? "").Where(x => x.Length > 0)]
                : [],
            arxiv,
            string.IsNullOrWhiteSpace(pdf) ? null : pdf,
            Str(p, "url"));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static string Short(string s) => s.Length > 300 ? s[..300] + "…" : s;

    /// <summary>Current year minus <paramref name="years"/> - 1, i.e. "last 3 years" = this year and the two before.</summary>
    public static int FromYear(int years, TimeProvider? clock = null) =>
        int.Parse((clock ?? TimeProvider.System).GetUtcNow().ToString("yyyy", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) - years + 1;
}
