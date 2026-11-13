using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ResearchScout.Infra;

namespace ResearchScout.Cli;

/// <summary>scout runs | scout trace &lt;runId&gt; | scout db migrate</summary>
internal static class TraceCommands
{
    public static async Task<int> RunAsync(string[] args, IDbContextFactory<ScoutDbContext> db)
    {
        switch (args)
        {
            case ["db", "migrate"]:
                await ScoutDb.MigrateAsync(db);
                Console.WriteLine("Database is up to date.");
                return 0;

            case ["runs", ..]:
                var runs = await new TraceReader(db).ListRecentAsync(20);
                if (runs.Count == 0)
                {
                    Console.WriteLine("No runs yet.");
                }

                foreach (var r in runs)
                {
                    var question = r.UserMessage.Length > 60 ? r.UserMessage[..60] + "…" : r.UserMessage;
                    Console.WriteLine($"{r.Id}  {r.StartedAt:yyyy-MM-dd HH:mm}  {r.Status,-18} ${r.CostUsd:0.0000}  {question}");
                }

                return 0;

            case ["trace", var id] when Guid.TryParse(id, out var runId):
                var run = await new TraceReader(db).GetRunAsync(runId);
                if (run is null)
                {
                    Console.Error.WriteLine($"No run with id {runId}.");
                    return 1;
                }

                Print(run);
                return 0;

            default:
                Console.Error.WriteLine("Usage: scout runs | scout trace <runId> | scout db migrate");
                return 1;
        }
    }

    private static void Print(RunEntity run)
    {
        Console.WriteLine($"Run {run.Id}");
        Console.WriteLine($"  Question: {run.UserMessage}");
        Console.WriteLine($"  Model:    {(run.Provider is null ? "-" : $"{run.Provider}/{run.Model}")}");
        Console.WriteLine($"  Status:   {run.Status}{(run.StopReason is null ? "" : $" — {run.StopReason}")}");
        Console.WriteLine($"  Started:  {run.StartedAt:u}   Took: {((run.FinishedAt ?? run.StartedAt) - run.StartedAt).TotalSeconds:0.0}s");
        Console.WriteLine($"  Tokens:   {run.InputTokens} in / {run.OutputTokens} out   Cost: ${run.CostUsd:0.000000}");
        Console.WriteLine();

        foreach (var step in run.Steps)
        {
            var tries = step.Attempts > 1 ? $" ({step.Attempts} tries)" : "";
            Console.WriteLine($"Step {step.Number} · {step.InputTokens} in / {step.OutputTokens} out · ${step.CostUsd:0.000000} · {step.DurationMs} ms{tries}");

            if (!string.IsNullOrWhiteSpace(step.TextPreview))
            {
                Console.WriteLine($"   text: {step.TextPreview.ReplaceLineEndings(" ")}");
            }

            foreach (var call in JsonDocument.Parse(step.ToolCallsJson).RootElement.EnumerateArray())
            {
                var isError = call.GetProperty("isError").GetBoolean();
                var approved = call.TryGetProperty("approved", out var a) && a.ValueKind != JsonValueKind.Null
                    ? (a.GetBoolean() ? " [approved]" : " [rejected]")
                    : "";
                Console.WriteLine(
                    $"   {(isError ? "✗" : "→")} {call.GetProperty("tool").GetString()}({call.GetProperty("input").GetString()}){approved} " +
                    $"= {call.GetProperty("output").GetString()?.ReplaceLineEndings(" ")}  ({call.GetProperty("ms").GetInt32()} ms)");
            }
        }

        if (!string.IsNullOrWhiteSpace(run.FinalText))
        {
            Console.WriteLine();
            Console.WriteLine(run.FinalText);
        }
    }
}
