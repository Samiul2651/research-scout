using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Qdrant.Client;
using ResearchScout.Harness;
using ResearchScout.Harness.Tools;
using ResearchScout.Infra;
using ResearchScout.Llm;
using ResearchScout.Papers;
using ResearchScout.Papers.Chunking;
using ResearchScout.Papers.Fetching;
using ResearchScout.Papers.Search;
using ResearchScout.Scout.Tools;

namespace ResearchScout.Scout;

/// <summary>Settings read from environment variables (secrets) and appsettings.json (everything else).</summary>
public sealed record ScoutSettings
{
    public required string DatabaseUrl { get; init; }
    public string? VoyageApiKey { get; init; }
    public string? QdrantUrl { get; init; }
    public string? QdrantApiKey { get; init; }
    public string? SemanticScholarApiKey { get; init; }
    public string EmbeddingModel { get; init; } = "voyage-4-lite";
    public int EmbeddingDimensions { get; init; } = 1024;
    public string QdrantCollection { get; init; } = "chunks";
    public required string InterestProfile { get; init; }

    /// <summary>Reads env vars + the "Embeddings" config section + profile.md (next to the app, or ProfilePath).</summary>
    public static ScoutSettings FromEnvironment(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

        var profilePath = config["ProfilePath"] ?? Path.Combine(AppContext.BaseDirectory, "profile.md");
        var profile = File.Exists(profilePath)
            ? File.ReadAllText(profilePath)
            : "Retrieval-augmented generation for code; code intelligence; LLMs for software engineering; event-driven systems.";

        return new ScoutSettings
        {
            DatabaseUrl = Env("DATABASE_URL") ?? throw new InvalidOperationException("DATABASE_URL is not set."),
            VoyageApiKey = Env("VOYAGE_API_KEY"),
            QdrantUrl = Env("QDRANT_URL"),
            QdrantApiKey = Env("QDRANT_API_KEY"),
            SemanticScholarApiKey = Env("SEMANTIC_SCHOLAR_API_KEY"),
            EmbeddingModel = config["Embeddings:Model"] ?? "voyage-4-lite",
            EmbeddingDimensions = int.TryParse(config["Embeddings:Dimensions"], out var d) ? d : 1024,
            InterestProfile = profile,
        };
    }
}

