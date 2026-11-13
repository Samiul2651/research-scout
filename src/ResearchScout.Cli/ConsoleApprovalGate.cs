using ResearchScout.Harness;

namespace ResearchScout.Cli;

/// <summary>Asks "Approve? [y/N]" in the terminal before tools that need approval.</summary>
internal sealed class ConsoleApprovalGate : IApprovalGate
{
    // Only one prompt at a time, even if runs overlap.
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Console.WriteLine();
            Console.WriteLine($"  The agent wants to run '{request.ToolName}' with:");
            Console.WriteLine($"  {request.InputJson}");
            Console.Write("  Approve? [y/N] (or type a reason to reject): ");

            var answer = (await Task.Run(Console.ReadLine, cancellationToken).ConfigureAwait(false))?.Trim() ?? "";
            return answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase)
                ? ApprovalDecision.Approve
                : ApprovalDecision.Reject(answer.Length > 1 ? answer : null);
        }
        finally
        {
            Lock.Release();
        }
    }
}
