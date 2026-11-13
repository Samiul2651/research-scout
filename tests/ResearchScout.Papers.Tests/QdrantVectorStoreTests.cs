using Qdrant.Client;
using ResearchScout.Papers.Search;

namespace ResearchScout.Papers.Tests;

/// <summary>Runs only when TEST_QDRANT_URL is set (CI starts a Qdrant service). Example: http://localhost:6334</summary>
public sealed class QdrantFactAttribute : FactAttribute
{
    public QdrantFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_QDRANT_URL")))
        {
            Skip = "Set TEST_QDRANT_URL (gRPC, e.g. http://localhost:6334) to run Qdrant tests.";
        }
    }
}

public class QdrantVectorStoreTests
{
    [QdrantFact]
    public async Task CreateUpsertSearch_WithPaperFilter()
    {
        var url = new Uri(Environment.GetEnvironmentVariable("TEST_QDRANT_URL")!);
        using var client = new QdrantClient(url.Host, url.Port, https: url.Scheme == "https");
        var collection = "test_" + Guid.NewGuid().ToString("N")[..8];
        var store = new QdrantVectorStore(client, collection);

        try
        {
            await store.EnsureCollectionAsync(3, default);
            await store.EnsureCollectionAsync(3, default); // second call is a no-op

            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var c = Guid.NewGuid();
            await store.UpsertAsync(
            [
                new VectorPoint(a, new float[] { 1, 0, 0 }, "p1", 1),
                new VectorPoint(b, new float[] { 0.9f, 0.1f, 0 }, "p2", 4),
                new VectorPoint(c, new float[] { 0, 0, 1 }, "p1", 2),
            ], default);

            var all = await store.SearchAsync(new float[] { 1, 0, 0 }, 2, null, default);
            Assert.Equal([a, b], all.Select(h => h.ChunkId));

            var onlyP2 = await store.SearchAsync(new float[] { 1, 0, 0 }, 5, ["p2"], default);
            Assert.Equal(b, Assert.Single(onlyP2).ChunkId);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection);
        }
    }
}
