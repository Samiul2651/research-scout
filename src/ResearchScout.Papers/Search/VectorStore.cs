using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace ResearchScout.Papers.Search;

/// <summary>A chunk's vector plus where it came from.</summary>
public sealed record VectorPoint(Guid ChunkId, ReadOnlyMemory<float> Vector, string PaperId, int Page);

public sealed record VectorHit(Guid ChunkId, float Score);

/// <summary>Stores chunk vectors and finds the nearest ones to a query vector.</summary>
public interface IVectorStore
{
    Task EnsureCollectionAsync(int dimensions, CancellationToken cancellationToken);

    Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken cancellationToken);

    /// <summary>Nearest chunks by cosine similarity. <paramref name="paperIds"/> limits the search to those papers.</summary>
    Task<IReadOnlyList<VectorHit>> SearchAsync(
        ReadOnlyMemory<float> query, int limit, IReadOnlyCollection<string>? paperIds, CancellationToken cancellationToken);
}

/// <summary>Qdrant (local Docker or Qdrant Cloud) through the official gRPC client.</summary>
public sealed class QdrantVectorStore(QdrantClient client, string collection = "chunks") : IVectorStore
{
    /// <summary>Upserts are sent in batches of this size (one request each).</summary>
    public const int UpsertBatchSize = 256;

    public async Task EnsureCollectionAsync(int dimensions, CancellationToken cancellationToken)
    {
        if (await client.CollectionExistsAsync(collection, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await client.CreateCollectionAsync(
            collection,
            new VectorParams { Size = (ulong)dimensions, Distance = Distance.Cosine },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Filtering by paper is common ("search only this paper"), so index the field.
        await client.CreatePayloadIndexAsync(collection, "paper_id", PayloadSchemaType.Keyword, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);

        foreach (var batch in points.Chunk(UpsertBatchSize))
        {
            var structs = batch.Select(p => new PointStruct
            {
                Id = p.ChunkId,
                Vectors = p.Vector.ToArray(),
                Payload = { ["paper_id"] = p.PaperId, ["page"] = p.Page },
            }).ToList();

            await client.UpsertAsync(collection, structs, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        ReadOnlyMemory<float> query, int limit, IReadOnlyCollection<string>? paperIds, CancellationToken cancellationToken)
    {
        Filter? filter = null;
        if (paperIds is { Count: > 0 })
        {
            filter = new Filter();
            filter.Must.Add(new Condition
            {
                Field = new FieldCondition { Key = "paper_id", Match = new Match { Keywords = new RepeatedStrings { Strings = { paperIds } } } },
            });
        }

        var hits = await client.QueryAsync(
            collection,
            query: query.ToArray(),
            filter: filter,
            limit: (ulong)limit,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return [.. hits.Select(h => new VectorHit(Guid.Parse(h.Id.Uuid), h.Score))];
    }
}

/// <summary>Brute-force cosine search in memory. For tests and small local runs without Qdrant.</summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, VectorPoint> _points = new();

    public int Count => _points.Count;

    public Task EnsureCollectionAsync(int dimensions, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);
        foreach (var p in points)
        {
            _points[p.ChunkId] = p;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorHit>> SearchAsync(
        ReadOnlyMemory<float> query, int limit, IReadOnlyCollection<string>? paperIds, CancellationToken cancellationToken)
    {
        IReadOnlyList<VectorHit> hits = [.. _points.Values
            .Where(p => paperIds is not { Count: > 0 } || paperIds.Contains(p.PaperId))
            .Select(p => new VectorHit(p.ChunkId, Cosine(query.Span, p.Vector.Span)))
            .OrderByDescending(h => h.Score)
            .Take(limit)];
        return Task.FromResult(hits);
    }

    private static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }
}
