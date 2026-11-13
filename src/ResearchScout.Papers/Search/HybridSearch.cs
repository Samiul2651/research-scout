using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ResearchScout.Infra;

namespace ResearchScout.Papers.Search;

/// <summary>A search result with everything needed to cite it.</summary>
public sealed record SearchHit(Guid ChunkId, string PaperId, string PaperTitle, int Page, string Text, double Score);

/// <summary>Looks up chunk text and paper titles by id (from Postgres, or memory in tests).</summary>
public interface IChunkLookup
{
    Task<IReadOnlyDictionary<Guid, (string PaperId, string Title, int Page, string Text)>> GetAsync(
        IReadOnlyCollection<Guid> chunkIds, CancellationToken cancellationToken);
}

public sealed class PostgresChunkLookup(IDbContextFactory<ScoutDbContext> dbFactory) : IChunkLookup
{
    public async Task<IReadOnlyDictionary<Guid, (string PaperId, string Title, int Page, string Text)>> GetAsync(
        IReadOnlyCollection<Guid> chunkIds, CancellationToken cancellationToken)
    {
        // One query for all ids (not one per id).
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await (
            from c in db.Chunks
            join p in db.Papers on c.PaperId equals p.Id
            where chunkIds.Contains(c.Id)
            select new { c.Id, c.PaperId, p.Title, c.Page, c.Text }).ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => (r.PaperId, r.Title, r.Page, r.Text));
    }
}

/// <summary>
/// Hybrid search: vector search (meaning) + BM25 (exact words), merged with reciprocal rank fusion (RRF).
///
/// RRF score of a chunk = Σ over the two result lists of 1 / (60 + rank), rank starting at 1.
/// Example: rank 1 in vectors and rank 3 in BM25 → 1/61 + 1/63 ≈ 0.0323. A chunk found by only one list
/// gets just that list's part. 60 is the standard constant; it stops the top rank from dominating.
/// </summary>
public sealed class HybridSearch(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IVectorStore vectors,
    Bm25Index keywords,
    IChunkLookup chunks)
{
    public const int RrfK = 60;

    /// <summary>How many candidates each method contributes before fusion.</summary>
    public int CandidatesPerMethod { get; init; } = 20;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query, int limit, IReadOnlyCollection<string>? paperIds = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var embedding = await embedder.GenerateAsync([query], EmbeddingInputType.Options(EmbeddingInputType.Query), cancellationToken)
            .ConfigureAwait(false);
        var vectorHits = await vectors.SearchAsync(embedding[0].Vector, CandidatesPerMethod, paperIds, cancellationToken).ConfigureAwait(false);
        var keywordHits = keywords.Search(query, CandidatesPerMethod, paperIds);

        var fused = Fuse(vectorHits.Select(h => h.ChunkId), keywordHits.Select(h => h.ChunkId)).Take(limit).ToList();
        if (fused.Count == 0)
        {
            return [];
        }

        var details = await chunks.GetAsync([.. fused.Select(f => f.ChunkId)], cancellationToken).ConfigureAwait(false);

        return [.. fused
            .Where(f => details.ContainsKey(f.ChunkId))
            .Select(f =>
            {
                var d = details[f.ChunkId];
                return new SearchHit(f.ChunkId, d.PaperId, d.Title, d.Page, d.Text, f.Score);
            })];
    }

    /// <summary>Reciprocal rank fusion of ranked id lists, best first.</summary>
    public static IEnumerable<(Guid ChunkId, double Score)> Fuse(params IEnumerable<Guid>[] rankedLists)
    {
        ArgumentNullException.ThrowIfNull(rankedLists);
        var scores = new Dictionary<Guid, double>();

        foreach (var list in rankedLists)
        {
            var rank = 0;
            foreach (var id in list)
            {
                rank++;
                scores[id] = scores.GetValueOrDefault(id) + (1.0 / (RrfK + rank));
            }
        }

        return scores.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value));
    }
}
