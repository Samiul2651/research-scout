using System.Net;
using System.Text.Json;
using ResearchScout.Harness;
using ResearchScout.Infra.Tests;
using ResearchScout.Papers.Fetching;
using ResearchScout.Papers.Search;

namespace ResearchScout.Papers.Tests;

public class SemanticScholarClientTests
{
    private const string PaperJson = """
        {
          "paperId": "abc123", "title": "RAG for Code", "year": 2025, "abstract": "We study retrieval.",
          "venue": "ICSE", "url": "https://www.semanticscholar.org/paper/abc123",
          "authors": [{"authorId": "1", "name": "Ada Lovelace"}, {"authorId": "2", "name": "Alan Turing"}],
          "externalIds": {"ArXiv": "2501.00001", "DOI": "10.1/x"},
          "openAccessPdf": null
        }
        """;

    private static readonly RetryPolicy NoWait = new() { MaxAttempts = 3, Delay = (_, _) => Task.CompletedTask };

    private static SemanticScholarClient Client(RoutedHandler handler, IApiCache? cache = null) =>
        new(new HttpClient(handler), cache ?? new InMemoryApiCache(), retry: NoWait);

    [Fact]
    public async Task GetPaper_ParsesFields_AndBuildsArxivPdfLink()
    {
        var handler = new RoutedHandler().On("paper/arXiv%3A2501.00001", PaperJson);

        var paper = await Client(handler).GetPaperAsync("arXiv:2501.00001", default);

        Assert.NotNull(paper);
        Assert.Equal("RAG for Code", paper.Title);
        Assert.Equal(["Ada Lovelace", "Alan Turing"], paper.AuthorNames);
        Assert.Equal("arxiv:2501.00001", paper.StableId);
        Assert.Equal("https://arxiv.org/pdf/2501.00001", paper.PdfUrl);
    }

    [Fact]
    public async Task GetPaper_NotFound_ReturnsNull()
    {
        var paper = await Client(new RoutedHandler()).GetPaperAsync("nope", default);

        Assert.Null(paper);
    }

    [Fact]
    public async Task SecondCall_ComesFromCache()
    {
        var handler = new RoutedHandler().On("paper/abc123", PaperJson);
        var client = Client(handler);

        await client.GetPaperAsync("abc123", default);
        await client.GetPaperAsync("abc123", default);

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task RateLimit_IsRetried()
    {
        var calls = 0;
        var handler = new RoutedHandler().On("paper/abc123", _ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PaperJson) });

        var paper = await Client(handler).GetPaperAsync("abc123", default);

        Assert.Equal("RAG for Code", paper?.Title);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AuthorPapers_KeepsOnlyRecentYears_NewestFirst()
    {
        var json = """
            {"data": [
              {"paperId": "a", "title": "Old", "year": 2019},
              {"paperId": "b", "title": "Mid", "year": 2025},
              {"paperId": "c", "title": "New", "year": 2026}
            ]}
            """;
        var handler = new RoutedHandler().On("author/42/papers", json);

        var papers = await Client(handler).GetAuthorPapersAsync("42", fromYear: 2024, default);

        Assert.Equal(["New", "Mid"], papers.Select(p => p.Title));
    }

    [Fact]
    public async Task AuthorPapers_ReadsEveryPage_SoRecentPapersOnLaterPagesAreFound()
    {
        // Page 1 (offset 0) has only old papers and says there is more ("next"); page 2 has this year's paper.
        var page1 = "{\"offset\": 0, \"next\": 100, \"data\": [" +
            string.Join(",", Enumerable.Range(0, 100).Select(i => $"{{\"paperId\": \"old{i}\", \"title\": \"Old {i}\", \"year\": 2012}}")) + "]}";
        var page2 = """{"offset": 100, "data": [{"paperId": "new1", "title": "Brand new", "year": 2026}]}""";
        var handler = new RoutedHandler().On("offset=100", page2).On("offset=0", page1);

        var papers = await Client(handler).GetAuthorPapersAsync("99", fromYear: 2024, default);

        Assert.Equal("Brand new", Assert.Single(papers).Title);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task GetAuthors_UsesOneBatchRequest()
    {
        var handler = new RoutedHandler().On("author/batch", """
            [{"authorId": "1", "name": "Ada", "affiliations": ["Memorial University"], "hIndex": 20},
             null,
             {"authorId": "3", "name": "Bob", "affiliations": []}]
            """);

        var authors = await Client(handler).GetAuthorsAsync(["1", "2", "3"], default);

        Assert.Single(handler.Calls);
        Assert.Equal("POST", handler.Calls[0].Method);
        Assert.Equal(["Ada", "Bob"], authors.Select(a => a.Name)); // null entry (unknown id) skipped
        Assert.Equal(["Memorial University"], authors[0].Affiliations);
    }

    [Fact]
    public void FromYear_LastThreeYears_IncludesThisYear()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2024, SemanticScholarClient.FromYear(3, clock)); // 2024, 2025, 2026
    }
}

public class VoyageEmbeddingGeneratorTests
{
    private static string Reply(int count) => JsonSerializer.Serialize(new
    {
        data = Enumerable.Range(0, count).Reverse().Select(i => new { embedding = new[] { (float)i, 1f }, index = i }), // out of order on purpose
        usage = new { total_tokens = count * 10 },
    });

    [Fact]
    public async Task SendsBatches_KeepsOrder_SetsInputType()
    {
        var handler = new RoutedHandler().On("embeddings", req =>
        {
            var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply(body.GetProperty("input").GetArrayLength())) };
        });
        using var voyage = new VoyageEmbeddingGenerator(new HttpClient(handler), "key", dimensions: 2);

        var texts = Enumerable.Range(0, 300).Select(i => $"t{i}").ToList();
        var result = await voyage.GenerateAsync(texts, EmbeddingInputType.Options(EmbeddingInputType.Query));

        // 300 texts / 128 per batch → 3 requests (128 + 128 + 44).
        Assert.Equal(3, handler.Calls.Count);
        Assert.Equal(300, result.Count);
        Assert.Equal(0f, result[0].Vector.Span[0]);  // sorted back by index
        Assert.Equal(1f, result[1].Vector.Span[0]);
        Assert.Equal(3000, result.Usage!.InputTokenCount); // 300 × 10

        var firstBody = JsonDocument.Parse(handler.Calls[0].Body!).RootElement;
        Assert.Equal("query", firstBody.GetProperty("input_type").GetString());
        Assert.Equal(2, firstBody.GetProperty("output_dimension").GetInt32());
    }
}
