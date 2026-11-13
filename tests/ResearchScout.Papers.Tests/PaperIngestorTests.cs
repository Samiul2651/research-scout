using Microsoft.EntityFrameworkCore;
using ResearchScout.Infra.Tests;
using ResearchScout.Papers.Chunking;
using ResearchScout.Papers.Fetching;
using ResearchScout.Papers.Search;

namespace ResearchScout.Papers.Tests;

public class PaperIngestorTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private sealed class FakeSource(params (PaperInfo Info, byte[]? Pdf)[] papers) : IPaperSource
    {
        public int Downloads { get; private set; }

        public Task<PaperInfo?> GetPaperAsync(string paperId, CancellationToken cancellationToken) =>
            Task.FromResult<PaperInfo?>(papers.FirstOrDefault(p => p.Info.PaperId == paperId).Info);

        public Task<byte[]> DownloadPdfAsync(PaperInfo paper, CancellationToken cancellationToken)
        {
            Downloads++;
            return Task.FromResult(papers.First(p => p.Info.PaperId == paper.PaperId).Pdf ?? throw new InvalidDataException("not a pdf"));
        }
    }

    private static PaperInfo Info(string id, string? pdfUrl = "https://example.org/x.pdf") =>
        new(id, $"Title {id}", 2025, null, null, ["Ada"], ArxivId: id, PdfUrl: pdfUrl, Url: null);

    private (PaperIngestor Ingestor, InMemoryVectorStore Vectors, Bm25Index Bm25, HashEmbedder Embedder) Create(FakeSource source)
    {
        var vectors = new InMemoryVectorStore();
        var bm25 = new Bm25Index();
        var embedder = new HashEmbedder();
        return (new PaperIngestor(source, new PdfChunker(), embedder, vectors, bm25, db.Factory), vectors, bm25, embedder);
    }

    [PostgresFact]
    public async Task Ingest_StoresChunksVectorsAndKeywords_ThenSearchCitesThePage()
    {
        var pdf = TestPdf.Build(
            "Abstract we propose a retriever for code " + TestPdf.Words("background", 70),
            "Evaluation we measure mean reciprocal rank on CodeSearchNet " + TestPdf.Words("details", 70));
        var source = new FakeSource((Info("2501.11111"), pdf));
        var (ingestor, vectors, bm25, embedder) = Create(source);

        var result = await ingestor.IngestAsync("2501.11111", default);

        Assert.True(result.Success, result.Error);
        Assert.Equal("arxiv:2501.11111", result.PaperId);
        Assert.Equal(2, result.Pages);
        Assert.Equal(2, result.Chunks);
        Assert.Equal(2, vectors.Count);
        Assert.Equal(2, bm25.Count);
        Assert.Equal(1, embedder.Calls); // all chunks embedded in one call

        var search = new HybridSearch(embedder, vectors, bm25, new PostgresChunkLookup(db.Factory));
        var hits = await search.SearchAsync("how do they measure reciprocal rank", 1);
        Assert.Equal("arxiv:2501.11111", hits[0].PaperId);
        Assert.Equal(2, hits[0].Page);
        Assert.Equal("Title 2501.11111", hits[0].PaperTitle);
    }

    [PostgresFact]
    public async Task IngestingTwice_DoesNothingTheSecondTime()
    {
        var source = new FakeSource((Info("2501.22222"), TestPdf.Build("some text " + TestPdf.Words("x", 80))));
        var (ingestor, vectors, _, _) = Create(source);

        await ingestor.IngestAsync("2501.22222", default);
        var second = await ingestor.IngestAsync("2501.22222", default);

        Assert.True(second.AlreadyIngested);
        Assert.Equal(1, source.Downloads);
        Assert.Equal(1, vectors.Count);
    }

    [PostgresFact]
    public async Task NoOpenAccessPdf_FailsWithReason()
    {
        var (ingestor, _, _, _) = Create(new FakeSource((Info("2501.33333", pdfUrl: null), null)));

        var result = await ingestor.IngestAsync("2501.33333", default);

        Assert.False(result.Success);
        Assert.Contains("no open-access PDF", result.Error, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task UnknownPaper_FailsWithReason()
    {
        var (ingestor, _, _, _) = Create(new FakeSource());

        var result = await ingestor.IngestAsync("missing", default);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task KeywordIndex_IsRebuiltFromDatabase()
    {
        var source = new FakeSource((Info("2501.44444"), TestPdf.Build("unique zebra token " + TestPdf.Words("y", 80))));
        var (ingestor, _, _, _) = Create(source);
        await ingestor.IngestAsync("2501.44444", default);

        var fresh = new Bm25Index(); // like after a restart
        var loaded = await PaperIngestor.LoadKeywordIndexAsync(db.Factory, fresh);

        Assert.True(loaded >= 1);
        Assert.NotEmpty(fresh.Search("zebra", 5));
    }
}
