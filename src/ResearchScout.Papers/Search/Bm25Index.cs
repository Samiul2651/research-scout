using System.Text.RegularExpressions;

namespace ResearchScout.Papers.Search;

/// <summary>
/// In-memory BM25 keyword index. Good at exact terms (model names, acronyms, "MRR@10") that vector search can miss.
///
/// Score of a chunk for a query = Σ over query terms t of
///   IDF(t) × tf × (k1 + 1) / (tf + k1 × (1 − b + b × chunkLength / averageLength))
/// where tf = times t appears in the chunk, IDF(t) = ln(1 + (N − n + 0.5) / (n + 0.5)),
/// N = number of chunks, n = chunks containing t. k1 = 1.5, b = 0.75 (standard values).
///
/// Rebuilt from the chunks table on startup (the free host's disk is wiped on restart). Thread-safe.
/// </summary>
public sealed partial class Bm25Index
{
    private const double K1 = 1.5;
    private const double B = 0.75;

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "has", "in", "is", "it", "its", "of", "on",
        "or", "that", "the", "this", "to", "was", "were", "which", "with", "we", "our", "how", "what", "does", "do",
    ];

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Dictionary<string, int>> _termCounts = [];
    private readonly Dictionary<Guid, int> _lengths = [];
    private readonly Dictionary<Guid, string> _paperOf = [];
    private readonly Dictionary<string, int> _docFrequency = new(StringComparer.Ordinal);
    private long _totalLength;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _lengths.Count;
            }
        }
    }

    public void Add(Guid chunkId, string paperId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var terms = Tokenize(text);
        var counts = terms.GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        lock (_lock)
        {
            if (_termCounts.ContainsKey(chunkId))
            {
                return; // already indexed
            }

            _termCounts[chunkId] = counts;
            _lengths[chunkId] = terms.Count;
            _paperOf[chunkId] = paperId;
            _totalLength += terms.Count;
            foreach (var term in counts.Keys)
            {
                _docFrequency[term] = _docFrequency.GetValueOrDefault(term) + 1;
            }
        }
    }

    public IReadOnlyList<(Guid ChunkId, double Score)> Search(string query, int limit, IReadOnlyCollection<string>? paperIds = null)
    {
        var queryTerms = Tokenize(query).Distinct(StringComparer.Ordinal).ToList();

        lock (_lock)
        {
            var n = _lengths.Count;
            if (n == 0 || queryTerms.Count == 0)
            {
                return [];
            }

            var averageLength = (double)_totalLength / n;
            var idf = queryTerms
                .Where(_docFrequency.ContainsKey)
                .ToDictionary(t => t, t => Math.Log(1 + ((n - _docFrequency[t] + 0.5) / (_docFrequency[t] + 0.5))), StringComparer.Ordinal);

            return [.. _termCounts
                .Where(kv => paperIds is not { Count: > 0 } || paperIds.Contains(_paperOf[kv.Key]))
                .Select(kv =>
                {
                    var length = _lengths[kv.Key];
                    var score = 0.0;
                    foreach (var (term, termIdf) in idf)
                    {
                        if (kv.Value.TryGetValue(term, out var tf))
                        {
                            score += termIdf * tf * (K1 + 1) / (tf + (K1 * (1 - B + (B * length / averageLength))));
                        }
                    }

                    return (ChunkId: kv.Key, Score: score);
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(limit)];
        }
    }

    /// <summary>Lower-case words and numbers; keeps terms like "gpt-4", "mrr@10", "f1".</summary>
    public static List<string> Tokenize(string text) =>
        [.. Token().Matches(text.ToLowerInvariant()).Select(m => m.Value.Trim('-', '.')).Where(t => t.Length > 1 && !StopWords.Contains(t))];

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}@\-.]*")]
    private static partial Regex Token();
}
