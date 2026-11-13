using System.Text.Json;
using ResearchScout.Scout.Evaluation;

namespace ResearchScout.Scout.Tests;

public class EvalScoringTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static readonly EvalQuestion Q = new()
    {
        Id = "q1", PaperId = "arxiv:1", Question = "?", Evidence = ["RAG-Sequence", "RAG-Token"],
    };

    private static readonly Dictionary<Citation, string> Pages = new()
    {
        [new Citation("arxiv:1", 3)] = "We propose two models, RAG-Sequence and RAG-Token.",
        [new Citation("arxiv:1", 4)] = "Experiments use Natural Questions.",
    };

    private static string? Page(Citation c) => Pages.GetValueOrDefault(c);

    [Fact]
    public void ExtractCitations_FindsTags_Deduplicated_LowerCased()
    {
        var c = EvalScoring.ExtractCitations("A [arXiv:1 p.3]. B [arxiv:1 p.3]. C [s2:xyz p.10].");

        Assert.Equal([new Citation("arxiv:1", 3), new Citation("s2:xyz", 10)], c);
    }

    [Fact]
    public void Score_CountsCorrectWrongAndInventedCitations()
    {
        // p.3 has both evidence phrases (correct), p.4 doesn't (wrong), p.99 doesn't exist (invented).
        var s = EvalScoring.Score(Q, "x [arxiv:1 p.3] y [arxiv:1 p.4] z [arxiv:1 p.99]", Page);

        Assert.Equal(3, s.Citations);
        Assert.Equal(1, s.CorrectCitations);
        Assert.Equal(1, s.InvalidCitations);
        Assert.True(s.AnyCorrect);
    }

    [Fact]
    public void PageHasEvidence_NeedsAllPhrases_IgnoresCaseAndSpacing()
    {
        Assert.True(EvalScoring.PageHasEvidence("we use  rag-sequence\nand RAG-token models", ["RAG-Sequence", "RAG-Token"]));
        Assert.False(EvalScoring.PageHasEvidence("only RAG-Sequence here", ["RAG-Sequence", "RAG-Token"]));
        Assert.False(EvalScoring.PageHasEvidence("anything", []));
    }

    [Fact]
    public void Unanswerable_ScoresRefusal()
    {
        var q = new EvalQuestion { Id = "n", PaperId = "arxiv:1", Question = "?", Unanswerable = true };

        Assert.True(EvalScoring.Score(q, "Not found in the ingested papers.", Page).RefusedCorrectly);
        Assert.False(EvalScoring.Score(q, "It is 10 million [arxiv:1 p.3].", Page).RefusedCorrectly);
    }

    [Fact]
    public void Summarize_UsesTheDocumentedFormulas()
    {
        var questions = new[] { Q, Q with { Id = "q2" }, new EvalQuestion { Id = "n", PaperId = "x", Question = "?", Unanswerable = true } };
        var scores = new[]
        {
            new QuestionScore("q1", Citations: 3, CorrectCitations: 2, InvalidCitations: 0, AnyCorrect: true, RefusedCorrectly: null),
            new QuestionScore("q2", Citations: 1, CorrectCitations: 0, InvalidCitations: 1, AnyCorrect: false, RefusedCorrectly: null),
            new QuestionScore("n", Citations: 0, CorrectCitations: 0, InvalidCitations: 0, AnyCorrect: false, RefusedCorrectly: true),
        };

        var s = EvalScoring.Summarize(questions, scores);

        Assert.Equal(2, s.AnswerableQuestions);
        Assert.Equal(0.5, s.CitationAccuracy);              // (2 + 0) correct ÷ (3 + 1) citations
        Assert.Equal(0.5, s.QuestionsWithCorrectCitation);  // q1 yes, q2 no
        Assert.Equal(1.0, s.QuestionsWithAnyCitation);      // both cited something
        Assert.Equal(1, s.InvalidCitations);
        Assert.Equal(1.0, s.RefusalAccuracy);
    }

    [Fact]
    public void TopKHitRate_CountsKnownGoodFitsInTopK()
    {
        var ranked = new[] { "Ada", "Bob", "Cy", "Di", "Ed", "Flo" };

        Assert.Equal(0.5, EvalScoring.TopKHitRate(ranked, ["bob", "Flo"], 5)); // Bob in top 5, Flo is 6th
    }

    [Fact]
    public void EvalDataFiles_AreValid()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "eval", "questions.json")))
        {
            dir = dir.Parent;
        }

        var questions = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "eval", "questions.json"))).RootElement
            .GetProperty("questions").Deserialize<List<EvalQuestion>>(WebJson)!;
        var papers = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "eval", "papers.json"))).RootElement
            .GetProperty("papers").EnumerateArray().Select(p => p.GetString()!.ToLowerInvariant()).ToHashSet();

        Assert.Equal(questions.Count, questions.Select(q => q.Id).Distinct().Count());
        Assert.All(questions.Where(q => !q.Unanswerable), q => Assert.NotEmpty(q.Evidence));
        Assert.All(questions, q => Assert.Contains(q.PaperId, papers));
        Assert.Contains(questions, q => q.Unanswerable);
    }
}
