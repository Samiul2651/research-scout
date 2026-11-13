using System.Text.RegularExpressions;

namespace ResearchScout.Scout.Evaluation;

/// <summary>One eval question. <see cref="Evidence"/> phrases must appear on a cited page for the citation to count as correct.</summary>
public sealed record EvalQuestion
{
    public required string Id { get; init; }
    public required string Question { get; init; }

    /// <summary>Paper the answer is in, as stored (e.g. "arxiv:2005.11401").</summary>
    public required string PaperId { get; init; }

    /// <summary>Phrases that appear on the page that answers the question (case-insensitive, all must match).</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>True when the papers do not contain the answer: the correct reply is "Not found".</summary>
    public bool Unanswerable { get; init; }
}

public sealed record Citation(string PaperId, int Page);

/// <summary>Result for one question.</summary>
public sealed record QuestionScore(
    string Id,
    int Citations,
    int CorrectCitations,
    int InvalidCitations,
    bool AnyCorrect,
    bool? RefusedCorrectly);

/// <summary>Totals over all questions.</summary>
public sealed record EvalSummary(
    int Questions,
    int AnswerableQuestions,
    double CitationAccuracy,
    double QuestionsWithCorrectCitation,
    double QuestionsWithAnyCitation,
    int InvalidCitations,
    double? RefusalAccuracy);

/// <summary>Pure scoring functions for the citation eval (no I/O, easy to test).</summary>
public static partial class EvalScoring
{
    public const string NotFoundPhrase = "not found in the ingested papers";

    /// <summary>All [paperId p.N] tags in an answer, in order, without duplicates.</summary>
    public static IReadOnlyList<Citation> ExtractCitations(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return [.. CitationRegex().Matches(answer)
            .Select(m => new Citation(m.Groups["paper"].Value.ToLowerInvariant(), int.Parse(m.Groups["page"].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .Distinct()];
    }

    /// <summary>True if the page text contains every evidence phrase (case-insensitive; whitespace differences ignored).</summary>
    public static bool PageHasEvidence(string pageText, IReadOnlyList<string> evidence)
    {
        ArgumentNullException.ThrowIfNull(pageText);
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Count == 0)
        {
            return false;
        }

        var page = Normalize(pageText);
        return evidence.All(e => page.Contains(Normalize(e), StringComparison.Ordinal));
    }

    /// <summary>
    /// Scores one answer. <paramref name="pageText"/> returns the text of a (paper, page), or null if that page
    /// does not exist in the ingested papers (an invented citation).
    /// </summary>
    public static QuestionScore Score(EvalQuestion question, string answer, Func<Citation, string?> pageText)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(pageText);

        var citations = ExtractCitations(answer);
        var correct = 0;
        var invalid = 0;
        foreach (var c in citations)
        {
            var text = pageText(c);
            if (text is null)
            {
                invalid++;
            }
            else if (!question.Unanswerable && PageHasEvidence(text, question.Evidence))
            {
                correct++;
            }
        }

        bool? refused = question.Unanswerable
            ? answer.Contains(NotFoundPhrase, StringComparison.OrdinalIgnoreCase) || answer.Contains("not found", StringComparison.OrdinalIgnoreCase)
            : null;

        return new QuestionScore(question.Id, citations.Count, correct, invalid, correct > 0, refused);
    }

    /// <summary>
    /// Citation accuracy = correct citations ÷ all citations, over answerable questions.
    /// Questions with a correct citation = answerable questions with at least one correct citation ÷ answerable questions.
    /// Refusal accuracy = unanswerable questions answered "Not found" ÷ unanswerable questions.
    /// </summary>
    public static EvalSummary Summarize(IReadOnlyList<EvalQuestion> questions, IReadOnlyList<QuestionScore> scores)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(scores);

        var unanswerableIds = questions.Where(q => q.Unanswerable).Select(q => q.Id).ToHashSet();
        var answerable = scores.Where(s => !unanswerableIds.Contains(s.Id)).ToList();
        var unanswerable = scores.Where(s => unanswerableIds.Contains(s.Id)).ToList();

        var totalCitations = answerable.Sum(s => s.Citations);
        return new EvalSummary(
            Questions: scores.Count,
            AnswerableQuestions: answerable.Count,
            CitationAccuracy: totalCitations == 0 ? 0 : (double)answerable.Sum(s => s.CorrectCitations) / totalCitations,
            QuestionsWithCorrectCitation: answerable.Count == 0 ? 0 : (double)answerable.Count(s => s.AnyCorrect) / answerable.Count,
            QuestionsWithAnyCitation: answerable.Count == 0 ? 0 : (double)answerable.Count(s => s.Citations > 0) / answerable.Count,
            InvalidCitations: scores.Sum(s => s.InvalidCitations),
            RefusalAccuracy: unanswerable.Count == 0 ? null : (double)unanswerable.Count(s => s.RefusedCorrectly == true) / unanswerable.Count);
    }

    /// <summary>Recall@k for retrieval: did any of the top-k hits land on a page with the evidence?</summary>
    public static bool RetrievalHit(EvalQuestion question, IEnumerable<(string PaperId, int Page, string Text)> topHits)
    {
        ArgumentNullException.ThrowIfNull(question);
        return topHits.Any(h => string.Equals(h.PaperId, question.PaperId, StringComparison.OrdinalIgnoreCase) && PageHasEvidence(h.Text, question.Evidence));
    }

    /// <summary>Top-k hit rate for professor ranking: share of known good-fit professors found in the top k.</summary>
    public static double TopKHitRate(IReadOnlyList<string> rankedNames, IReadOnlyList<string> knownGoodFit, int k)
    {
        ArgumentNullException.ThrowIfNull(rankedNames);
        ArgumentNullException.ThrowIfNull(knownGoodFit);
        if (knownGoodFit.Count == 0)
        {
            return 0;
        }

        var top = rankedNames.Take(k).Select(Normalize).ToHashSet();
        return (double)knownGoodFit.Count(n => top.Contains(Normalize(n))) / knownGoodFit.Count;
    }

    private static string Normalize(string s) => Spaces().Replace(s.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(ScoutPrompts.CitationPattern)]
    private static partial Regex CitationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
