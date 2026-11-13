using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ResearchScout.Harness;
using ResearchScout.Llm;
using ResearchScout.Scout;
using ResearchScout.Scout.Evaluation;

// dotnet run --project src/ResearchScout.Eval -- <command> [--model provider/model] [--eval-dir eval]
//   ingest      ingest the papers in eval/papers.json
//   retrieval   recall@5 of hybrid search (no LLM, free)
//   citations   run the agent on every question; citation accuracy (costs a few cents)
//   ranking     top-5 hit rate of professor ranking (eval/professors.json)
//   all         everything above

var command = args.Length > 0 ? args[0] : "help";
var model = Arg("--model");
var evalDir = Arg("--eval-dir") ?? FindEvalDir();

if (command is "help" or "-h" or "--help" || evalDir is null)
{
    Console.WriteLine("Usage: eval <ingest|retrieval|citations|ranking|all> [--model provider/model] [--eval-dir path]");
    return evalDir is null ? 1 : 0;
}

LoadDotEnv();
var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: false).Build();
var llmOptions = config.GetSection(LlmOptions.SectionName).Get<LlmOptions>() ?? new LlmOptions();
using var services = new ScoutServices(ScoutSettings.FromEnvironment(config), llmOptions, LlmProviders.ApiKeysFromEnvironment());
await services.InitializeAsync();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
var questions = JsonSerializer.Deserialize<QuestionFile>(File.ReadAllText(Path.Combine(evalDir, "questions.json")), json)!.Questions;
var report = new Dictionary<string, object> { ["date"] = DateTimeOffset.UtcNow, ["questions"] = questions.Count };

if (command is "ingest" or "all")
{
    var papers = JsonSerializer.Deserialize<PaperFile>(File.ReadAllText(Path.Combine(evalDir, "papers.json")), json)!.Papers;
    foreach (var id in papers)
    {
        var r = await services.Ingestor.IngestAsync(id, CancellationToken.None);
        Console.WriteLine(r.Success ? $"✓ {id}: {r.Title} ({r.Pages} pages, {r.Chunks} chunks{(r.AlreadyIngested ? ", already" : "")})" : $"✗ {id}: {r.Error}");
    }
}

if (command is "retrieval" or "all")
{
    const int k = 5;
    var answerable = questions.Where(q => !q.Unanswerable).ToList();
    var hits = 0;
    foreach (var q in answerable)
    {
        var results = await services.Search.SearchAsync(q.Question, k);
        var hit = EvalScoring.RetrievalHit(q, results.Select(r => (r.PaperId, r.Page, r.Text)));
        hits += hit ? 1 : 0;
        Console.WriteLine($"{(hit ? "✓" : "✗")} {q.Id}");
    }

    // recall@5 = questions whose evidence page is in the top 5 ÷ answerable questions
    var recall = answerable.Count == 0 ? 0 : (double)hits / answerable.Count;
    Console.WriteLine($"Retrieval recall@{k}: {hits}/{answerable.Count} = {recall:P0}");
    report["retrievalRecallAt5"] = recall;
}

if (command is "citations" or "all")
{
    var selection = model is null ? await services.Selector.GetCurrentAsync() : ParseModel(model);
    if (!services.Catalog.IsAvailable(selection))
    {
        Console.Error.WriteLine($"Model {selection} is not available (missing API key?).");
        return 1;
    }

    var pageCache = await LoadPagesAsync(services);
    var scores = new List<QuestionScore>();
    var cost = 0m;

    foreach (var q in questions)
    {
        var runId = Guid.NewGuid();
        var loop = services.CreateLoop(runId, DenyAllApprovalGate.Instance);
        var result = await loop.RunAsync(q.Question, services.CreateRunOptions(runId, selection, stream: false));
        cost += result.CostUsd;

        var score = EvalScoring.Score(q, result.FinalText, c => pageCache.GetValueOrDefault((c.PaperId, c.Page)));
        scores.Add(score);
        Console.WriteLine(
            $"{q.Id,-8} citations {score.Citations} · correct {score.CorrectCitations} · invalid {score.InvalidCitations}" +
            (score.RefusedCorrectly is { } r ? $" · refused correctly: {r}" : "") + $" · {result.Status} · trace {runId}");

        if (result.Status == AgentRunStatus.SpendLimitReached)
        {
            break;
        }
    }

    var summary = EvalScoring.Summarize(questions, scores);
    Console.WriteLine();
    Console.WriteLine($"Model: {selection}   Total cost: ${cost.ToString("0.0000", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Citation accuracy (correct ÷ all citations):        {summary.CitationAccuracy:P0}");
    Console.WriteLine($"Questions with ≥1 correct citation:                 {summary.QuestionsWithCorrectCitation:P0} of {summary.AnswerableQuestions}");
    Console.WriteLine($"Questions with any citation:                        {summary.QuestionsWithAnyCitation:P0}");
    Console.WriteLine($"Invented citations (page not in ingested papers):   {summary.InvalidCitations}");
    if (summary.RefusalAccuracy is { } refusal)
    {
        Console.WriteLine($"Unanswerable questions answered 'Not found':        {refusal:P0}");
    }

    report["model"] = selection.ToString();
    report["costUsd"] = cost;
    report["citations"] = summary;
    report["perQuestion"] = scores;
}