/// <summary>
/// Composition root shared by the CLI and the API: builds the database, LLM, paper pipeline and tools once,
/// and makes a fresh agent loop per run.
/// </summary>
public sealed class ScoutServices : IDisposable
{
    private readonly SocketsHttpHandler _httpHandler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(15) };
    private readonly HttpClient _scholarHttp;
    private readonly HttpClient _pdfHttp;
    private readonly HttpClient _voyageHttp;
    private readonly QdrantClient? _qdrant;

    public ScoutServices(ScoutSettings settings, LlmOptions llmOptions, IReadOnlyDictionary<string, string> llmKeys)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        LlmOptions = llmOptions;

        DbFactory = ScoutDb.CreateFactory(settings.DatabaseUrl);

        // LLM
        Catalog = new ModelCatalog(llmOptions, llmKeys);
        Selector = new LlmSelector(Catalog, new PostgresLlmSettingsStore(DbFactory));
        Chat = new SwitchableChatClient(Selector, new ProviderChatClientFactory(llmOptions, llmKeys));

        // HTTP clients share one connection pool; each has its own timeout.
        _scholarHttp = new HttpClient(_httpHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        _pdfHttp = new HttpClient(_httpHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(90) };
        _voyageHttp = new HttpClient(_httpHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };

        // Papers
        Scholar = new SemanticScholarClient(_scholarHttp, new PostgresApiCache(DbFactory), settings.SemanticScholarApiKey);
        Embedder = settings.VoyageApiKey is null
            ? new MissingKeyEmbedder()
            : new VoyageEmbeddingGenerator(_voyageHttp, settings.VoyageApiKey, settings.EmbeddingModel, settings.EmbeddingDimensions);

        if (settings.QdrantUrl is not null)
        {
            _qdrant = CreateQdrantClient(settings.QdrantUrl, settings.QdrantApiKey);
            Vectors = new QdrantVectorStore(_qdrant, settings.QdrantCollection);
        }
        else
        {
            Vectors = new InMemoryVectorStore();
            UsingInMemoryVectors = true;
        }

        Keywords = new Bm25Index();
        Search = new HybridSearch(Embedder, Vectors, Keywords, new PostgresChunkLookup(DbFactory));
        Ingestor = new PaperIngestor(new RemotePaperSource(Scholar, new PdfDownloader(_pdfHttp)), new PdfChunker(), Embedder, Vectors, Keywords, DbFactory);
        FitScorer = new FitScorer(Scholar, Embedder, settings.InterestProfile);
    }

    public ScoutSettings Settings { get; }
    public LlmOptions LlmOptions { get; }
    public IDbContextFactory<ScoutDbContext> DbFactory { get; }
    public ModelCatalog Catalog { get; }
    public LlmSelector Selector { get; }
    public SwitchableChatClient Chat { get; }
    public SemanticScholarClient Scholar { get; }
    public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; }
    public IVectorStore Vectors { get; }
    public Bm25Index Keywords { get; }
    public HybridSearch Search { get; }
    public PaperIngestor Ingestor { get; }
    public FitScorer FitScorer { get; }

    /// <summary>True when QDRANT_URL is not set: vectors are kept in memory and lost on restart (fine for a quick local try).</summary>
    public bool UsingInMemoryVectors { get; }

    /// <summary>Creates/updates tables and loads the keyword index. Call once at startup.</summary>
    public async Task<int> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ScoutDb.MigrateAsync(DbFactory, cancellationToken).ConfigureAwait(false);
        return await PaperIngestor.LoadKeywordIndexAsync(DbFactory, Keywords, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The six Scout tools. <paramref name="runId"/> lets draft_email link drafts to their run.</summary>
    public ToolRegistry CreateTools(Func<Guid?> runId) => new ToolRegistry()
        .Add(new FindProfessorsTool(Scholar))
        .Add(new GetRecentPapersTool(Scholar))
        .Add(new IngestPaperTool(Ingestor))
        .Add(new SearchPapersTool(Search))
        .Add(new ScoreFitTool(FitScorer))
        .Add(new DraftEmailTool(Scholar, DbFactory, runId));

    /// <summary>A loop for one run, with the Scout tools, pricing, retries, tracing and the given gate/guard.</summary>
    public AgentLoop CreateLoop(Guid runId, IApprovalGate approvalGate, IAgentObserver? extraObserver = null, ISpendGuard? spendGuard = null)
    {
        IAgentObserver trace = new PostgresTraceStore(DbFactory);
        return new AgentLoop(Chat, CreateTools(() => runId), new AgentLoopServices
        {
            CostCalculator = new CatalogCostCalculator(Catalog),
            RetryPolicy = ProviderErrorClassifier.CreateRetryPolicy(),
            Observer = extraObserver is null ? trace : new CompositeObserver(trace, extraObserver),
            ApprovalGate = approvalGate,
            SpendGuard = spendGuard,
        });
    }

    /// <summary>Run options pinned to one model, with the Scout system prompt.</summary>
    public AgentRunOptions CreateRunOptions(Guid runId, LlmSelection model, bool stream, RunBudget? budget = null) => new()
    {
        RunId = runId,
        Stream = stream,
        MaxSteps = 12,
        MaxOutputTokens = 2048,
        SystemPrompt = ScoutPrompts.System(Settings.InterestProfile),
        Budget = budget ?? new RunBudget(),
        BaseChatOptions = new ChatOptions { AdditionalProperties = new() { [SwitchableChatClient.PinnedSelectionKey] = model } },
    };

    /// <summary>
    /// Qdrant URL as shown in Qdrant Cloud (https://xyz.cloud.qdrant.io:6333) or local (http://localhost:6334).
    /// The client uses gRPC, so the REST port 6333 is switched to the gRPC port 6334.
    /// </summary>
    public static QdrantClient CreateQdrantClient(string url, string? apiKey)
    {
        var uri = new Uri(url);
        var port = uri.IsDefaultPort || uri.Port == 6333 ? 6334 : uri.Port;
        return new QdrantClient(uri.Host, port, https: uri.Scheme == Uri.UriSchemeHttps, apiKey: apiKey);
    }

    public void Dispose()
    {
        Chat.Dispose();
        _qdrant?.Dispose();
        _scholarHttp.Dispose();
        _pdfHttp.Dispose();
        _voyageHttp.Dispose();
        _httpHandler.Dispose();
    }

    /// <summary>Stands in when VOYAGE_API_KEY is missing, so the app still starts and tools explain the problem.</summary>
    private sealed class MissingKeyEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("VOYAGE_API_KEY is not set, so papers can't be embedded or searched.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
