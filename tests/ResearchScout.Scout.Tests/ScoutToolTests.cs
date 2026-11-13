using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ResearchScout.Harness;
using ResearchScout.Infra.Tests;
using ResearchScout.Papers.Fetching;
using ResearchScout.Papers.Search;
using ResearchScout.Papers.Tests;
using ResearchScout.Scout.Tools;

namespace ResearchScout.Scout.Tests;

internal static class S2
{
    public static readonly RetryPolicy NoWait = new() { MaxAttempts = 1 };

    public static SemanticScholarClient Client(RoutedHandler handler) => new(new HttpClient(handler), new InMemoryApiCache(), retry: NoWait);

    public static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    public static JsonElement Out(Harness.Tools.ToolResult r) => JsonDocument.Parse(r.Content).RootElement;

    public static readonly FakeClock Clock = new(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero));
}

public class FindProfessorsToolTests
{
    [Fact]
    public async Task WithNames_PrefersTheMatchAtThatUniversity()
    {
        var handler = new RoutedHandler().On("author/search?query=Jane%20Doe", """
            {"data": [
              {"authorId": "9", "name": "Jane Doe", "affiliations": ["Stanford University"], "hIndex": 50},
              {"authorId": "7", "name": "Jane Doe", "affiliations": ["Memorial University of Newfoundland"], "hIndex": 12}
            ]}
            """);

        var result = await new FindProfessorsTool(S2.Client(handler), S2.Clock).RunAsync(
            S2.Args(new { university = "Memorial University of Newfoundland", topic = "LLMs", names = new[] { "Jane Doe" } }), default);

        var prof = S2.Out(result).GetProperty("professors")[0];
        Assert.Equal("7", prof.GetProperty("authorId").GetString());
    }

    [Fact]
    public async Task WithoutNames_FiltersPaperAuthorsByAffiliation()
    {
        var handler = new RoutedHandler()
            .On("paper/search", """{"data": [{"authors": [{"authorId": "1", "name": "A"}, {"authorId": "2", "name": "B"}]}]}""")
            .On("author/batch", """
                [{"authorId": "1", "name": "A", "affiliations": ["Memorial University, Canada"], "hIndex": 10},
                 {"authorId": "2", "name": "B", "affiliations": ["MIT"], "hIndex": 90}]
                """);

        var result = await new FindProfessorsTool(S2.Client(handler), S2.Clock).RunAsync(
            S2.Args(new { university = "Memorial University", topic = "code search" }), default);

        var profs = S2.Out(result).GetProperty("professors");
        Assert.Equal(1, profs.GetArrayLength());
        Assert.Equal("A", profs[0].GetProperty("name").GetString());
        Assert.Contains(handler.Calls, c => c.Url.Contains("year=2024-", StringComparison.Ordinal)); // last 3 years
    }