if (command is "ranking" or "all")
{
    var cases = JsonSerializer.Deserialize<ProfessorFile>(File.ReadAllText(Path.Combine(evalDir, "professors.json")), json)!.Cases
        .Where(c => c.Candidates.Count > 0 && c.KnownGoodFit.Count > 0).ToList();

    if (cases.Count == 0)
    {
        Console.WriteLine("Ranking eval skipped: fill in 'candidates' and 'knownGoodFit' in eval/professors.json.");
    }

    var rates = new List<double>();
    foreach (var c in cases)
    {
        // Resolve names to Semantic Scholar ids (preferring the right university), then rank with the fit scorer.
        var idToName = new Dictionary<string, string>();
        foreach (var name in c.Candidates)
        {
            var matches = await services.Scholar.SearchAuthorsAsync(name, 5, CancellationToken.None);
            var best = matches.FirstOrDefault(a => a.Affiliations.Any(x => x.Contains(c.University.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
                ?? (matches.Count > 0 ? matches[0] : null);
            if (best is not null)
            {
                idToName[best.AuthorId] = name;
            }
        }

        var ranked = new List<FitScore>();
        foreach (var batch in idToName.Keys.Chunk(10))
        {
            ranked.AddRange(await services.FitScorer.ScoreAsync(batch, CancellationToken.None));
        }

        var rankedNames = ranked.OrderByDescending(r => r.Score).Select(r => idToName[r.AuthorId]).ToList();
        var rate = EvalScoring.TopKHitRate(rankedNames, c.KnownGoodFit, 5);
        rates.Add(rate);
        Console.WriteLine($"{c.University}: top 5 = {string.Join(", ", rankedNames.Take(5))}");
        Console.WriteLine($"  known good fits in top 5: {rate:P0}");
    }

    if (rates.Count > 0)
    {
        report["rankingTop5HitRate"] = rates.Average();
    }
}

var outFile = Path.Combine(evalDir, "results", $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{command}.json");
File.WriteAllText(outFile, JsonSerializer.Serialize(report, json));
Console.WriteLine($"\nSaved {outFile}");
return 0;

string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

static LlmSelection ParseModel(string m) => m.Split('/', 2) is [var p, var id] ? new LlmSelection(p, id) : new LlmSelection("", m);

static string? FindEvalDir()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "eval");
        if (File.Exists(Path.Combine(candidate, "questions.json")))
        {
            return candidate;
        }
    }

    return null;
}

static void LoadDotEnv()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        var path = Path.Combine(dir.FullName, ".env");
        if (!File.Exists(path))
        {
            continue;
        }

        foreach (var line in File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && Environment.GetEnvironmentVariable(line[..eq].Trim()) is null && line[(eq + 1)..].Trim().Trim('"') is { Length: > 0 } value)
            {
                Environment.SetEnvironmentVariable(line[..eq].Trim(), value);
            }
        }

        return;
    }
}

/// <summary>Text of every (paper, page), joined from its chunks. Small enough to load at once for 5 papers.</summary>
static async Task<Dictionary<(string, int), string>> LoadPagesAsync(ScoutServices services)
{
    await using var db = await services.DbFactory.CreateDbContextAsync();
    var chunks = await db.Chunks.AsNoTracking().Select(c => new { c.PaperId, c.Page, c.Index, c.Text }).ToListAsync();
    return chunks.GroupBy(c => (c.PaperId.ToLowerInvariant(), c.Page))
        .ToDictionary(g => g.Key, g => string.Join(" ", g.OrderBy(c => c.Index).Select(c => c.Text)));
}

internal sealed record QuestionFile(List<EvalQuestion> Questions);

internal sealed record PaperFile(List<string> Papers);

internal sealed record ProfessorFile(List<ProfessorCase> Cases);

internal sealed record ProfessorCase(string University, string Topic, List<string> Candidates, List<string> KnownGoodFit);
