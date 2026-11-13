using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ResearchScout.Infra;
using ResearchScout.Papers.Chunking;
using ResearchScout.Papers.Fetching;
using ResearchScout.Papers.Search;

namespace ResearchScout.Papers;

/// <summary>Where paper metadata and PDFs come from. Real: Semantic Scholar + download. Tests: in memory.</summary>
public interface IPaperSource
{
    Task<PaperInfo?> GetPaperAsync(string paperId, CancellationToken cancellationToken);

    Task<byte[]> DownloadPdfAsync(PaperInfo paper, CancellationToken cancellationToken);
}

public sealed class RemotePaperSource(SemanticScholarClient scholar, PdfDownloader downloader) : IPaperSource
{
    public Task<PaperInfo?> GetPaperAsync(string paperId, CancellationToken cancellationToken) =>
        scholar.GetPaperAsync(paperId, cancellationToken);

    public Task<byte[]> DownloadPdfAsync(PaperInfo paper, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paper);
        return downloader.DownloadAsync(new Uri(paper.PdfUrl ?? throw new InvalidOperationException("No PDF link.")), cancellationToken);
    }
}

public sealed record IngestResult(
    bool Success,
    string? PaperId,
    string? Title,
    int Pages,
    int Chunks,
    bool AlreadyIngested,
    string? Error = null)
{
    public static IngestResult Fail(string error) => new(false, null, null, 0, 0, false, error);
}

/// <summary>
/// Ingests one paper: look up → download PDF → chunk (with pages) → embed (batched) → Qdrant → Postgres → BM25.
/// Vectors are written before the database row, so a failure can never leave a paper marked as ingested
/// without its vectors. Ingesting the same paper twice is a no-op.
/// </summary>
public sealed class PaperIngestor(
    IPaperSource source,
    PdfChunker chunker,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IVectorStore vectors,
    Bm25Index keywords,
    IDbContextFactory<ScoutDbContext> dbFactory,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<IngestResult> IngestAsync(string paperId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paperId);

        var info = await source.GetPaperAsync(paperId.Trim(), cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return IngestResult.Fail($"Paper '{paperId}' was not found on Semantic Scholar. Try an id like 'arXiv:2106.15928' or a DOI.");
        }

        var existing = await FindExistingAsync(info.StableId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        if (info.PdfUrl is null)
        {
            return IngestResult.Fail($"'{info.Title}' has no open-access PDF (not on arXiv and no free link), so it can't be read.");
        }

        byte[] pdf;
        try
        {
            pdf = await source.DownloadPdfAsync(info, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return IngestResult.Fail($"Could not download the PDF for '{info.Title}': {ex.Message}");
        }

        IReadOnlyList<TextChunk> chunks;
        int pageCount;
        try
        {
            chunks = chunker.Chunk(pdf, out pageCount);
        }
#pragma warning disable CA1031 // PdfPig throws many exception types for broken files; all mean "can't read this PDF".
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return IngestResult.Fail($"Could not read the PDF for '{info.Title}': {ex.Message}");
        }

        if (chunks.Count == 0)
        {
            return IngestResult.Fail($"No text found in the PDF for '{info.Title}' (it may be scanned images).");
        }

        // 1. Embed all chunks (the embedder batches requests).
        var embeddings = await embedder.GenerateAsync(
            chunks.Select(c => c.Text), EmbeddingInputType.Options(EmbeddingInputType.Document), cancellationToken).ConfigureAwait(false);

        var ids = chunks.Select(_ => Guid.NewGuid()).ToArray();

        // 2. Vectors first.
        await vectors.EnsureCollectionAsync(embeddings[0].Vector.Length, cancellationToken).ConfigureAwait(false);
        await vectors.UpsertAsync(
            [.. chunks.Select((c, i) => new VectorPoint(ids[i], embeddings[i].Vector, info.StableId, c.Page))],
            cancellationToken).ConfigureAwait(false);

        // 3. Then the paper + chunk rows, in one transaction.
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            db.Papers.Add(new PaperEntity
            {
                Id = info.StableId,
                Title = Truncate(info.Title, 1000),
                Authors = string.Join(", ", info.AuthorNames),
                Year = info.Year,
                Url = info.Url ?? info.PdfUrl,
                PageCount = pageCount,
                IngestedAt = _clock.GetUtcNow(),
            });
            db.Chunks.AddRange(chunks.Select((c, i) => new ChunkEntity
            {
                Id = ids[i],
                PaperId = info.StableId,
                Index = c.Index,
                Page = c.Page,
                Text = c.Text,
            }));

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Someone ingested the same paper at the same moment. Their copy wins; ours are orphan vectors
                // that search ignores (no chunk row).
                return await FindExistingAsync(info.StableId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Could not save paper {info.StableId}.");
            }
        }

        // 4. Keyword index.
        for (var i = 0; i < chunks.Count; i++)
        {
            keywords.Add(ids[i], info.StableId, chunks[i].Text);
        }

        return new IngestResult(true, info.StableId, info.Title, pageCount, chunks.Count, AlreadyIngested: false);
    }

    /// <summary>Fills the BM25 index from all stored chunks. Call once at startup.</summary>
    public static async Task<int> LoadKeywordIndexAsync(
        IDbContextFactory<ScoutDbContext> dbFactory, Bm25Index index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(index);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        await foreach (var c in db.Chunks.AsNoTracking().Select(c => new { c.Id, c.PaperId, c.Text }).AsAsyncEnumerable()
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            index.Add(c.Id, c.PaperId, c.Text);
            count++;
        }

        return count;
    }

    private async Task<IngestResult?> FindExistingAsync(string stableId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var paper = await db.Papers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == stableId, cancellationToken).ConfigureAwait(false);
        if (paper is null)
        {
            return null;
        }

        var chunkCount = await db.Chunks.CountAsync(c => c.PaperId == stableId, cancellationToken).ConfigureAwait(false);
        return new IngestResult(true, paper.Id, paper.Title, paper.PageCount, chunkCount, AlreadyIngested: true);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
