using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Decisions;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Copilot;

/// <summary>
/// One copilot API outcome: the status, the closed result code and the body (or the exact stored bytes). <c>Recorded</c>
/// is set when the store already appended the ledger outcome in the same transaction as the business change.
/// </summary>
public sealed record CopilotApiResult(int Status, string Code, JsonObject Body, string? Raw = null, bool Recorded = false)
{
    public static CopilotApiResult Error(int status, string code, IReadOnlyList<string>? blockers = null)
    {
        var error = new JsonObject { ["code"] = code };
        if (blockers is not null) error["blockers"] = new JsonArray([.. blockers.Select(item => (JsonNode)item)]);
        return new(status, code, new JsonObject { ["contractVersion"] = CopilotProjections.ContractVersion, ["error"] = error });
    }

    public CopilotOutcome Outcome => Status < 300 ? CopilotOutcome.Succeeded
        : Status is 401 or 403 || Code is "case_not_found" or "confirmation_not_found" ? CopilotOutcome.Denied : CopilotOutcome.Failed;

    public IResult ToHttp() => Raw is not null
        ? Results.Text(Raw, "application/json", Encoding.UTF8, Status)
        : Results.Json(Body, statusCode: Status);

    public CopilotInvocationResult ToInvocation() => new(ToHttp(), Status, Code, Outcome, Recorded);
}

