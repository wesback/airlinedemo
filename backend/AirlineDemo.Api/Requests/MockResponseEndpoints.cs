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
            if (!string.Equals(command.ScenarioTime, current.ScenarioTime, StringComparison.Ordinal))
                return new(MockResponseOutcome.ScenarioTimeMismatch, null);
            var fingerprint = Fingerprint(scope.CaseId, command);
            var priorEvent = FindEvent(state, command.EventId);
            if (priorEvent is not null)
                return priorEvent.Fingerprint == fingerprint
                    ? new(MockResponseOutcome.Replayed, priorEvent.Receipt)
                    : Conflict();

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
            var documents = current.Documents.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            documents.Add(documentId, BasisDocument.Create(documentId, [command.Body]));
            var nextAssessmentBasis = current with
            {
                BasisVersion = nextVersion,
                Documents = documents,
                Coverage = current.Coverage with
                {
                    Inventory = checked(current.Coverage.Inventory + 1),
                    Processed = checked(current.Coverage.Processed + 1),
                },
            };
            var storedBasis = CaseStateStore.CreateBasisVersion(nextVersion, command.Body,
                state.Basis.Count == 0 ? string.Empty : state.Basis[^1].BasisDigest);
            var receipt = new MockResponseReceipt(command.EventId, scope.CaseId, request.RequestId, command.ReportId,
                command.BasisId, command.BasisVersion, current.BasisId, nextVersion, storedBasis.BasisDigest, documentId);
            var detail = new ResponseAuditDetail(fingerprint, receipt);
            var priorResponseDocuments = ReadResponseAudit(state).Select(item => item.Receipt.DocumentId);
            var responseWitnesses = FindIndependentWitnesses(current, command.Body, priorResponseDocuments);
            IReadOnlyList<AssessmentClaim> responseClaims = responseWitnesses.Count == 0
                ? []
                : [new AssessmentClaim(
                    ClaimKind.Baseline,
                    command.Body,
                    "mock-response",
                    command.ReportId,
                    responseWitnesses)];
            try
            {
                if (!assessments.RunIfCurrent(scope.CaseId, current, () =>
                {
                    var assessmentService = new AssessmentExecutionService(cases, requests, assessments);
                    if (responseClaims.Count == 0)
                    {
                        assessments.SetCurrentBasis(scope.CaseId, nextAssessmentBasis);
                        var reassessed = false;
                        try
                        {
                            assessmentService.Assess(scope, nextAssessmentBasis, responseClaims);
                            reassessed = true;
                        }
                        finally
                        {
                            if (!reassessed)
                                assessments.SetCurrentBasis(scope.CaseId, current);
                        }

                        var revision = cases.Read(scope).Revision;
                        cases.Commit(scope, revision, new CaseCommit(storedBasis, command.EventId, actor, AcceptedAction,
                            JsonSerializer.Serialize(detail, JsonOptions), time.GetUtcNow()));
                    }
                    else
                    {
                        cases.Commit(scope, state.Revision, new CaseCommit(storedBasis, command.EventId, actor, AcceptedAction,
                            JsonSerializer.Serialize(detail, JsonOptions), time.GetUtcNow()));
                        assessments.SetCurrentBasis(scope.CaseId, nextAssessmentBasis);
                        assessmentService.Assess(scope, nextAssessmentBasis, responseClaims);
                    }
                }))
                    return new(MockResponseOutcome.Stale, null);
            }
            catch (RevisionConflictException)
            {
                var latest = cases.Read(scope);
                var racedEvent = FindEvent(latest, command.EventId);
                if (racedEvent is not null)
                {
                    return racedEvent.Fingerprint == fingerprint
                        ? new(MockResponseOutcome.Replayed, racedEvent.Receipt)
                        : Conflict();
                }

                return Conflict();
            }
            catch (ReceiptConflictException)
            {
                return Conflict();
            }
            return new(MockResponseOutcome.Accepted, receipt);
        }
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
    private sealed record ResponseAuditDetail(string Fingerprint, MockResponseReceipt Receipt);
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
