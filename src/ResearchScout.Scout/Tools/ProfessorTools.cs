using System.Text.Json;
using ResearchScout.Harness.Tools;
using ResearchScout.Papers.Fetching;

namespace ResearchScout.Scout.Tools;

/// <summary>
/// Finds researchers at a university who work on a topic.
/// Semantic Scholar only has affiliations for some authors, so the tool also accepts professor names
/// (for example copied from the department's faculty page), which is the most reliable way.
/// </summary>
public sealed class FindProfessorsTool(SemanticScholarClient scholar, TimeProvider? clock = null) : ScoutTool
{
    public override string Name => "find_professors";

    public override string Description =>
        "Finds researchers at a university who publish on a topic, using Semantic Scholar. Returns author ids, names, " +
        "affiliations and h-index. If you know professor names (e.g. from a faculty page), pass them in 'names' for exact matches. " +
        "Affiliation data is incomplete, so an empty result does not mean nobody works on the topic.";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "university": { "type": "string", "description": "University name as it appears in affiliations, e.g. 'Memorial University of Newfoundland'" },
            "topic": { "type": "string", "description": "Research topic keywords, e.g. 'large language models for code'" },
            "names": { "type": "array", "items": { "type": "string" }, "description": "Optional professor names to look up directly" }
          },
          "required": ["university", "topic"]
        }
        """;

    public override async Task<ToolResult> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var university = StrArg(input, "university");
        var topic = StrArg(input, "topic");
        if (university is null || topic is null)
        {
            return ToolResult.Error("Both 'university' and 'topic' are required.");
        }

        var names = StrListArg(input, "names").Take(15).ToList();
        List<AuthorInfo> authors;

        if (names.Count > 0)
        {
            authors = [];
            foreach (var name in names)
            {
                var matches = await scholar.SearchAuthorsAsync(name, 5, cancellationToken).ConfigureAwait(false);
                var best = matches.FirstOrDefault(a => AtUniversity(a, university)) ?? (matches.Count > 0 ? matches[0] : null);
                if (best is not null)
                {
                    authors.Add(best);
                }
            }
        }
        else
        {
            // Recent papers on the topic → their authors → keep authors whose affiliation mentions the university.
            var fromYear = SemanticScholarClient.FromYear(3, clock);
            var candidates = await scholar.SearchPaperAuthorsAsync($"{topic} {university}", fromYear, 100, cancellationToken).ConfigureAwait(false);
            var ids = candidates.Select(c => c.AuthorId).Distinct().Take(500).ToList();

            authors = [];
            foreach (var batch in ids.Chunk(100))
            {
                authors.AddRange(await scholar.GetAuthorsAsync(batch, cancellationToken).ConfigureAwait(false));
            }

            authors = [.. authors.Where(a => AtUniversity(a, university))];
        }

        var result = authors
            .DistinctBy(a => a.AuthorId)
            .OrderByDescending(a => a.HIndex ?? 0)
            .Take(15)
            .Select(a => new { a.AuthorId, a.Name, a.Affiliations, a.HIndex, a.PaperCount, a.Url })
            .ToList();

        return Json(new
        {
            university,
            topic,
            count = result.Count,
            professors = result,
            note = result.Count == 0
                ? "No matches with this affiliation on Semantic Scholar. Ask the user for professor names from the faculty page and call again with 'names'."
                : null,
        });
    }

    private static bool AtUniversity(AuthorInfo author, string university)
    {
        // Match on the distinctive words (e.g. "Memorial", "Newfoundland"), not on "University".
        var words = university.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3 && !w.Equals("University", StringComparison.OrdinalIgnoreCase) && !w.Equals("of", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return author.Affiliations.Any(aff =>
            aff.Contains(university, StringComparison.OrdinalIgnoreCase)
            || (words.Count > 0 && words.All(w => aff.Contains(w, StringComparison.OrdinalIgnoreCase))));
    }
}

/// <summary>An author's recent papers with abstracts.</summary>
public sealed class GetRecentPapersTool(SemanticScholarClient scholar, TimeProvider? clock = null) : ScoutTool
{
    public override string Name => "get_recent_papers";

    public override string Description =>
        "Lists one author's papers from the last few years (default 3), newest first, with venue, abstract and whether a free PDF exists. " +
        "Use the returned paperId with ingest_paper to read a paper.";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "authorId": { "type": "string", "description": "Semantic Scholar author id from find_professors" },
            "years": { "type": "integer", "description": "How many years back, 1-10 (default 3)" },
            "limit": { "type": "integer", "description": "Max papers, 1-30 (default 10)" }
          },
          "required": ["authorId"]
        }
        """;

    public override async Task<ToolResult> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var authorId = StrArg(input, "authorId");
        if (authorId is null)
        {
            return ToolResult.Error("'authorId' is required (get it from find_professors).");
        }

        var years = IntArg(input, "years", 3, 1, 10);
        var limit = IntArg(input, "limit", 10, 1, 30);
        var papers = await scholar.GetAuthorPapersAsync(authorId, SemanticScholarClient.FromYear(years, clock), cancellationToken).ConfigureAwait(false);

        return Json(new
        {
            authorId,
            count = Math.Min(papers.Count, limit),
            papers = papers.Take(limit).Select(p => new
            {
                paperId = p.ArxivId is not null ? $"arXiv:{p.ArxivId}" : p.PaperId,
                p.Title,
                p.Year,
                p.Venue,
                hasPdf = p.PdfUrl is not null,
                @abstract = Clip(p.Abstract, 500),
            }),
        });
    }
}
