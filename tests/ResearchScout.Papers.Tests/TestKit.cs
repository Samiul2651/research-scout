using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using ResearchScout.Papers.Search;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ResearchScout.Papers.Tests;

/// <summary>Answers HTTP requests from a route table (first matching URL substring wins) and counts calls.</summary>
internal sealed class RoutedHandler : HttpMessageHandler
{
    private readonly List<(string UrlPart, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<(string Method, string Url, string? Body)> Calls { get; } = [];

    public RoutedHandler On(string urlPart, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((urlPart, _ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        return this;
    }

    public RoutedHandler On(string urlPart, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((urlPart, respond));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request.Method.Method, url, body));

        var route = _routes.FirstOrDefault(r => url.Contains(r.UrlPart, StringComparison.Ordinal));
        return route.Respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
    }
}

/// <summary>
/// Deterministic fake embeddings: each word adds 1 to a slot chosen by its hash. Texts that share words get
/// similar vectors, which is enough to test search without calling Voyage.
/// </summary>
internal sealed class HashEmbedder(int dimensions = 64) : IEmbeddingGenerator<string, Embedding<float>>
{
    public int Calls { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var text in values)
        {
            var v = new float[dimensions];
            foreach (var word in Bm25Index.Tokenize(text))
            {
                v[(int)((uint)StableHash(word) % (uint)dimensions)] += 1;
            }

            result.Add(new Embedding<float>(v));
        }

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 23;
            foreach (var c in s)
            {
                h = (h * 31) + c;
            }

            return h;
        }
    }
}

internal static class TestPdf
{
    /// <summary>Builds a real PDF with one entry per page, each page's text written as lines.</summary>
    public static byte[] Build(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var pageText in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            var words = pageText.Split(' ');
            var y = 800.0;
            foreach (var line in words.Chunk(12))
            {
                page.AddText(string.Join(' ', line), 9, new PdfPoint(30, y), font);
                y -= 12;
            }
        }

        return builder.Build();
    }

    public static string Words(string word, int count) => string.Join(' ', Enumerable.Repeat(word, count));
}
