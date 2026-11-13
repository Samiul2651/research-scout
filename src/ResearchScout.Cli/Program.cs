using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ResearchScout.Cli;
using ResearchScout.Harness;
using ResearchScout.Infra;
using ResearchScout.Llm;
using ResearchScout.Scout;

// scout models
// scout chat "question" [--model provider/model] [--max-steps 12] [--max-cost 0.05]
// scout runs | scout trace <runId> | scout db migrate      (need DATABASE_URL)
// scout ask "question" | scout ingest <paperId> | scout search "query"   (need DATABASE_URL + VOYAGE_API_KEY)

var envFile = DotEnv.Load();

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var llmOptions = config.GetSection(LlmOptions.SectionName).Get<LlmOptions>() ?? new LlmOptions();
var apiKeys = LlmProviders.ApiKeysFromEnvironment();
var catalog = new ModelCatalog(llmOptions, apiKeys);
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
var dbFactory = string.IsNullOrWhiteSpace(databaseUrl) ? null : ScoutDb.CreateFactory(databaseUrl);
var selector = new LlmSelector(catalog, dbFactory is null ? new InMemoryLlmSettingsStore() : new PostgresLlmSettingsStore(dbFactory));

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

if (args[0] is "runs" or "trace" or "db")
{
    if (dbFactory is null)
    {
        Console.Error.WriteLine("Set DATABASE_URL first (e.g. in .env). Local dev: start Postgres with 'docker compose up -d'.");
        return 1;
    }

    return await TraceCommands.RunAsync(args, dbFactory);
}

if (args[0] is "ask" or "ingest" or "search")
{
    if (dbFactory is null)
    {
        Console.Error.WriteLine("Set DATABASE_URL first (e.g. in .env). Local dev: start Postgres and Qdrant with 'docker compose up -d'.");
        return 1;
    }

    using var services = new ScoutServices(ScoutSettings.FromEnvironment(config), llmOptions, apiKeys);
    return await ScoutCommands.RunAsync(args, services);
}

if (catalog.Available.Count == 0)
{
    Console.Error.WriteLine("No LLM API key found. Set one of these (in your shell or a .env file):");
    foreach (var provider in LlmProviders.All)
    {
        Console.Error.WriteLine($"  {LlmProviders.ApiKeyEnvVar(provider)}");
    }

    return 1;
}

switch (args[0])
{
    case "models":
        var current = await selector.GetCurrentAsync();
        Console.WriteLine($"Keys found for: {string.Join(", ", apiKeys.Keys)}{(envFile is null ? "" : $"  (loaded {envFile})")}");
        foreach (var m in catalog.Available)
        {
            var mark = m.Provider == current.Provider && m.ModelId == current.ModelId ? "*" : " ";
            Console.WriteLine(
                $" {mark} {m.Provider + "/" + m.ModelId,-36} {m.DisplayName,-24} ${m.InputUsdPerMillion}/M in, ${m.OutputUsdPerMillion}/M out");
        }

        Console.WriteLine(" * = default");
        return 0;

    case "chat":
        return await RunChatAsync(args[1..]);

    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        PrintHelp();
        return 1;
}

