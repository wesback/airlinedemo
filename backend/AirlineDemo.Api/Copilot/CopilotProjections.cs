using System.Text.Json;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Dashboard;
using AirlineDemo.Api.Decisions;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Obligations;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Projection;
using AirlineDemo.Api.Reviews;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Copilot;

public sealed record CopilotCaseSummary(
    int ContractVersion,
    string CaseId,
    string AircraftId,
    long CaseRevision,
    CasePackCaseStates Status,
    string? OwnerRole,
    string? NextActionId,
    string CapabilityLabel,
    bool Released,
    IReadOnlyList<string> ReleaseBlockers,
    bool Simulated,
    bool Synthetic);

public sealed record CopilotSourceRef(string SourceId, string RecordId, int RecordVersion);

public sealed record CopilotOpenPoint(
    string PointId, string Domain, string PointType, string State, string OwnerRole, IReadOnlyList<CopilotSourceRef> SourceRefs);

public sealed record CopilotOpenPoints(int ContractVersion, string CaseId, long CaseRevision, string Domain, IReadOnlyList<CopilotOpenPoint> Points);

public sealed record CopilotStatusEnvelope(int ContractVersion, string CaseId, long CaseRevision, CasePackCaseStates Status);

public enum CopilotProjectionOutcome { Ok, Unauthenticated, Forbidden, NotFound, ContextInvalid, PointLimit }

public sealed record CopilotProjectionResult(CopilotProjectionOutcome Outcome, object? Value = null);

