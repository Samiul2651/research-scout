using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ResearchScout.Harness.Tools;
using ResearchScout.Infra;
using ResearchScout.Papers.Fetching;

namespace ResearchScout.Scout.Tools;

/// <summary>Ranks researchers by how well their recent papers match the interest profile.</summary>
public sealed class ScoreFitTool(FitScorer scorer) : ScoutTool
{
    public override string Name => "score_fit";

    public override string Description =>
        "Scores up to 10 researchers (0-100) by how well their last 3 years of papers match the student's research profile " +
        "(RAG, code intelligence, LLMs for software engineering), favouring recent work. Returns each score with the best-matching papers as evidence.";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "authorIds": { "type": "array", "items": { "type": "string" }, "description": "Semantic Scholar author ids from find_professors (max 10)" }
          },
          "required": ["authorIds"]
        }
        """;

    public override async Task<ToolResult> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var ids = StrListArg(input, "authorIds");
        if (ids.Count == 0)
        {
            return ToolResult.Error("'authorIds' needs at least one id.");
        }

        if (ids.Count > 10)
        {
            return ToolResult.Error("At most 10 authors per call. Split the list.");
        }

        var scores = await scorer.ScoreAsync(ids, cancellationToken).ConfigureAwait(false);
        return Json(new
        {
            method = "score = average of (similarity to profile × recency weight) over each author's best 5 recent papers, × 100",
            ranking = scores.Select(s => new
            {
                s.AuthorId,
                s.Score,
                s.PapersConsidered,
                evidence = s.TopPapers.Select(p => new { p.Title, p.Year, similarity = p.Similarity }),
            }),
        });
    }
}

/// <summary>
/// Saves an outreach email draft for a professor, after a human approves it. Never sends anything.
/// Every cited paper is checked against the professor's real paper list first, so the email
/// can't praise a paper they didn't write.
/// </summary>
public sealed class DraftEmailTool(
    SemanticScholarClient scholar,
    IDbContextFactory<ScoutDbContext> dbFactory,
    Func<Guid?> currentRunId,
    TimeProvider? clock = null) : ScoutTool
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public override string Name => "draft_email";

    public override bool NeedsApproval => true;

    public override string Description =>
        "Saves a short outreach email draft to a professor (the user must approve it first; it is never sent). " +
        "Write the subject and body yourself: under 200 words, specific, citing 1-2 of the professor's real papers by title. " +
        "Pass those papers' ids in citedPaperIds; each is checked against the professor's actual papers.";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "authorId": { "type": "string", "description": "Semantic Scholar author id of the professor" },
            "professorName": { "type": "string" },
            "subject": { "type": "string" },
            "body": { "type": "string", "description": "Email text, under 200 words" },
            "citedPaperIds": { "type": "array", "items": { "type": "string" }, "description": "1-2 paper ids (as returned by get_recent_papers) mentioned in the email" }
          },
          "required": ["authorId", "professorName", "subject", "body", "citedPaperIds"]
        }
        """;

    public override async Task<ToolResult> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var authorId = StrArg(input, "authorId");
        var name = StrArg(input, "professorName");
        var subject = StrArg(input, "subject");
        var body = StrArg(input, "body");
        var cited = StrListArg(input, "citedPaperIds");

        if (authorId is null || name is null || subject is null || body is null)
        {
            return ToolResult.Error("authorId, professorName, subject and body are all required.");
        }

        if (cited.Count is < 1 or > 2)
        {
            return ToolResult.Error("Cite 1 or 2 of the professor's papers in citedPaperIds.");
        }

        var wordCount = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 250)
        {
            return ToolResult.Error($"The body has {wordCount} words. Keep it under 200.");
        }

        // Check every cited paper is really theirs (any year).
        var theirPapers = await scholar.GetAuthorPapersAsync(authorId, fromYear: 0, cancellationToken).ConfigureAwait(false);
        var known = theirPapers
            .SelectMany(p => new[] { p.PaperId, p.ArxivId is null ? null : $"arXiv:{p.ArxivId}", p.ArxivId is null ? null : $"arxiv:{p.ArxivId}" })
            .Where(id => id is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = cited.Where(id => !known.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            return ToolResult.Error(
                $"These papers are not in {name}'s Semantic Scholar paper list: {string.Join(", ", unknown)}. " +
                "Use ids from get_recent_papers for this author.");
        }

        var draft = new EmailDraftEntity
        {
            Id = Guid.NewGuid(),
            RunId = currentRunId(),
            AuthorId = authorId,
            ProfessorName = name,
            Subject = Clip(subject, 300),
            Body = body,
            CitedPaperIds = string.Join(",", cited),
            CreatedAt = _clock.GetUtcNow(),
        };

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            db.EmailDrafts.Add(draft);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Json(new { draftId = draft.Id, saved = true, sent = false, note = "Draft saved for the user to review and send themselves." });
    }
}
