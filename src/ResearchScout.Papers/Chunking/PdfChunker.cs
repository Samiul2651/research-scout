using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace ResearchScout.Papers.Chunking;

/// <summary>One piece of a paper's text, with the page it came from.</summary>
public sealed record TextChunk(int Index, int Page, string Text);

/// <summary>
/// Splits a PDF into chunks of about <see cref="ChunkWords"/> words with <see cref="OverlapWords"/> words of overlap.
/// Chunks never cross a page boundary, so each chunk has exactly one page number and citations are exact.
/// (A typical paper page has 500-900 words, so a page becomes 1-3 chunks.)
/// </summary>
public sealed partial class PdfChunker
{
    /// <summary>About 500 tokens (1 token ≈ 0.75 English words → 500 × 0.75 ≈ 375).</summary>
    public int ChunkWords { get; init; } = 375;

    /// <summary>About 50 tokens of overlap between neighbouring chunks on the same page.</summary>
    public int OverlapWords { get; init; } = 40;

    /// <summary>Chunks shorter than this many words are merged into the previous chunk on the same page.</summary>
    public int MinWords { get; init; } = 60;

    public IReadOnlyList<TextChunk> Chunk(byte[] pdf, out int pageCount)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        using var document = PdfDocument.Open(pdf);
        pageCount = document.NumberOfPages;

        var pages = document.GetPages().Select(p => (p.Number, Text: string.Join(' ', p.GetWords().Select(w => w.Text))));
        return ChunkPages(pages);
    }

    /// <summary>Chunks already-extracted page text. Separate from PDF parsing so it is easy to test.</summary>
    public IReadOnlyList<TextChunk> ChunkPages(IEnumerable<(int Page, string Text)> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (OverlapWords >= ChunkWords)
        {
            throw new InvalidOperationException("OverlapWords must be smaller than ChunkWords.");
        }

        var chunks = new List<TextChunk>();
        foreach (var (page, text) in pages)
        {
            var words = Whitespace().Split(Clean(text)).Where(w => w.Length > 0).ToArray();
            var pageChunks = new List<string>();

            for (var start = 0; start < words.Length; start += ChunkWords - OverlapWords)
            {
                var take = Math.Min(ChunkWords, words.Length - start);
                var piece = words.AsSpan(start, take);

                // A short tail is joined to the previous chunk instead of standing alone.
                if (take < MinWords && pageChunks.Count > 0)
                {
                    var tail = string.Join(' ', words.Skip(start + OverlapWords).Take(take - OverlapWords));
                    if (tail.Length > 0)
                    {
                        pageChunks[^1] += " " + tail;
                    }

                    break;
                }

                pageChunks.Add(string.Join(' ', piece.ToArray()));
                if (start + take >= words.Length)
                {
                    break;
                }
            }

            chunks.AddRange(pageChunks.Select(c => new TextChunk(0, page, c)));
        }

        // Re-number so indexes are 0..n-1 in reading order.
        return [.. chunks.Select((c, i) => c with { Index = i })];
    }

    /// <summary>Joins words split by a line-end hyphen ("retriev- al" → "retrieval") and drops control characters.</summary>
    private static string Clean(string text) => ControlChars().Replace(HyphenBreak().Replace(text, ""), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<=\p{Ll})- (?=\p{Ll})")]
    private static partial Regex HyphenBreak();

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]")]
    private static partial Regex ControlChars();
}
