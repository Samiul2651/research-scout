using System.Text.Json;
using ResearchScout.Harness.Tools;
using ResearchScout.Papers;
using ResearchScout.Papers.Search;

namespace ResearchScout.Scout.Tools;

/// <summary>Downloads, chunks and indexes a paper so search_papers can find it.</summary>
public sealed class IngestPaperTool(PaperIngestor ingestor) : ScoutTool
{
    public override string Name => "ingest_paper";

    public override string Description =>
        "Downloads a paper's PDF (arXiv or open access), splits it into page-tagged chunks and indexes it for search_papers. " +
        "Accepts a Semantic Scholar id, 'arXiv:2106.15928' or 'DOI:10.1145/...'. Safe to call again for an already ingested paper.";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "paperId": { "type": "string", "description": "e.g. 'arXiv:2106.15928', a DOI with 'DOI:' prefix, or a Semantic Scholar paper id" }
          },
          "required": ["paperId"]
        }
        """;

    public override async Task<ToolResult> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var paperId = StrArg(input, "paperId");
        if (paperId is null)
        {
            return ToolResult.Error("'paperId' is required.");
        }

        var result = await ingestor.IngestAsync(paperId, cancellationToken).ConfigureAwait(false);
        return result.Success
            ? Json(new
            {
                paperId = result.PaperId,
                result.Title,
                result.Pages,
                result.Chunks,
                status = result.AlreadyIngested ? "already ingested" : "ingested",
                citeAs = $"[{result.PaperId} p.N]",
            })
            : ToolResult.Error(result.Error ?? "Ingest failed.");
    }
}

/// <summary>Hybrid search over ingested papers. Every hit carries the citation tag to use.</summary>
public sealed class SearchPapersTool(HybridSearch search) : ScoutTool
{
    public override string Name => "search_papers";

    public override string Description =>
        "Searches the text of ingested papers (hybrid keyword + meaning search). Each result has the paper id, title, page and text, " +
        "plus a 'cite' tag like [arxiv:2106.15928 p.4] to put after any claim that uses it. Ingest papers first with ingest_paper.";

    protected override string SchemaJson => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "What to look for, in plain words" },
            "paperIds": { "type": "array", "items": { "type": "string" }, "description": "Optional: only search these papers (ids as returned by ingest_paper, e.g. 'arxiv:2106.15928')" },
            "limit": { "type": "integer", "description": "Results, 1-10 (default 6)" }
          },
          "required": ["query"]
        }
        """;

    public override async Task<ToolResult> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var query = StrArg(input, "query");
        if (query is null)
        {
            return ToolResult.Error("'query' is required.");
        }

        var paperIds = StrListArg(input, "paperIds").Select(NormalizeId).ToList();
        var hits = await search.SearchAsync(query, IntArg(input, "limit", 6, 1, 10), paperIds, cancellationToken).ConfigureAwait(false);

        if (hits.Count == 0)
        {
            return Json(new { query, count = 0, note = "Nothing found. The paper may not be ingested yet, or try other words." });
        }

        return Json(new
        {
            query,
            count = hits.Count,
            results = hits.Select(h => new
            {
                cite = $"[{h.PaperId} p.{h.Page}]",
                h.PaperId,
                title = h.PaperTitle,
                h.Page,
                text = Clip(h.Text, 1500),
            }),
        });
    }

    /// <summary>"arXiv:2106.15928" (Semantic Scholar style) → "arxiv:2106.15928" (our stored id).</summary>
    public static string NormalizeId(string id) =>
        id.StartsWith("arxiv:", StringComparison.OrdinalIgnoreCase) ? "arxiv:" + id[6..] : id;
}
