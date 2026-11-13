using ResearchScout.Papers.Chunking;
using ResearchScout.Papers.Search;

namespace ResearchScout.Papers.Tests;

public class PdfChunkerTests
{
    [Fact]
    public void RealPdf_ChunksKeepTheirPageNumbers()
    {
        var pdf = TestPdf.Build(
            "Introduction retrieval augmented generation for code " + TestPdf.Words("intro", 80),
            "Method hybrid search with reciprocal rank fusion " + TestPdf.Words("method", 80),
            "Results mean reciprocal rank improved " + TestPdf.Words("results", 80));

        var chunks = new PdfChunker().Chunk(pdf, out var pageCount);

        Assert.Equal(3, pageCount);
        Assert.Equal([1, 2, 3], chunks.Select(c => c.Page));
        Assert.Contains("reciprocal rank fusion", chunks[1].Text, StringComparison.Ordinal);
        Assert.Equal([0, 1, 2], chunks.Select(c => c.Index));
    }

    [Fact]
    public void LongPage_IsSplitWithOverlap_AndNeverCrossesPages()
    {
        var page1 = string.Join(' ', Enumerable.Range(1, 1000).Select(i => $"w{i}"));
        var chunker = new PdfChunker { ChunkWords = 400, OverlapWords = 50, MinWords = 60 };

        var chunks = chunker.ChunkPages([(1, page1), (2, "next page words " + TestPdf.Words("x", 100))]);

        // Page 1: starts at word 1, 351, 701 (step = 400 − 50 = 350); the last one has 300 words.
        var page1Chunks = chunks.Where(c => c.Page == 1).ToList();
        Assert.Equal(3, page1Chunks.Count);
        Assert.StartsWith("w351 ", page1Chunks[1].Text, StringComparison.Ordinal);
        Assert.Contains("w351", page1Chunks[0].Text, StringComparison.Ordinal); // overlap: w351..w400 in both
        Assert.EndsWith("w1000", page1Chunks[2].Text, StringComparison.Ordinal);
        Assert.StartsWith("next page", chunks.Single(c => c.Page == 2).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortTail_IsMergedIntoPreviousChunk()
    {
        var text = string.Join(' ', Enumerable.Range(1, 420).Select(i => $"w{i}"));
        var chunker = new PdfChunker { ChunkWords = 400, OverlapWords = 50, MinWords = 100 };

        var chunks = chunker.ChunkPages([(1, text)]);

        // Second chunk would be w351..w420 (70 words < 100), so its new words w401..w420 join the first chunk.
        var only = Assert.Single(chunks);
        Assert.EndsWith("w420", only.Text, StringComparison.Ordinal);
        Assert.Equal(420, only.Text.Split(' ').Length);
    }

    [Fact]
    public void LineEndHyphens_AreJoined()
    {
        var chunks = new PdfChunker().ChunkPages([(1, "dense retriev- al works")]);

        Assert.Equal("dense retrieval works", chunks[0].Text);
    }

    [Fact]
    public void EmptyPages_GiveNoChunks()
    {
        Assert.Empty(new PdfChunker().ChunkPages([(1, "   "), (2, "")]));
    }
}

public class Bm25IndexTests
{
    [Fact]
    public void ExactRareTerm_RanksFirst()
    {
        var index = new Bm25Index();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        index.Add(a, "p1", "we evaluate retrieval with recall and precision on many benchmarks");
        index.Add(b, "p1", "we report MRR@10 on CodeSearchNet for retrieval");
        index.Add(c, "p2", "the model is trained on code and text");

        var hits = index.Search("MRR@10 retrieval", 10);

        Assert.Equal(b, hits[0].ChunkId);
        Assert.DoesNotContain(hits, h => h.ChunkId == c); // shares no query term
    }

    [Fact]
    public void PaperFilter_LimitsResults()
    {
        var index = new Bm25Index();
        var a = Guid.NewGuid();
        index.Add(a, "p1", "graph neural networks for code");
        index.Add(Guid.NewGuid(), "p2", "graph neural networks for code");

        var hits = index.Search("graph neural", 10, ["p1"]);

        Assert.Equal(a, Assert.Single(hits).ChunkId);
    }

    [Fact]
    public void Tokenize_KeepsTechnicalTerms_DropsStopWords()
    {
        Assert.Equal(["gpt-4", "mrr@10", "f1", "score"], Bm25Index.Tokenize("The GPT-4 and MRR@10, F1 score."));
    }

    [Fact]
    public void AddingSameChunkTwice_CountsOnce()
    {
        var index = new Bm25Index();
        var id = Guid.NewGuid();
        index.Add(id, "p", "text one");
        index.Add(id, "p", "text one");

        Assert.Equal(1, index.Count);
    }
}

public class HybridSearchTests
{
    [Fact]
    public void Rrf_MatchesTheFormula()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        // a: rank 1 in list 1, rank 3 in list 2 → 1/61 + 1/63
        // b: rank 2 in list 1 only               → 1/62
        var fused = HybridSearch.Fuse([a, b], [Guid.NewGuid(), Guid.NewGuid(), a]).ToDictionary(x => x.ChunkId, x => x.Score);

        Assert.Equal((1.0 / 61) + (1.0 / 63), fused[a], 10);
        Assert.Equal(1.0 / 62, fused[b], 10);
        Assert.True(fused[a] > fused[b]);
    }

    [Fact]
    public async Task FindsChunk_AndReturnsCitationDetails()
    {
        var embedder = new HashEmbedder();
        var vectors = new InMemoryVectorStore();
        var bm25 = new Bm25Index();
        var lookup = new MemoryLookup();

        var texts = new[]
        {
            ("p1", 3, "We evaluate retrieval quality with mean reciprocal rank on CodeSearchNet."),
            ("p1", 5, "Training uses contrastive loss on code and docstring pairs."),
            ("p2", 2, "Long context models read the whole repository instead of retrieving."),
        };

        var embeddings = await embedder.GenerateAsync(texts.Select(t => t.Item3));
        for (var i = 0; i < texts.Length; i++)
        {
            var id = Guid.NewGuid();
            await vectors.UpsertAsync([new VectorPoint(id, embeddings[i].Vector, texts[i].Item1, texts[i].Item2)], default);
            bm25.Add(id, texts[i].Item1, texts[i].Item3);
            lookup.Items[id] = (texts[i].Item1, $"Paper {texts[i].Item1}", texts[i].Item2, texts[i].Item3);
        }

        var hits = await new HybridSearch(embedder, vectors, bm25, lookup).SearchAsync("how is retrieval evaluated reciprocal rank", 2);

        Assert.Equal("p1", hits[0].PaperId);
        Assert.Equal(3, hits[0].Page);
        Assert.Contains("mean reciprocal rank", hits[0].Text, StringComparison.Ordinal);
        Assert.Equal("Paper p1", hits[0].PaperTitle);
    }

    private sealed class MemoryLookup : IChunkLookup
    {
        public Dictionary<Guid, (string PaperId, string Title, int Page, string Text)> Items { get; } = [];

        public Task<IReadOnlyDictionary<Guid, (string PaperId, string Title, int Page, string Text)>> GetAsync(
            IReadOnlyCollection<Guid> chunkIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, (string, string, int, string)>>(
                Items.Where(kv => chunkIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    }
}