    [Fact]
    public async Task NoMatches_TellsTheModelToAskForNames()
    {
        var handler = new RoutedHandler().On("paper/search", """{"data": []}""");

        var result = await new FindProfessorsTool(S2.Client(handler), S2.Clock).RunAsync(
            S2.Args(new { university = "Tiny College", topic = "x" }), default);

        Assert.Contains("faculty page", S2.Out(result).GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingArgs_IsAnError()
    {
        var result = await new FindProfessorsTool(S2.Client(new RoutedHandler())).RunAsync(S2.Args(new { topic = "x" }), default);

        Assert.True(result.IsError);
    }
}

public class FitScorerTests
{
    private const string Profile = "retrieval augmented generation for source code search";

    private static RoutedHandler Papers() => new RoutedHandler()
        .On("author/good/papers", """
            {"data": [
              {"paperId": "g1", "title": "Retrieval augmented generation for code", "year": 2026, "abstract": "source code search with retrieval"},
              {"paperId": "g2", "title": "Code search benchmarks", "year": 2025, "abstract": "retrieval for source code"}
            ]}
            """)
        .On("author/far/papers", """
            {"data": [
              {"paperId": "f1", "title": "Protein folding dynamics", "year": 2026, "abstract": "molecular biology simulation"}
            ]}
            """)
        .On("author/old/papers", """
            {"data": [
              {"paperId": "o1", "title": "Retrieval augmented generation for code", "year": 2024, "abstract": "source code search with retrieval"}
            ]}
            """);

    [Fact]
    public async Task RelevantAuthor_ScoresHigher()
    {
        var scorer = new FitScorer(S2.Client(Papers()), new HashEmbedder(256), Profile, S2.Clock);

        var scores = await scorer.ScoreAsync(["far", "good"], default);

        Assert.Equal("good", scores[0].AuthorId);
        Assert.True(scores[0].Score > scores[1].Score);
        Assert.Equal(2, scores[0].PapersConsidered);
        Assert.Equal("Retrieval augmented generation for code", scores[0].TopPapers[0].Title);
    }

    [Fact]
    public async Task OlderWork_IsWeightedDown()
    {
        var scorer = new FitScorer(S2.Client(Papers()), new HashEmbedder(256), Profile, S2.Clock);

        var scores = (await scorer.ScoreAsync(["good", "old"], default)).ToDictionary(s => s.AuthorId);

        // Same best paper text; "old" is 2 years old → weight 1 − 0.15 × 2 = 0.70 vs 1.0 for 2026.
        var newest = scores["good"].TopPapers[0].Similarity;
        Assert.Equal((int)Math.Round(newest * 0.70 * 100), scores["old"].Score);
    }

    [Fact]
    public async Task AuthorWithNoRecentPapers_ScoresZero()
    {
        var handler = new RoutedHandler().On("author/none/papers", """{"data": []}""");

        var scores = await new FitScorer(S2.Client(handler), new HashEmbedder(), Profile, S2.Clock).ScoreAsync(["none"], default);

        Assert.Equal(0, Assert.Single(scores).Score);
    }

    [Fact]
    public async Task Tool_RejectsMoreThanTen()
    {
        var tool = new ScoreFitTool(new FitScorer(S2.Client(Papers()), new HashEmbedder(), Profile));

        var result = await tool.RunAsync(S2.Args(new { authorIds = Enumerable.Range(0, 11).Select(i => i.ToString()) }), default);

        Assert.True(result.IsError);
    }
}

public class SearchToolAndPromptTests
{
    [Fact]
    public async Task SearchPapers_ReturnsCiteTags()
    {
        var embedder = new HashEmbedder();
        var vectors = new InMemoryVectorStore();
        var bm25 = new Bm25Index();
        var id = Guid.NewGuid();
        const string text = "We evaluate with mean reciprocal rank on CodeSearchNet.";
        await vectors.UpsertAsync([new VectorPoint(id, (await embedder.GenerateAsync([text]))[0].Vector, "arxiv:2501.00001", 7)], default);
        bm25.Add(id, "arxiv:2501.00001", text);
        var lookup = new OneChunk(id, "arxiv:2501.00001", "RAG for Code", 7, text);

        var result = await new SearchPapersTool(new HybridSearch(embedder, vectors, bm25, lookup))
            .RunAsync(S2.Args(new { query = "reciprocal rank evaluation", paperIds = new[] { "arXiv:2501.00001" } }), default);

        var hit = S2.Out(result).GetProperty("results")[0];
        Assert.Equal("[arxiv:2501.00001 p.7]", hit.GetProperty("cite").GetString());
    }

    [Fact]
    public void CitationPattern_MatchesTheTagFormat()
    {
        var m = Regex.Match("MRR is 0.71 [arxiv:2106.15928 p.4].", ScoutPrompts.CitationPattern);

        Assert.True(m.Success);
        Assert.Equal("arxiv:2106.15928", m.Groups["paper"].Value);
        Assert.Equal("4", m.Groups["page"].Value);
    }

    [Fact]
    public void SystemPrompt_HasCitationRuleAndProfile()
    {
        var prompt = ScoutPrompts.System("MY PROFILE TEXT");

        Assert.Contains("Not found in the ingested papers", prompt, StringComparison.Ordinal);
        Assert.Contains("[arxiv:2106.15928 p.4]", prompt, StringComparison.Ordinal);
        Assert.Contains("MY PROFILE TEXT", prompt, StringComparison.Ordinal);
    }

    private sealed class OneChunk(Guid id, string paper, string title, int page, string text) : IChunkLookup
    {
        public Task<IReadOnlyDictionary<Guid, (string PaperId, string Title, int Page, string Text)>> GetAsync(
            IReadOnlyCollection<Guid> chunkIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, (string, string, int, string)>>(
                new Dictionary<Guid, (string, string, int, string)> { [id] = (paper, title, page, text) });
    }
}

public class DraftEmailToolTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private static RoutedHandler TheirPapers() => new RoutedHandler().On("author/7/papers", """
        {"data": [{"paperId": "s2paper1", "title": "Code RAG", "year": 2025, "externalIds": {"ArXiv": "2501.00009"}}]}
        """);

    private DraftEmailTool Tool(Guid runId) => new(S2.Client(TheirPapers()), db.Factory, () => runId);

    [Fact]
    public void NeedsApproval() => Assert.True(new DraftEmailTool(S2.Client(new RoutedHandler()), null!, () => null).NeedsApproval);

    [PostgresFact]
    public async Task RealCitedPaper_DraftIsSaved_NotSent()
    {
        var runId = Guid.NewGuid();

        var result = await Tool(runId).RunAsync(S2.Args(new
        {
            authorId = "7", professorName = "Dr. Doe", subject = "Prospective MSc student",
            body = "Dear Dr. Doe, I enjoyed your paper on code RAG...", citedPaperIds = new[] { "arXiv:2501.00009" },
        }), default);

        Assert.False(result.IsError, result.Content);
        Assert.False(S2.Out(result).GetProperty("sent").GetBoolean());
        await using var ctx = await db.Factory.CreateDbContextAsync();
        var draft = await ctx.EmailDrafts.SingleAsync(d => d.RunId == runId);
        Assert.Equal("arXiv:2501.00009", draft.CitedPaperIds);
    }

    [PostgresFact]
    public async Task PaperNotByThatProfessor_IsRejected()
    {
        var result = await Tool(Guid.NewGuid()).RunAsync(S2.Args(new
        {
            authorId = "7", professorName = "Dr. Doe", subject = "Hi", body = "Loved your famous paper.",
            citedPaperIds = new[] { "arXiv:1706.03762" },
        }), default);

        Assert.True(result.IsError);
        Assert.Contains("not in Dr. Doe's", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TooLong_IsRejected()
    {
        var result = await new DraftEmailTool(S2.Client(TheirPapers()), null!, () => null).RunAsync(S2.Args(new
        {
            authorId = "7", professorName = "Dr. Doe", subject = "Hi", body = string.Join(' ', Enumerable.Repeat("word", 300)),
            citedPaperIds = new[] { "s2paper1" },
        }), default);

        Assert.True(result.IsError);
        Assert.Contains("300 words", result.Content, StringComparison.Ordinal);
    }
}