/// <summary>
/// Creates final-release proposals and records trusted presenter decisions on them (FR-008, FR-009). A proposal is only
/// visible to the presenter user and exact demo session that created it, for its own case.
/// </summary>
public sealed class CopilotProposalService(
    ICopilotProposalStore proposals, ICopilotInvocationLedger ledger, CopilotProjections projections, ICaseStateStore cases, TimeProvider time,
    DemoSessionStore sessions, ICopilotInvocationStore invocations)
{
    public const string Warning = "Simulated release: no lessee or external system is contacted.";
    private static readonly Dictionary<string, string> Decisions = new(StringComparer.Ordinal)
    {
        ["approve"] = CopilotProposalStates.Approved, ["decline"] = CopilotProposalStates.Declined, ["cancel"] = CopilotProposalStates.Cancelled,
    };

    public static bool IsDecision(string? decision) => decision is not null && Decisions.ContainsKey(decision);

    /// <summary>Creates one pending proposal from a trusted raw note; nothing is released.</summary>
    public CopilotApiResult Create(DemoSession? session, string caseId, long expectedRevision, string? rawNote)
    {
        if (Presenter(session, caseId, out var scope) is { } denied) return denied;
        if (rawNote is null) return CopilotApiResult.Error(400, "release_note_required");
        if (NormalizeNote(rawNote) is not { } note) return CopilotApiResult.Error(400, "invalid_release_note");
        if (cases.Read(scope!).Revision != expectedRevision) return CopilotApiResult.Error(409, "stale_revision");
        var now = time.GetUtcNow();
        var proposal = new CopilotProposal(Guid.NewGuid(), ledger.CallerRef(session!.User.Id), ledger.SessionRef(session.Id), caseId,
            CopilotProposal.FinalRelease, expectedRevision, note, now, now + CopilotProposal.Lifetime);
        proposals.Add(proposal);
        var projected = Project(session, new CopilotProposalEntry(proposal, CopilotProposalStates.Pending));
        return projected.Status == 200 ? projected with { Code = "confirmation_required" } : projected;
    }

    /// <summary>Reads the exact stored tuple for the confirmation UI; never changes state.</summary>
    public CopilotApiResult Read(DemoSession? session, string caseId, string proposalId)
    {
        if (Presenter(session, caseId, out _) is { } denied) return denied;
        return Bound(session!, caseId, proposalId) is { } entry ? Project(session!, entry) : CopilotApiResult.Error(404, "confirmation_not_found");
    }

    public CopilotApiResult Decide(DemoSession? session, string caseId, string proposalId, string decision)
    {
        if (Presenter(session, caseId, out _) is { } denied) return denied;
        if (Bound(session!, caseId, proposalId) is not { } bound) return CopilotApiResult.Error(404, "confirmation_not_found");
        var target = Decisions[decision];
        CopilotApiResult? failure = null;
        var entry = proposals.Update(bound.Proposal.ProposalId, current =>
        {
            var effective = current.EffectiveState(time.GetUtcNow());
            if (effective == CopilotProposalStates.Expired) failure = CopilotApiResult.Error(410, "confirmation_expired");
            else if (effective == target) return null;
            else if (effective == CopilotProposalStates.Pending || effective == CopilotProposalStates.Approved && target != CopilotProposalStates.Approved)
                return target;
            else failure = CopilotApiResult.Error(409, "confirmation_state_conflict");
            return null;
        });
        if (failure is not null) return failure;
        return new(200, "ok", new JsonObject
        {
            ["contractVersion"] = CopilotProjections.ContractVersion, ["proposalId"] = entry!.Proposal.ProposalId.ToString("D"), ["caseId"] = caseId,
            ["state"] = entry.State, ["expiresAt"] = entry.Proposal.ExpiresAt,
        });
    }

    /// <summary>
    /// Releases from an approved proposal, or replays its original receipt, in one atomic store operation (FR-010,
    /// FR-011). The ledger outcome of a fresh release is appended inside that operation.
    /// </summary>
    public CopilotApiResult Release(CopilotAdmission admission, DemoSession? session, string caseId, long expectedRevision, string? rawNote,
        string proposalId)
    {
        if (Presenter(session, caseId, out var scope) is { } denied) return denied;
        if (rawNote is null) return CopilotApiResult.Error(400, "release_note_required");
        if (NormalizeNote(rawNote) is not { } note) return CopilotApiResult.Error(400, "invalid_release_note");
        if (!Guid.TryParseExact(proposalId, "D", out var id)) return CopilotApiResult.Error(404, "confirmation_not_found");
        var (callerRef, sessionRef) = (ledger.CallerRef(session!.User.Id), ledger.SessionRef(session.Id));
        var commit = new CopilotReleaseCommit(id, callerRef, sessionRef,
            CopilotReleasePolicy.Fingerprint(id, callerRef, sessionRef, caseId, CopilotProposal.FinalRelease, expectedRevision, note),
            expectedRevision, session.User.Id, time, () => Revalidate(session, caseId),
            ledger.OutcomeRecord(admission, 200, "ok", CopilotOutcome.Succeeded), proposals, invocations);
        try { return cases.CommitCopilotRelease(scope!, commit); }
        catch (CopilotAuditUnavailableException) { return CopilotApiResult.Error(503, "audit_unavailable"); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CopilotApiResult.Error(503, "release_unavailable");
        }
    }

    // Runs inside the store's commit boundary: the session must still be live, the same user, a presenter and in scope.
    private CopilotApiResult? Revalidate(DemoSession session, string caseId)
    {
        var live = sessions.Find(session.Id);
        if (live is null || live.User.Id != session.User.Id) return CopilotApiResult.Error(401, "unauthenticated");
        if (live.User.Role != ReleaseEndpoints.ReleaseRole) return CopilotApiResult.Error(403, "forbidden");
        return live.User.CaseScopes.Contains(caseId, StringComparer.Ordinal) ? null : CopilotApiResult.Error(404, "case_not_found");
    }

    /// <summary>The trimmed note when the raw note is 1 to 2000 UTF-16 units with a nonempty trim; otherwise null.</summary>
    public static string? NormalizeNote(string rawNote) =>
        rawNote.Length is 0 or > ReleaseEndpoints.MaxNoteLength || rawNote.Trim().Length == 0 ? null : rawNote.Trim();

    // Unknown role 403, missing or out-of-scope case 404, then any role other than the presenter 403.
    internal CopilotApiResult? Presenter(DemoSession? session, string caseId, out CaseScope? scope)
    {
        var access = projections.Authorize(session, caseId);
        scope = access.Scope;
        return access.Denied?.Outcome switch
        {
            CopilotProjectionOutcome.Unauthenticated => CopilotApiResult.Error(401, "unauthenticated"),
            CopilotProjectionOutcome.Forbidden => CopilotApiResult.Error(403, "forbidden"),
            not null => CopilotApiResult.Error(404, "case_not_found"),
            null when access.Role != ReleaseEndpoints.ReleaseRole => CopilotApiResult.Error(403, "forbidden"),
            _ => null,
        };
    }

    // Another user, another session, another case or an unknown id are indistinguishable.
    internal CopilotProposalEntry? Bound(DemoSession session, string caseId, string proposalId) =>
        Guid.TryParseExact(proposalId, "D", out var id) && proposals.Find(id) is { } entry
            && entry.Proposal.CallerRef == ledger.CallerRef(session.User.Id) && entry.Proposal.SessionRef == ledger.SessionRef(session.Id)
            && entry.Proposal.CaseId == caseId
            ? entry : null;

    private CopilotApiResult Project(DemoSession session, CopilotProposalEntry entry)
    {
        var proposal = entry.Proposal;
        var summary = projections.Summary(session, proposal.CaseId);
        if (summary is not { Outcome: CopilotProjectionOutcome.Ok, Value: CopilotCaseSummary value })
            return CopilotApiResult.Error(502, "copilot_context_invalid");
        var root = $"/api/v1/cases/{Uri.EscapeDataString(proposal.CaseId)}/copilot";
        return new(200, "ok", new JsonObject
        {
            ["contractVersion"] = CopilotProjections.ContractVersion, ["proposalId"] = proposal.ProposalId.ToString("D"),
            ["caseId"] = proposal.CaseId, ["aircraftId"] = value.AircraftId, ["action"] = proposal.Action,
            ["expectedRevision"] = proposal.ExpectedRevision, ["note"] = proposal.Note, ["createdAt"] = proposal.CreatedAt,
            ["expiresAt"] = proposal.ExpiresAt, ["state"] = entry.EffectiveState(time.GetUtcNow()), ["simulated"] = true, ["warning"] = Warning,
            ["controls"] = new JsonObject
            {
                ["decision"] = $"{root}/proposals/{proposal.ProposalId:D}/decision", ["advance"] = $"{root}/advance",
            },
        });
    }
}

