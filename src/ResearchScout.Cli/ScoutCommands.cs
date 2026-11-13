using System.Globalization;
using ResearchScout.Harness;
using ResearchScout.Llm;
using ResearchScout.Scout;

namespace ResearchScout.Cli;

/// <summary>scout ask | scout ingest | scout search — the real Research Scout features.</summary>
internal static class ScoutCommands
{
    public static async Task<int> RunAsync(string[] args, ScoutServices services)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var loaded = await services.InitializeAsync(cts.Token);
        if (services.UsingInMemoryVectors)
        {
            Console.Error.WriteLine("Note: QDRANT_URL is not set, so vectors are kept in memory for this run only.");
        }

        try
        {
            return args switch
            {
                ["ingest", var paperId, ..] => await IngestAsync(services, paperId, cts.Token),
                ["search", var query, ..] => await SearchAsync(services, query, cts.Token),
                ["ask", ..] => await AskAsync(services, args[1..], cts.Token),
                _ => Usage(),
            };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or Grpc.Core.RpcException or TimeoutException)
        {
            // Expected problems (missing key, network, service down): a short message, not a stack trace.
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }

        static int Usage()
        {
            Console.Error.WriteLine("Usage: scout ask \"question\" | scout ingest <paperId> | scout search \"query\"");
            return 1;
        }
    }

    private static async Task<int> IngestAsync(ScoutServices services, string paperId, CancellationToken ct)
    {
        var result = await services.Ingestor.IngestAsync(paperId, ct);
        if (!result.Success)
        {
            Console.Error.WriteLine(result.Error);
            return 2;
        }

        Console.WriteLine($"{(result.AlreadyIngested ? "Already ingested" : "Ingested")}: {result.Title}");
        Console.WriteLine($"  id {result.PaperId} · {result.Pages} pages · {result.Chunks} chunks");
        return 0;
    }

    private static async Task<int> SearchAsync(ScoutServices services, string query, CancellationToken ct)
    {
        var hits = await services.Search.SearchAsync(query, 6, cancellationToken: ct);
        if (hits.Count == 0)
        {
            Console.WriteLine("Nothing found. Ingest papers first with 'scout ingest'.");
        }

        foreach (var h in hits)
        {
            var text = h.Text.Length > 300 ? h.Text[..300] + "…" : h.Text;
            Console.WriteLine($"[{h.PaperId} p.{h.Page}] {h.PaperTitle}  (score {h.Score.ToString("0.0000", CultureInfo.InvariantCulture)})");
            Console.WriteLine($"   {text}");
            Console.WriteLine();
        }

        return 0;
    }

    private static async Task<int> AskAsync(ScoutServices services, string[] askArgs, CancellationToken ct)
    {
        string? question = null;
        string? model = null;
        for (var i = 0; i < askArgs.Length; i++)
        {
            if (askArgs[i] == "--model" && i + 1 < askArgs.Length)
            {
                model = askArgs[++i];
            }
            else
            {
                question ??= askArgs[i];
            }
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            Console.Error.WriteLine("Usage: scout ask \"question\" [--model provider/model]");
            return 1;
        }

        LlmSelection selection;
        if (model is not null)
        {
            var parts = model.Split('/', 2);
            selection = parts.Length == 2 ? new LlmSelection(parts[0], parts[1]) : new LlmSelection("", model);
            if (!services.Catalog.IsAvailable(selection))
            {
                Console.Error.WriteLine($"Model '{model}' is not available. Run 'scout models'.");
                return 1;
            }
        }
        else
        {
            selection = await services.Selector.GetCurrentAsync(ct);
        }

        var runId = Guid.NewGuid();
        var printer = new ConsolePrinter();
        var loop = services.CreateLoop(runId, new ConsoleApprovalGate(), printer);

        Console.WriteLine($"Model: {selection}");
        Console.WriteLine();
        var result = await loop.RunAsync(question, services.CreateRunOptions(runId, selection, stream: true), ct);

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine(
            $"{result.Status} · {result.Steps.Count} step(s) · {result.Usage.InputTokenCount ?? 0} in / {result.Usage.OutputTokenCount ?? 0} out · " +
            $"${result.CostUsd.ToString("0.000000", CultureInfo.InvariantCulture)}");
        if (result.StopReason is not null)
        {
            Console.WriteLine(result.StopReason);
        }

        Console.WriteLine($"Trace: scout trace {result.RunId}");
        return result.Status == AgentRunStatus.Completed ? 0 : 2;
    }

    /// <summary>Prints streamed text and tool activity as it happens.</summary>
    private sealed class ConsolePrinter : IAgentObserver
    {
        public ValueTask OnTextDeltaAsync(AgentRunContext run, string text, CancellationToken cancellationToken)
        {
            Console.Write(text);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnToolStartedAsync(AgentRunContext run, int stepNumber, string toolName, string inputJson, CancellationToken cancellationToken)
        {
            var input = inputJson.Length > 120 ? inputJson[..120] + "…" : inputJson;
            Console.WriteLine();
            Console.WriteLine($"  → {toolName} {input}");
            return ValueTask.CompletedTask;
        }
    }
}
