namespace ResearchScout.Infra;

/// <summary>A paper that has been ingested (table: papers).</summary>
public sealed class PaperEntity
{
    /// <summary>Stable id, e.g. "arxiv:2401.01234".</summary>
    public required string Id { get; set; }

    public required string Title { get; set; }
    public string? Authors { get; set; }
    public int? Year { get; set; }
    public string? Url { get; set; }
    public int PageCount { get; set; }
    public DateTimeOffset IngestedAt { get; set; }
}

/// <summary>
/// One searchable piece of a paper (table: chunks). Text is stored here so the BM25 index can be rebuilt on
/// startup (the free host's disk is wiped on restart); vectors live in Qdrant.
/// </summary>
public sealed class ChunkEntity
{
    /// <summary>Same id as the Qdrant point.</summary>
    public Guid Id { get; set; }

    public required string PaperId { get; set; }

    /// <summary>Position of the chunk in the paper (0, 1, 2, ...).</summary>
    public int Index { get; set; }

    /// <summary>1-based page the chunk starts on (used for citations).</summary>
    public int Page { get; set; }

    public required string Text { get; set; }
}
