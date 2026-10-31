using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Decisions;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Copilot;

/// <summary>The stored release receipt: its exact bytes and the fingerprint of the command that produced it.</summary>
public sealed record CopilotReleaseReceipt(string Fingerprint, string Bytes);

/// <summary>
/// Everything one atomic copilot release needs inside the store's commit boundary: the requested command, the
/// revalidation of the live session, the clock, the prepared ledger outcome row and the stores that hold the proposal
/// lifecycle and the ledger.
/// </summary>
public sealed record CopilotReleaseCommit(
    Guid ProposalId, string CallerRef, string SessionRef, string Fingerprint, long ExpectedRevision, string Actor, TimeProvider Time,
    Func<CopilotApiResult?> Revalidate, CopilotInvocationRecord Outcome, ICopilotProposalStore Proposals, ICopilotInvocationStore Ledger);

public static class CopilotReleasePolicy
{
    /// <summary>SHA-256 over the exact command tuple; any difference in proposal, caller, session, case, revision or note differs.</summary>
    public static string Fingerprint(Guid proposalId, string callerRef, string sessionRef, string caseId, string action, long expectedRevision, string note)
    {
        var canonical = JsonSerializer.Serialize(new object[] { proposalId.ToString("D"), callerRef, sessionRef, caseId, action, expectedRevision, note });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string Fingerprint(CopilotProposal proposal) => Fingerprint(proposal.ProposalId, proposal.CallerRef, proposal.SessionRef,
        proposal.CaseId, proposal.Action, proposal.ExpectedRevision, proposal.Note);

    /// <summary>The business audit detail, the same shape the existing release endpoint writes.</summary>
    public static string AuditDetail(string note) => JsonSerializer.Serialize(new { decision = "released", note });

    public static string Receipt(CopilotProposal proposal, long revision, int auditSequence) => JsonSerializer.Serialize(new
    {
        contractVersion = CopilotProjections.ContractVersion, proposalId = proposal.ProposalId.ToString("D"), caseId = proposal.CaseId,
        decision = "released", revision, auditSequence, simulated = true,
    });

    /// <summary>The fresh release; its outcome row was appended inside the store's transaction.</summary>
    public static CopilotApiResult Released(string receipt) => new(200, "ok", new JsonObject(), receipt, Recorded: true);

    /// <summary>
    /// Decides the release against the state read inside the commit boundary. Null means every check passed and the
    /// store may commit; otherwise the rejection or the exact replay of the original receipt.
    /// </summary>
    public static CopilotApiResult? Evaluate(CopilotReleaseCommit commit, CopilotProposalEntry? entry, CaseState state)
    {
        if (commit.Revalidate() is { } denied) return denied;
        if (entry is null || entry.Proposal.CallerRef != commit.CallerRef || entry.Proposal.SessionRef != commit.SessionRef)
            return CopilotApiResult.Error(404, "confirmation_not_found");
        if (entry.State == CopilotProposalStates.Consumed)
            return entry.Receipt is { } receipt && receipt.Fingerprint == commit.Fingerprint
                ? new(200, "ok", new JsonObject(), receipt.Bytes)
                : CopilotApiResult.Error(409, "replay_mismatch");
        if (Fingerprint(entry.Proposal) != commit.Fingerprint) return CopilotApiResult.Error(409, "confirmation_mismatch");
        switch (entry.EffectiveState(commit.Time.GetUtcNow()))
        {
            case CopilotProposalStates.Pending: return CopilotApiResult.Error(409, "confirmation_required");
            case CopilotProposalStates.Declined: return CopilotApiResult.Error(409, "confirmation_declined");
            case CopilotProposalStates.Cancelled: return CopilotApiResult.Error(409, "confirmation_cancelled");
            case CopilotProposalStates.Expired: return CopilotApiResult.Error(410, "confirmation_expired");
        }
        if (state.Revision != commit.ExpectedRevision) return CopilotApiResult.Error(409, "stale_revision");
        if (ReleaseEndpoints.IsReleased(state)) return CopilotApiResult.Error(409, "already_released");
        var blockers = ReleaseEndpoints.Blockers(state);
        return blockers.Count > 0 ? CopilotApiResult.Error(409, "release_blocked", blockers) : null;
    }
}

public sealed class CopilotReleaseReceiptRow
{
    public Guid ProposalId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string Receipt { get; set; } = string.Empty;
}