async Task<int> RunChatAsync(string[] chatArgs)
{
    string? question = null;
    string? model = null;
    var maxSteps = 12;
    var maxCost = 0.05m;

    for (var i = 0; i < chatArgs.Length; i++)
    {
        switch (chatArgs[i])
        {
            case "--model" when i + 1 < chatArgs.Length:
                model = chatArgs[++i];
                break;
            case "--max-steps" when i + 1 < chatArgs.Length && int.TryParse(chatArgs[i + 1], out var steps):
                maxSteps = steps;
                i++;
                break;
            case "--max-cost" when i + 1 < chatArgs.Length
                && decimal.TryParse(chatArgs[i + 1], NumberStyles.Number, CultureInfo.InvariantCulture, out var cost):
                maxCost = cost;
                i++;
                break;
            default:
                question ??= chatArgs[i];
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(question))
    {
        Console.Error.WriteLine("Usage: scout chat \"your question\" [--model provider/model]");
        return 1;
    }

    // One run = one model. Pick it now and pin it, so the whole run stays on it.
    LlmSelection selection;
    if (model is not null)
    {
        var parts = model.Split('/', 2);
        selection = parts.Length == 2 ? new LlmSelection(parts[0], parts[1]) : new LlmSelection("", model);
        if (!catalog.IsAvailable(selection))
        {
            Console.Error.WriteLine($"Model '{model}' is not available. Run 'scout models' to see the list (format: provider/model).");
            return 1;
        }
    }
    else
    {
        selection = await selector.GetCurrentAsync();
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    using var chat = new SwitchableChatClient(selector, new ProviderChatClientFactory(llmOptions, apiKeys));
    var loop = new AgentLoop(chat, DemoTools.Registry(), new AgentLoopServices
    {
        CostCalculator = new CatalogCostCalculator(catalog),
        RetryPolicy = ProviderErrorClassifier.CreateRetryPolicy(),
        Observer = dbFactory is null ? null : new PostgresTraceStore(dbFactory),
        ApprovalGate = new ConsoleApprovalGate(),
    });

    var runOptions = new AgentRunOptions
    {
        MaxSteps = maxSteps,
        SystemPrompt = "You are a helpful assistant. Use the tools when they help, especially for arithmetic and the current time. Be brief.",
        Budget = new RunBudget { MaxCostUsd = maxCost },
        BaseChatOptions = new ChatOptions
        {
            AdditionalProperties = new() { [SwitchableChatClient.PinnedSelectionKey] = selection },
        },
    };

    Console.WriteLine($"Model: {selection}   Budget: ${maxCost} / {maxSteps} steps");
    Console.WriteLine();

    AgentRunResult result;
    try
    {
        result = await loop.RunAsync(question, runOptions, cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        return 130;
    }

    foreach (var step in result.Steps)
    {
        var usage = step.Usage;
        var tries = step.Attempts > 1 ? $" ({step.Attempts} tries)" : "";
        Console.WriteLine(
            $"Step {step.Number} · {usage?.InputTokenCount ?? 0} in / {usage?.OutputTokenCount ?? 0} out · " +
            $"${step.CostUsd:0.000000} · {step.ModelDuration.TotalMilliseconds:0} ms{tries}");

        foreach (var call in step.ToolCalls)
        {
            var mark = call.IsError ? "✗" : "→";
            Console.WriteLine($"   {mark} {call.ToolName}({call.InputJson}) = {call.Output}");
        }
    }

    Console.WriteLine();
    Console.WriteLine(string.IsNullOrWhiteSpace(result.FinalText) ? "(no answer text)" : result.FinalText);
    Console.WriteLine();
    Console.WriteLine(
        $"{result.Status} · {result.Steps.Count} step(s) · " +
        $"{result.Usage.InputTokenCount ?? 0} in / {result.Usage.OutputTokenCount ?? 0} out · total ${result.CostUsd:0.000000}");

    if (result.StopReason is not null)
    {
        Console.WriteLine(result.StopReason);
    }

    Console.WriteLine(dbFactory is null ? "(trace not saved: DATABASE_URL not set)" : $"Trace: scout trace {result.RunId}");

    return result.Status == AgentRunStatus.Completed ? 0 : 2;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Research Scout CLI

          scout models                       List models you can use (needs API keys)
          scout chat "question" [options]    Run the agent loop with demo tools (time, calculator)
          scout ask "question" [--model p/m] Ask Research Scout (papers + professors)
          scout ingest <paperId>             Ingest a paper, e.g. arXiv:2106.15928
          scout search "query"               Search ingested papers (no LLM)
          scout runs                         List recent runs            (needs DATABASE_URL)
          scout trace <runId>                Print a run step by step    (needs DATABASE_URL)
          scout db migrate                   Create/update the database tables

        Options for chat:
          --model provider/model   e.g. gemini/gemini-3.5-flash-lite (default: the configured default)
          --max-steps N            Most model calls in the run (default 12)
          --max-cost USD           Cost limit for the run (default 0.05)

        API keys are read from environment variables or a .env file:
          ANTHROPIC_API_KEY, GEMINI_API_KEY, DEEPSEEK_API_KEY, OPENAI_API_KEY
          DATABASE_URL, VOYAGE_API_KEY, QDRANT_URL (+ QDRANT_API_KEY), SEMANTIC_SCHOLAR_API_KEY (optional)
        """);
}
