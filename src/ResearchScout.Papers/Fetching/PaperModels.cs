namespace ResearchScout.Papers.Fetching;

/// <summary>A researcher from Semantic Scholar.</summary>
public sealed record AuthorInfo(
    string AuthorId,
    string Name,
    IReadOnlyList<string> Affiliations,
    int? HIndex,
    int? PaperCount,
    string? Url);

/// <summary>A paper from Semantic Scholar.</summary>
public sealed record PaperInfo(
    string PaperId,
    string Title,
    int? Year,
    string? Abstract,
    string? Venue,
    IReadOnlyList<string> AuthorNames,
    string? ArxivId,
    string? PdfUrl,
    string? Url)
{
    /// <summary>Stable id used in our database and citations: "arxiv:ID" when there is one, else "s2:ID".</summary>
    public string StableId => ArxivId is not null ? $"arxiv:{ArxivId}" : $"s2:{PaperId}";
}
