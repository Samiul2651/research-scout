using System.Text.RegularExpressions;
using ResearchScout.Harness;
using ResearchScout.Harness.Tests;
using ResearchScout.Harness.Tools;
using ResearchScout.Papers.Search;
using ResearchScout.Papers.Tests;
using ResearchScout.Scout.Tools;
using static ResearchScout.Harness.Tests.ScriptedChatClient;

namespace ResearchScout.Scout.Tests;

public class EndToEndTests
{
    [Fact]
    public async Task Agent_SearchesPapers_AndAnswersWithACitationFromTheTool()
    {
        // A tiny index with one chunk on page 7.
        var embedder = new HashEmbedder();
        var vectors = new InMemoryVectorStore();
        var bm25 = new Bm25Index();
        var id = Guid.NewGuid();
        const string text = "Retrieval is evaluated with mean reciprocal rank (MRR@10) on CodeSearchNet.";
        await vectors.UpsertAsync([new VectorPoint(id, (await embedder.GenerateAsync([text]))[0].Vector, "arxiv:2501.00001", 7)], default);
        bm25.Add(id, "arxiv:2501.00001", text);
        var search = new HybridSearch(embedder, vectors, bm25, new Lookup(id, text));

        // The scripted model: 1) calls search_papers, 2) answers using the tag the tool returned.
        var chat = new ScriptedChatClient(
            Calls(Call("c1", "search_papers", new { query = "how is retrieval evaluated" })),
            Text("They use MRR@10 on CodeSearchNet [arxiv:2501.00001 p.7]."));

        var result = await new AgentLoop(chat, new ToolRegistry().Add(new SearchPapersTool(search)))
            .RunAsync("How does paper X evaluate retrieval?", new AgentRunOptions { SystemPrompt = ScoutPrompts.System("profile") });

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        var toolOutput = result.Steps[0].ToolCalls[0].Output;
        Assert.Contains("[arxiv:2501.00001 p.7]", toolOutput, StringComparison.Ordinal);

        // Every citation in the answer must be one the tool actually returned.
        var cited = Regex.Matches(result.FinalText, ScoutPrompts.CitationPattern).Select(m => m.Value).ToList();
        Assert.NotEmpty(cited);
        Assert.All(cited, c => Assert.Contains(c, toolOutput, StringComparison.Ordinal));
    }

    private sealed class Lookup(Guid id, string text) : IChunkLookup
    {
        public Task<IReadOnlyDictionary<Guid, (string PaperId, string Title, int Page, string Text)>> GetAsync(
            IReadOnlyCollection<Guid> chunkIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, (string, string, int, string)>>(
                new Dictionary<Guid, (string, string, int, string)> { [id] = ("arxiv:2501.00001", "RAG for Code", 7, text) });
    }
}
