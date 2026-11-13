using System.Numerics.Tensors;
using Microsoft.Extensions.AI;
using ResearchScout.Papers.Fetching;
using ResearchScout.Papers.Search;

namespace ResearchScout.Scout;

/// <summary>How well one researcher's recent work matches the student's interest profile.</summary>
public sealed record FitScore(string AuthorId, int Score, int PapersConsidered, IReadOnlyList<(string Title, int? Year, double Similarity)> TopPapers);

/// <summary>
/// Scores researchers against the interest profile (profile.md).
///
/// For each of the author's papers from the last 3 years:
///   similarity = cosine(profile embedding, embedding of "title. abstract")      (0 to 1)
///   recency weight = 1.0 this year, 0.85 last year, 0.70 two years ago            (1 − 0.15 × age)
///   weighted = similarity × weight
/// Score = average "weighted" of the author's best 5 papers, × 100, rounded.
/// Using the best 5 (not all papers) means one strong line of work counts, even for very prolific authors.
///
/// All papers of all authors are embedded in one batched request.
/// </summary>
public sealed class FitScorer(
    SemanticScholarClient scholar,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    string interestProfile,
    TimeProvider? clock = null)
{
    public const int TopPapersPerAuthor = 5;
    public const double RecencyDropPerYear = 0.15;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<IReadOnlyList<FitScore>> ScoreAsync(IReadOnlyList<string> authorIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorIds);
        var fromYear = SemanticScholarClient.FromYear(3, _clock);
        var thisYear = _clock.GetUtcNow().Year;

        var papersByAuthor = new Dictionary<string, IReadOnlyList<PaperInfo>>();
        foreach (var id in authorIds.Distinct())
        {
            papersByAuthor[id] = await scholar.GetAuthorPapersAsync(id, fromYear, cancellationToken).ConfigureAwait(false);
        }

        var allPapers = papersByAuthor.SelectMany(kv => kv.Value.Select(p => (AuthorId: kv.Key, Paper: p))).ToList();
        if (allPapers.Count == 0)
        {
            return [.. authorIds.Distinct().Select(id => new FitScore(id, 0, 0, []))];
        }

        // One embedding call for the profile, one for every paper (the embedder batches internally).
        var profileVector = (await embedder.GenerateAsync([interestProfile], EmbeddingInputType.Options(EmbeddingInputType.Query), cancellationToken)
            .ConfigureAwait(false))[0].Vector;
        var paperVectors = await embedder.GenerateAsync(
            allPapers.Select(x => $"{x.Paper.Title}. {x.Paper.Abstract}"), EmbeddingInputType.Options(EmbeddingInputType.Document), cancellationToken)
            .ConfigureAwait(false);

        var scored = allPapers.Select((x, i) =>
        {
            var similarity = Math.Max(0, TensorPrimitives.CosineSimilarity(profileVector.Span, paperVectors[i].Vector.Span));
            var age = Math.Max(0, thisYear - (x.Paper.Year ?? fromYear));
            var weight = Math.Max(0, 1 - (RecencyDropPerYear * age));
            return (x.AuthorId, x.Paper, Similarity: (double)similarity, Weighted: similarity * weight);
        }).ToList();

        return [.. authorIds.Distinct().Select(id =>
        {
            var best = scored.Where(s => s.AuthorId == id).OrderByDescending(s => s.Weighted).Take(TopPapersPerAuthor).ToList();
            var score = best.Count == 0 ? 0 : (int)Math.Round(best.Average(b => b.Weighted) * 100);
            return new FitScore(id, score, papersByAuthor[id].Count,
                [.. best.Take(3).Select(b => (b.Paper.Title, b.Paper.Year, Math.Round(b.Similarity, 3)))]);
        }).OrderByDescending(f => f.Score)];
    }
}
