using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Reviews;
using Microsoft.AspNetCore.Http;

namespace AirlineDemo.Api.Requests;

public sealed record MockResponseCommand(
    string EventId, string ReportId, string RequestId, string BasisId,
    int BasisVersion, string ScenarioTime, string Body);
public sealed record MockResponseReceipt(
    string EventId, string CaseId, string RequestId, string ReportId,
    string RequestBasisId, int RequestBasisVersion, string BasisId,
    int BasisVersion, string BasisDigest, string DocumentId);
public enum MockResponseOutcome { Accepted, Replayed, Conflict, NotFound, Stale, Invalid, ScenarioTimeMismatch }
public sealed record MockResponseResult(MockResponseOutcome Outcome, MockResponseReceipt? Receipt);

/// <summary>Accepts synthetic responses into the internal inbox and appends their basis to scoped case history.</summary>
public sealed class MockResponseService(
    ICaseStateStore cases,
    MockRequestInbox requests,
    AssessmentStore assessments,
    TimeProvider time)
{
    public const string AcceptedAction = "mock-response.accepted";
    private const int MaxBodyLength = 1_000_000;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public MockResponseResult Accept(CaseScope scope, string actor, MockResponseCommand command)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(command);
        if (!IsValid(command)) return new(MockResponseOutcome.Invalid, null);
        lock (Gate)
        {
            var state = cases.Read(scope);
            var current = assessments.CurrentBasis(scope.CaseId);
            if (current is null) return NotFound();
            var fingerprint = Fingerprint(scope.CaseId, command);
            var priorEvent = FindEvent(state, command.EventId);
            if (priorEvent is not null)
            {
                if (priorEvent.Fingerprint != fingerprint) return Conflict();
                CompletePostCommit(scope, command, priorEvent.Receipt);
                return new(MockResponseOutcome.Replayed, priorEvent.Receipt);
            }

            if (!string.Equals(command.ScenarioTime, current.ScenarioTime, StringComparison.Ordinal))
                return new(MockResponseOutcome.ScenarioTimeMismatch, null);
            if (state.ReceiptFingerprints.ContainsKey(command.EventId)
                || FindReport(state, command.ReportId) is not null
                || FindRequestResponse(state, command.RequestId) is not null)
                return Conflict();
            var request = requests.Requests.FirstOrDefault(r =>
                r.CaseId == scope.CaseId && r.RequestId == command.RequestId);
            if (request is null) return NotFound();

            if (request.BasisId != command.BasisId || request.BasisVersion != command.BasisVersion
                || current.BasisId != command.BasisId || current.BasisVersion != command.BasisVersion
                || state.Basis.Count != current.BasisVersion
                || !current.Coverage.IsComplete)
                return new(MockResponseOutcome.Stale, null);
            var nextVersion = checked(current.BasisVersion + 1);
            var documentId = "response-" + Digest(scope.CaseId, command.EventId)[..16].ToLowerInvariant();
            var storedBasis = CaseStateStore.CreateBasisVersion(nextVersion, command.Body,
                state.Basis.Count == 0 ? string.Empty : state.Basis[^1].BasisDigest);
            var receipt = new MockResponseReceipt(command.EventId, scope.CaseId, request.RequestId, command.ReportId,
                command.BasisId, command.BasisVersion, current.BasisId, nextVersion, storedBasis.BasisDigest, documentId);
            var nextBasis = AddResponseDocument(current, receipt, command.Body);
            var plan = PlanReassessment(scope, nextBasis, command, state);
            var detail = new ResponseAuditDetail(fingerprint, receipt, plan.Outcome, plan.Reason);
            try
            {
                if (!assessments.RunIfCurrent(scope.CaseId, current, () =>
                {
                    cases.Commit(scope, state.Revision, new CaseCommit(storedBasis, command.EventId, actor, AcceptedAction,
                        JsonSerializer.Serialize(detail, JsonOptions), time.GetUtcNow()));
                    CompletePostCommit(scope, nextBasis, plan);
                }))
                    return new(MockResponseOutcome.Stale, null);
            }
            catch (RevisionConflictException)
            {
                var latest = cases.Read(scope);
                var racedEvent = FindEvent(latest, command.EventId);
                if (racedEvent is not null)
                {
                    if (racedEvent.Fingerprint != fingerprint) return Conflict();
                    CompletePostCommit(scope, command, racedEvent.Receipt);
                    return new(MockResponseOutcome.Replayed, racedEvent.Receipt);
                }

                return Conflict();
            }
            catch (ReceiptConflictException)
            {
                var latest = cases.Read(scope);
                var racedEvent = FindEvent(latest, command.EventId);
                if (racedEvent is null || racedEvent.Fingerprint != fingerprint) return Conflict();
                CompletePostCommit(scope, command, racedEvent.Receipt);
                return new(MockResponseOutcome.Replayed, racedEvent.Receipt);
            }
            return new(MockResponseOutcome.Accepted, receipt);
        }
    }

    private void CompletePostCommit(
        CaseScope scope,
        MockResponseCommand command,
        MockResponseReceipt receipt)
    {
        var state = cases.Read(scope);
        var current = assessments.CurrentBasis(scope.CaseId)
            ?? throw new InvalidDataException("Accepted mock response has no assessment basis.");
        var nextBasis = ReconstructResponseBasis(current, state, receipt, command.Body);
        var plannedRecord = PlanReassessment(scope, nextBasis, command, state);
        CompletePostCommit(scope, nextBasis, plannedRecord);
    }

    private void CompletePostCommit(
        CaseScope scope,
        AssessmentBasis nextBasis,
        AssessmentRecord plannedRecord)
    {
        var current = assessments.CurrentBasis(scope.CaseId);
        if (current is null || current.BasisVersion <= nextBasis.BasisVersion)
            assessments.SetCurrentBasis(scope.CaseId, nextBasis);
        else if (current.BasisId != nextBasis.BasisId)
            throw new InvalidDataException("Accepted mock response does not match the current assessment history.");
        EnsureWorkItem(scope, plannedRecord);
        EnsureAssessmentRecord(plannedRecord);
    }

    private AssessmentRecord PlanReassessment(
        CaseScope scope,
        AssessmentBasis basis,
        MockResponseCommand command,
        CaseState state)
    {
        var responseDocuments = ReadResponseAudit(state).Select(item => item.Receipt.DocumentId)
            .Append("response-" + Digest(scope.CaseId, command.EventId)[..16].ToLowerInvariant());
        var witnesses = FindIndependentWitnesses(basis, command.Body, responseDocuments);
        IReadOnlyList<AssessmentClaim> claims = witnesses.Count == 0
            ? []
            : [new AssessmentClaim(ClaimKind.Baseline, command.Body, "mock-response", command.ReportId, witnesses)];
        var validated = claims.Select(claim => ClaimValidator.Validate(
            new InvestigationClaim(claim.Value, claim.Witnesses), claim.Kind, basis.Documents, claim.Value)).ToArray();
        var assessedClaims = claims.Select(claim => new AssessedClaim(
            claim.Kind.ToString(),
            claim.Value,
            claim.Component,
            claim.SerialNumber,
            claim.Witnesses.Select(witness => new AssessedWitness(witness.DocumentId, witness.Page)).ToArray())).ToArray();
        var (outcome, reason) = !basis.Coverage.IsComplete
            ? (AssessmentExecutionService.Review, "processing or inventory coverage incomplete")
            : basis.IdentityAmbiguous
                ? (AssessmentExecutionService.Review, "ambiguous identity")
                : validated.Any(claim => claim.Kind == ClaimKind.Conflict)
                    ? (AssessmentExecutionService.Review, "conflicting evidence")
                    : validated.FirstOrDefault(claim => claim.Status == ClaimStatus.Downgraded) is { } downgraded
                        ? (AssessmentExecutionService.Review, $"downgraded claim: {downgraded.Reason}")
                        : validated.Length == 0
                            ? (AssessmentExecutionService.Review, "no claim")
                            : (AssessmentExecutionService.Baseline, "supported baseline");
        var workItemId = outcome == AssessmentExecutionService.Review
            ? "mock-response-review-response-" + Digest(scope.CaseId, command.EventId)[..16].ToLowerInvariant()
            : null;
        return new AssessmentRecord(
            scope.CaseId, basis.BasisId, basis.BasisVersion, outcome, reason, assessedClaims, null, workItemId);
    }

    private void EnsureWorkItem(CaseScope scope, AssessmentRecord record)
    {
        if (record.WorkItemId is null) return;
        var existing = cases.ListWorkItems(scope).FirstOrDefault(item => item.ItemId == record.WorkItemId);
        if (existing is not null)
        {
            if (existing.CapabilityId != AssessmentExecutionService.ReviewCapability
                || existing.State != "open" || existing.Owner != AssessmentExecutionService.ReviewOwner)
                throw new InvalidDataException("Mock response review work item does not match its receipt.");
            return;
        }

        cases.CreateWorkItem(scope, cases.Read(scope).Revision,
            new WorkItemDraft(record.WorkItemId, AssessmentExecutionService.ReviewCapability,
                "open", AssessmentExecutionService.ReviewOwner), AssessmentExecutionService.Actor);
    }

    private void EnsureAssessmentRecord(AssessmentRecord record)
    {
        var existing = assessments.List(record.CaseId).FirstOrDefault(item =>
            (record.WorkItemId is not null && item.WorkItemId == record.WorkItemId)
            || (item.BasisId == record.BasisId && item.BasisVersion == record.BasisVersion
                && AssessmentRecordsEqual(item, record)));
        if (existing is null)
        {
            assessments.Append(record);
            return;
        }

        if (!AssessmentRecordsEqual(existing, record))
            throw new InvalidDataException("Mock response assessment does not match its receipt.");
    }

    private static bool AssessmentRecordsEqual(AssessmentRecord left, AssessmentRecord right) =>
        left.CaseId == right.CaseId && left.BasisId == right.BasisId && left.BasisVersion == right.BasisVersion
        && left.Outcome == right.Outcome && left.Reason == right.Reason && left.RequestId == right.RequestId
        && left.WorkItemId == right.WorkItemId && left.Claims.Count == right.Claims.Count
        && left.Claims.Zip(right.Claims).All(pair =>
            pair.First.Kind == pair.Second.Kind && pair.First.Value == pair.Second.Value
            && pair.First.Component == pair.Second.Component && pair.First.SerialNumber == pair.Second.SerialNumber
            && pair.First.Witnesses.SequenceEqual(pair.Second.Witnesses));

    private static AssessmentBasis AddResponseDocument(
        AssessmentBasis current,
        MockResponseReceipt receipt,
        string body)
    {
        if (current.BasisId != receipt.RequestBasisId || current.BasisVersion != receipt.RequestBasisVersion)
            throw new InvalidDataException("Accepted mock response was based on a different assessment basis.");
        var documents = current.Documents.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        documents.Add(receipt.DocumentId, BasisDocument.Create(receipt.DocumentId, [body]));
        return current with
        {
            BasisVersion = receipt.BasisVersion,
            Documents = documents,
            Coverage = current.Coverage with
            {
                Inventory = checked(current.Coverage.Inventory + 1),
                Processed = checked(current.Coverage.Processed + 1),
            },
        };
    }

    private static AssessmentBasis ReconstructResponseBasis(
        AssessmentBasis current,
        CaseState state,
        MockResponseReceipt receipt,
        string body)
    {
        if (current.BasisId == receipt.RequestBasisId
            && current.BasisVersion == receipt.RequestBasisVersion)
            return AddResponseDocument(current, receipt, body);
        if (current.BasisId != receipt.BasisId || current.BasisVersion < receipt.BasisVersion)
            throw new InvalidDataException("Accepted mock response cannot be reconciled with the current assessment basis.");
        if (!current.Documents.TryGetValue(receipt.DocumentId, out var response)
            || response.Pages.Count != 1 || response.Pages[0] != body)
            throw new InvalidDataException("Accepted mock response basis does not match its receipt.");
        if (current.BasisVersion == receipt.BasisVersion) return current;

        var documents = current.Documents.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var laterDocuments = ReadResponseAudit(state)
            .Where(item => item.Receipt.BasisId == receipt.BasisId
                && item.Receipt.BasisVersion > receipt.BasisVersion
                && item.Receipt.BasisVersion <= current.BasisVersion)
            .Select(item => item.Receipt.DocumentId);
        var removed = 0;
        foreach (var documentId in laterDocuments)
        {
            if (documents.Remove(documentId)) removed++;
        }

        if (removed == 0 || current.Coverage.Inventory < removed || current.Coverage.Processed < removed)
            throw new InvalidDataException("Accepted mock response history is incomplete.");
        return current with
        {
            BasisVersion = receipt.BasisVersion,
            Documents = documents,
            Coverage = current.Coverage with
            {
                Inventory = current.Coverage.Inventory - removed,
                Processed = current.Coverage.Processed - removed,
            },
        };
    }

    private static bool IsValid(MockResponseCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.EventId) || string.IsNullOrWhiteSpace(command.ReportId)
            || string.IsNullOrWhiteSpace(command.RequestId) || string.IsNullOrWhiteSpace(command.BasisId)
            || command.BasisVersion < 1 || string.IsNullOrWhiteSpace(command.ScenarioTime)
            || string.IsNullOrWhiteSpace(command.Body))
            return false;
        return command.Body.Length <= MaxBodyLength;
    }

    private static ResponseAuditDetail? FindEvent(CaseState state, string eventId) =>
        ReadResponseAudit(state).FirstOrDefault(item => item.Receipt.EventId == eventId);
    private static ResponseAuditDetail? FindReport(CaseState state, string reportId) =>
        ReadResponseAudit(state).FirstOrDefault(item => item.Receipt.ReportId == reportId);
    private static ResponseAuditDetail? FindRequestResponse(CaseState state, string requestId) =>
        ReadResponseAudit(state).FirstOrDefault(item => item.Receipt.RequestId == requestId);

    private static IEnumerable<ResponseAuditDetail> ReadResponseAudit(CaseState state) =>
        state.Audit.Where(a => a.Action == AcceptedAction)
            .Select(a => JsonSerializer.Deserialize<ResponseAuditDetail>(a.Detail, JsonOptions)
                ?? throw new InvalidDataException("Mock response audit detail is empty."));

    private static IReadOnlyList<DocumentWitness> FindIndependentWitnesses(
        AssessmentBasis basis, string value, IEnumerable<string> responseDocumentIds)
    {
        var responseDocuments = responseDocumentIds.ToHashSet(StringComparer.Ordinal);
        foreach (var document in basis.Documents.Values)
        {
            if (responseDocuments.Contains(document.DocumentId) || document.Pages is null) continue;
            for (var page = 0; page < document.Pages.Count; page++)
            {
                if (document.Pages[page].Contains(value, StringComparison.Ordinal))
                    return [new DocumentWitness(document.DocumentId, page + 1, value)];
            }
        }

        return [];
    }

    private static string Fingerprint(string caseId, MockResponseCommand command) =>
        Digest(caseId, command.EventId, command.ReportId, command.RequestId, command.BasisId,
            command.BasisVersion.ToString(CultureInfo.InvariantCulture), command.ScenarioTime, command.Body);
    private static string Digest(params string[] fields)
    {
        using var stream = new MemoryStream();
        foreach (var field in fields)
        {
            stream.Write(Encoding.ASCII.GetBytes(field.Length.ToString(CultureInfo.InvariantCulture) + ":"));
            stream.Write(MemoryMarshal.AsBytes(field.AsSpan()));
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
    private static MockResponseResult NotFound() => new(MockResponseOutcome.NotFound, null);
    private static MockResponseResult Conflict() => new(MockResponseOutcome.Conflict, null);
    private sealed record ResponseAuditDetail(
        string Fingerprint,
        MockResponseReceipt Receipt,
        string ReassessmentOutcome,
        string ReassessmentReason);
}

public static class MockResponseEndpoints
{
    public static IEndpointRouteBuilder MapMockResponseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/cases/{caseId}/mock-responses", AcceptResponse);
        return app;
    }

    private static IResult AcceptResponse(
        string caseId,
        MockResponseCommand body,
        HttpContext http,
        DemoSessionStore sessions,
        ICaseStateStore cases,
        MockResponseService responses,
        IConfiguration config,
        ILogger<DemoSessionStore> logger)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!DemoAuthEndpoints.HasValidAntiForgery(http, session)) return DemoAuthEndpoints.AntiForgeryFailed();
        if (session.User.Role != EvidenceEndpoints.PresenterRole)
            return Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden);

        var scope = TechnicalReviewPolicy.ScopeFor(cases, session.User.CaseScopes, caseId);
        if (scope is null)
        {
            TelemetryRedaction.EmitSecurityEvent(logger, TelemetryRedaction.SecurityEvents.AuthScopeFailure,
                TelemetryRedaction.ServiceVersion, config["Telemetry:Environment"] ?? "demo");
            return Results.NotFound();
        }

        var result = responses.Accept(scope, session.User.Id, body);
        return result.Outcome switch
        {
            MockResponseOutcome.Accepted => Results.Json(result.Receipt, statusCode: StatusCodes.Status201Created),
            MockResponseOutcome.Replayed => Results.Ok(result.Receipt),
            MockResponseOutcome.NotFound => Results.NotFound(),
            MockResponseOutcome.Invalid or MockResponseOutcome.ScenarioTimeMismatch =>
                Results.BadRequest(new { error = "Invalid mock response." }),
            MockResponseOutcome.Stale or MockResponseOutcome.Conflict =>
                Results.Json(new { error = "Mock response conflicts with current case state." }, statusCode: StatusCodes.Status409Conflict),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