/// <summary>
/// API-owned, closed-field copilot projections. Every value is taken from an existing canonical projection or policy
/// (the case-pack list, release gates, technical/finance/legal decisions, source summary pins) and checked against
/// its registered token set; an unknown value fails the projection instead of being forwarded.
/// </summary>
public sealed class CopilotProjections(
    ICaseStateStore cases,
    IAircraftCasePackSnapshotStore snapshots,
    FinanceInputStore financeInputs,
    SourceModeStore sourceModes,
    TimeProvider time)
{
    public const int ContractVersion = 1;
    public const int MaxPoints = 100;
    public const int MaxSourceRefs = 100;
    public static readonly IReadOnlyList<string> Roles = ["technical-reviewer", "finance-reviewer", "legal-reviewer", "presenter", "executive"];
    private static readonly string[] BlockerCodes = ["technical_review_not_accepted", "reserve_not_approved", "obligations_not_signed_off"];
    private static readonly Regex Identity = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public CopilotProjectionResult Summary(DemoSession? session, string caseId)
    {
        var access = Authorize(session, caseId);
        if (access.Denied is not null) return access.Denied;
        var summary = BuildSummary(caseId, access.Role!, cases.Read(access.Scope!));
        return summary is null ? new(CopilotProjectionOutcome.ContextInvalid) : new(CopilotProjectionOutcome.Ok, summary);
    }

    public CopilotProjectionResult OpenPoints(DemoSession? session, string caseId)
    {
        var access = Authorize(session, caseId);
        if (access.Denied is not null) return access.Denied;
        var role = access.Role!;
        var state = cases.Read(access.Scope!);
        var summary = BuildSummary(caseId, role, state);
        if (summary is null) return new(CopilotProjectionOutcome.ContextInvalid);
        var domain = role switch
        {
            "technical-reviewer" => "technical",
            "finance-reviewer" => "finance",
            "legal-reviewer" => "legal",
            _ => null,
        };
        if (domain is null)
            return new(CopilotProjectionOutcome.Ok, new CopilotStatusEnvelope(ContractVersion, caseId, summary.CaseRevision, summary.Status));

        var refs = SourceSummaryService.ReadPins(state)
            .Where(pin => SourceSummaryService.CanSeeDetails(role, pin.SourceId))
            .Select(pin => new CopilotSourceRef(pin.SourceId, pin.RecordId, pin.RecordVersion))
            .Distinct()
            .OrderBy(item => item.SourceId, StringComparer.Ordinal).ThenBy(item => item.RecordId, StringComparer.Ordinal)
            .ThenBy(item => item.RecordVersion).ToArray();
        if (refs.Length > MaxSourceRefs || refs.Any(item => !Identity.IsMatch(item.RecordId)))
            return new(CopilotProjectionOutcome.ContextInvalid);

        var modes = sourceModes.ModesFor(state);
        var inputs = financeInputs.Find(caseId);
        var open = domain switch
        {
            "technical" => TechnicalPoints(state, inputs, modes),
            "finance" => FinancePoints(caseId, state, modes),
            _ => LegalPoints(state, inputs, modes),
        };
        if (open.Any(point => !Identity.IsMatch(point.Id))) return new(CopilotProjectionOutcome.ContextInvalid);
        if (open.Count > MaxPoints) return new(CopilotProjectionOutcome.PointLimit);

        var pointType = domain switch { "technical" => "technical_requirement", "finance" => "reserve_review", _ => "obligation_review" };
        var points = open.OrderBy(point => point.Id, StringComparer.Ordinal)
            .Select(point => new CopilotOpenPoint(point.Id, domain, pointType, point.Blocked ? "blocked" : "open", role, refs))
            .ToArray();
        return new(CopilotProjectionOutcome.Ok, new CopilotOpenPoints(ContractVersion, caseId, summary.CaseRevision, domain, points));
    }

    // Role and scope are decided from session metadata and case ownership only, so a denial never reads case state.
    internal (CopilotProjectionResult? Denied, string? Role, CaseScope? Scope) Authorize(DemoSession? session, string caseId)
    {
        if (session is null) return (new(CopilotProjectionOutcome.Unauthenticated), null, null);
        var role = session.User.Role;
        if (!Roles.Contains(role, StringComparer.Ordinal)) return (new(CopilotProjectionOutcome.Forbidden), null, null);
        if (CasePackEndpoints.Metadata(caseId) is null) return (new(CopilotProjectionOutcome.NotFound), null, null);
        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        return scope is null ? (new(CopilotProjectionOutcome.NotFound), null, null) : (null, role, scope);
    }

    private CopilotCaseSummary? BuildSummary(string caseId, string role, CaseState state)
    {
        var snapshot = snapshots.Read();
        if (snapshot is null || CasePackEndpoints.Metadata(caseId) is not { } metadata
            || !CasePackSceneProjection.TrySerial(snapshot, caseId, "aircraft", null, out var aircraftId) || !Identity.IsMatch(aircraftId))
            return null;

        CasePackCaseSummary listed;
        try { listed = CasePackEndpoints.Project(caseId, metadata.Name, metadata.Category, state, role, time.GetUtcNow()); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException) { return null; }

        var nextActionId = listed.NextAction?.ActionId;
        var blockers = ReleaseEndpoints.Blockers(state);
        var valid = listed.CaseRevision is >= 0 and < long.MaxValue
            && StatusValid(listed.Status)
            && (listed.Owner is null || Roles.Contains(listed.Owner, StringComparer.Ordinal))
            && (nextActionId is null || (state.WorkItems ?? []).Any(item => item.ItemId == nextActionId
                && item.CaseId == caseId && item.Owner == role && item.State == "open"))
            && (listed.CapabilityLabel == CapabilityLabels.Unknown || CapabilityLabels.Known.Contains(listed.CapabilityLabel, StringComparer.Ordinal))
            && blockers.All(code => BlockerCodes.Contains(code, StringComparer.Ordinal));
        return valid
            ? new CopilotCaseSummary(ContractVersion, caseId, aircraftId, listed.CaseRevision, listed.Status, listed.Owner, nextActionId,
                listed.CapabilityLabel, ReleaseEndpoints.IsReleased(state), blockers, Simulated: true, Synthetic: true)
            : null;
    }

    // Finance also accepts "return": it is the token the existing reserve decision API records for a rejection.
    private static bool StatusValid(CasePackCaseStates status) =>
        (status.Assessment is null or "Baseline" or "Request" or "Review")
        && (status.Review is null or "open" or "closed")
        && (status.Request is null or "delivered")
        && (status.Finance is null or "approve" or "reject" or "return")
        && (status.Legal is null or "signed-off" or "rejected")
        && (status.Acceptance is null or "accept" or "reject");

    // Technical requirements stay open until the latest decision on the current basis accepts them.
    private static List<(string Id, bool Blocked)> TechnicalPoints(CaseState state, FinanceInputs? inputs, IReadOnlyDictionary<string, string> modes)
    {
        if (TechnicalReviewPolicy.AuthorizesRequest(state)) return [];
        var unavailable = state.Basis.Count == 0 || modes["technical"] != SourceModes.Available;
        return (inputs?.Obligation?.Requirements ?? [])
            .GroupBy(requirement => requirement.Id, StringComparer.Ordinal)
            .Select(group => (group.Key, unavailable || group.Any(item => item.Evidence == RequirementEvidence.Ambiguous)))
            .ToList();
    }

    // A reserve decision recorded before the current basis was created is stale and does not close the point.
    private static List<(string Id, bool Blocked)> FinancePoints(string caseId, CaseState state, IReadOnlyDictionary<string, string> modes)
    {
        var decision = state.Audit.LastOrDefault(item => item.Action == FinanceEndpoints.ReserveDecisionAction);
        var current = decision is not null && (state.Basis.Count == 0 || decision.At >= state.Basis[^1].CreatedAt);
        if (current && Field(decision!.Detail, "decision") == "approve") return [];
        return [($"RSV-{caseId}", state.Basis.Count == 0 || modes["finance"] != SourceModes.Available)];
    }

    // A legal sign-off closes the obligation only when it was given on the current basis version.
    private static List<(string Id, bool Blocked)> LegalPoints(CaseState state, FinanceInputs? inputs, IReadOnlyDictionary<string, string> modes)
    {
        if (inputs?.Obligation is not { } obligation) return [];
        var decision = state.Audit.LastOrDefault(item => item.Action == FinanceEndpoints.LegalDecisionAction);
        if (decision is not null && Field(decision.Detail, "decision") == "signed-off"
            && Field(decision.Detail, "basisVersion") == state.Basis.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return [];
        return [(obligation.Id, state.Basis.Count == 0 || modes["contracts"] != SourceModes.Available)];
    }

    private static string? Field(string detail, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(detail);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in json.RootElement.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? property.Value.ToString() : null;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