public static class CopilotProposalEndpoints
{
    public static IEndpointRouteBuilder MapCopilotProposalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/cases/{caseId}/copilot/proposals/{proposalId}",
            (string caseId, string proposalId, HttpContext http, DemoSessionStore sessions, CopilotProposalService service) =>
                service.Read(DemoAuthEndpoints.CurrentSession(http, sessions), caseId, proposalId).ToHttp());
        app.MapPost("/api/v1/cases/{caseId}/copilot/proposals/{proposalId}/decision", Decide);
        return app;
    }

    private static async Task<IResult> Decide(string caseId, string proposalId, HttpContext http, DemoSessionStore sessions,
        CopilotProposalService service, ICopilotInvocationLedger ledger, ICaseStateStore cases)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (CopilotRequestGuard.Check(http, session) is { } denied) return CopilotRequestGuard.Error(denied.Status, denied.Code);
        var body = await ReadObject(http);
        var decision = body is { Count: 1 } && body.TryGetValue("decision", out var value) && value.ValueKind == JsonValueKind.String
            && CopilotProposalService.IsDecision(value.GetString()) ? value.GetString() : null;
        return await CopilotInvocation.RunAsync(ledger, cases, session, decision ?? "decision", CopilotProvenance.TrustedUi, caseId,
            _ => Task.FromResult((decision is null ? CopilotApiResult.Error(400, "invalid_request")
                : service.Decide(session, caseId, proposalId, decision)).ToInvocation()), http.RequestAborted);
    }

    /// <summary>The request's JSON object properties, or null for malformed JSON, a non-object or a duplicate property.</summary>
    internal static async Task<Dictionary<string, JsonElement>?> ReadObject(HttpContext http)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!properties.TryAdd(property.Name, property.Value.Clone())) return null;
            return properties;
        }
        catch (JsonException) { return null; }
    }
}
