namespace ResearchScout.Harness;

/// <summary>A tool call waiting for a human yes/no.</summary>
public sealed record ApprovalRequest(Guid RunId, int StepNumber, string CallId, string ToolName, string InputJson);

public sealed record ApprovalDecision(bool Approved, string? Reason = null)
{
    public static readonly ApprovalDecision Approve = new(true);

    public static ApprovalDecision Reject(string? reason = null) => new(false, reason);
}

/// <summary>
/// Asks a human before a tool with <c>NeedsApproval = true</c> runs. The loop waits for the answer.
/// Implementations: console prompt (CLI), web (SSE event + approve/reject endpoints).
/// </summary>
public interface IApprovalGate
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Rejects everything. The safe default when no human is available.</summary>
public sealed class DenyAllApprovalGate : IApprovalGate
{
    public static readonly DenyAllApprovalGate Instance = new();

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalDecision.Reject("No one is available to approve this tool call."));
}

/// <summary>Approves everything. For tests and trusted local scripts only.</summary>
public sealed class AutoApproveGate : IApprovalGate
{
    public static readonly AutoApproveGate Instance = new();

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalDecision.Approve);
}
