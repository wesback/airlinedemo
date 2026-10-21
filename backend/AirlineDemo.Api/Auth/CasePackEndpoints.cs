using System.Globalization;
using System.Text.Json;
using AirlineDemo.Api.Admission;
using AirlineDemo.Api.Dashboard;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Projection;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Reviews;
namespace AirlineDemo.Api.Auth;

/// <summary>The versioned envelope returned by the case-pack list endpoint.</summary>
public sealed record CasePackListResponse(int ContractVersion, string PackVersion, IReadOnlyList<CasePackCaseSummary> Cases);
/// <summary>The role-visible ARR state tokens for a case.</summary>
public sealed record CasePackCaseStates(
    string? Assessment,
    string? Review,
    string? Request,
    string? Finance,
    string? Legal,
    string? Acceptance);

/// <summary>A permitted action available to the signed-in user's role.</summary>
public sealed record CasePackActionSummary(string ActionId, string Text);

/// <summary>A synthetic, in-scope case summary combining the published pack and current ARR state.</summary>
public sealed record CasePackCaseSummary(
    string CaseId,
    string Name,
    string AircraftCategory,
    long CaseRevision,
    string CurrentRunId,
    CasePackCaseStates Status,
    string? Owner,
    CasePackActionSummary? NextAction,
    string? ActivityId,
    string CapabilityLabel,
    bool Synthetic);
public static class CasePackEndpoints
{
    private const int ContractVersion = 1;
    public static IEndpointRouteBuilder MapCasePackEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v{version:int}/cases", GetCases)
            .WithName("GetCasePackCases")
            .WithSummary("List authorized case-pack cases")
            .WithDescription("Returns summaries for published case-pack cases in the signed-in user's scope.")
            .Produces<CasePackListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status500InternalServerError)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        app.MapPost("/api/v{version:int}/cases", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed))
            .ExcludeFromDescription();

        app.MapGet("/api/v{version:int}/cases/{caseId}/scene", GetScene)
            .WithName("GetCasePackScene")
            .WithSummary("Read an authorized case-pack scene")
            .WithDescription("Returns the published, role-visible scene for one authorized case.");
        app.MapGet("/api/v{version:int}/cases/{*caseId}", GetScene);
        app.MapMethods("/api/v{version:int}/cases/{caseId}/scene",
                new[] { "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" },
                (int version) => version == ContractVersion
                    ? Error(StatusCodes.Status405MethodNotAllowed, "method_not_allowed", "method_not_allowed")
                    : Error(StatusCodes.Status400BadRequest, "unsupported_contract_version", "unsupported_contract_version"))
            .ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> GetScene(
        int version,
        string caseId,
        HttpContext http,
        DemoSessionStore sessions,
        CasePackRequestLimiter limiter,
        IAircraftCasePackSnapshotStore snapshots,
        ICaseStateStore caseStates,
        EvidencePackageStore packages,
        IArrCaseHistorySource history,
        CancellationToken cancellationToken)
    {
        if (caseId.EndsWith("/scene", StringComparison.Ordinal))
            caseId = caseId[..^"/scene".Length];
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "unauthenticated");
        if (version != ContractVersion)
            return Error(StatusCodes.Status400BadRequest, "unsupported_contract_version", "unsupported_contract_version");

        try
        {
            if (!await limiter.TryRecordAttemptAsync(session.User.Id, CasePackRequestLimiter.SceneEndpoint, cancellationToken))
                return Error(StatusCodes.Status429TooManyRequests, "rate_limit_exceeded", "rate_limit_exceeded");

            if (!session.User.CaseScopes.Contains(caseId, StringComparer.Ordinal))
                return SceneNotFound();

            var snapshot = snapshots.Read();
            var published = snapshot?.Cases.SingleOrDefault(item =>
                StringComparer.Ordinal.Equals(item.CaseId, caseId));
            if (snapshot is null || published is null)
                return SceneNotFound();

            var owner = caseStates.OwnerOf(caseId);
            if (owner is null)
                return SceneNotFound();

            CaseScope scope;
            try { scope = caseStates.AuthorizeCase(caseId, owner); }
            catch (CaseAccessDeniedException) { return SceneNotFound(); }

            var result = CasePackSceneProjection.BuildResult(
                caseStates, scope, snapshot, session.User.Role, packages, history);
            if (!result.Succeeded)
            {
                if (result.IsPayloadLimit)
                    return Error(StatusCodes.Status422UnprocessableEntity,
                        CasePackSceneProjection.PayloadLimitCode, CasePackSceneProjection.PayloadLimitCode);
                if (result.ErrorCode == "invalid_scene") return SceneNotFound();
                return Error(StatusCodes.Status500InternalServerError,
                    "internal_error", "The request could not be completed.");
            }

            return Results.Ok(result.Scene);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error(StatusCodes.Status500InternalServerError,
                "internal_error", "The request could not be completed.");
        }
    }

    private static IResult SceneNotFound() =>
        Error(StatusCodes.Status404NotFound, "not_found", "Not found.");
    private static async Task<IResult> GetCases(
        int version,
        HttpContext http,
        DemoSessionStore sessions,
        CasePackRequestLimiter limiter,
        IAircraftCasePackSnapshotStore snapshots,
        ICaseStateStore caseStates,
        TimeProvider time,
        ILogger<DemoSessionStore> logger,
        CancellationToken cancellationToken)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "unauthenticated");
        if (version != ContractVersion)
            return Error(StatusCodes.Status400BadRequest, "unsupported_contract_version", "unsupported_contract_version");
        if (!CanReadSummaries(session.User.Role))
            return Error(StatusCodes.Status403Forbidden, "forbidden", "forbidden");

        try
        {
            if (!await limiter.TryRecordAttemptAsync(
                    session.User.Id, CasePackRequestLimiter.CaseListEndpoint, cancellationToken))
                return Error(StatusCodes.Status429TooManyRequests, "rate_limit_exceeded", "rate_limit_exceeded");

            var snapshot = snapshots.Read();
            if (snapshot is null)
                return Error(StatusCodes.Status503ServiceUnavailable, "case_pack_unavailable", "case_pack_unavailable");

            var summaries = new List<CasePackCaseSummary>();
            foreach (var item in snapshot.Cases)
            {
                if (!session.User.CaseScopes.Contains(item.CaseId, StringComparer.Ordinal)
                    || Metadata(item.CaseId) is not { } metadata)
                    continue;

                CaseState? state = null;
                var ownerId = caseStates.OwnerOf(item.CaseId);
                if (ownerId is not null)
                    state = caseStates.Read(caseStates.AuthorizeCase(item.CaseId, ownerId));

                summaries.Add(Project(item.CaseId, metadata.Name, metadata.Category, state, session.User.Role, time.GetUtcNow()));
            }
            return Results.Ok(new CasePackListResponse(ContractVersion, snapshot.PackVersion, summaries));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Case-pack list read failed.");
            return Error(StatusCodes.Status500InternalServerError, "internal_error", "The request could not be completed.");
        }
    }
    private static CasePackCaseSummary Project(
        string caseId, string name, string category, CaseState? state, string role, DateTimeOffset now)
    {
        if (state is null)
            return new(caseId, name, category, 0, string.Empty, EmptyStates, null, null, null,
                CapabilityLabels.Unknown, Synthetic: true);

        var currentBasisVersion = state.Basis.Count;
        var latestJourney = state.JourneyEvents?
            .Where(item => item.CaseId == caseId)
            .MaxBy(item => item.Sequence);
        var latestAudit = state.Audit.LastOrDefault();
        var currentRunId = latestJourney?.RunId
            ?? state.Traces?.LastOrDefault(item => item.CaseId == caseId)?.RunId
            ?? string.Empty;
        var nextItem = CapabilityProjection.NextDecisionItem(
            CapabilityProjection.Build(caseId, role, state, now).Queue);
        var latestReview = TechnicalReviewPolicy.Decisions(state).LastOrDefault(item => item.Current);
        var latestRequest = state.RequestReceipts?.LastOrDefault(item =>
            item.CaseId == caseId && item.BasisVersion == currentBasisVersion);
        var latestAssessment = state.Assessments?.LastOrDefault(item =>
            item.CaseId == caseId && item.BasisVersion == currentBasisVersion);
        var activityLabel = latestJourney is not null && latestAudit?.Action == latestJourney.EventType
            ? latestJourney.CapabilityLabel
            : latestAudit is null ? null : CapabilityProjection.LabelForAuditAction(latestAudit.Action);
        var capabilityLabel = activityLabel is null
            ? CapabilityLabels.Unknown
            : CapabilityLabels.Normalize(activityLabel);

        var states = new CasePackCaseStates(
            latestAssessment?.Outcome,
            state.WorkItems?.LastOrDefault(item => item.CapabilityId == AssessmentExecutionService.ReviewCapability)?.State,
            latestRequest is null ? null : "delivered",
            ReadDecision(state, FinanceEndpoints.ReserveDecisionAction),
            ReadDecision(state, FinanceEndpoints.LegalDecisionAction),
            latestReview?.Decision);

        return new CasePackCaseSummary(
            caseId,
            name,
            category,
            state.Revision,
            currentRunId,
            states,
            nextItem?.Owner,
            nextItem is null ? null : new CasePackActionSummary(nextItem.ItemId, nextItem.Label),
            latestAudit?.Sequence.ToString(CultureInfo.InvariantCulture),
            capabilityLabel,
            Synthetic: true);
    }
    private static string? ReadDecision(CaseState state, string action)
    {
        var audit = state.Audit.LastOrDefault(item => item.Action == action);
        if (audit is null) return null;

        using var document = JsonDocument.Parse(audit.Detail);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !(root.TryGetProperty("decision", out var decision)
                || root.TryGetProperty("Decision", out decision))
            || decision.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(decision.GetString()))
            throw new InvalidDataException("A decision audit record is invalid.");

        return decision.GetString();
    }
    private static bool CanReadSummaries(string role) =>
        role is "technical-reviewer" or "finance-reviewer" or "legal-reviewer" or "presenter" or "executive";

    private static (string Name, string Category)? Metadata(string caseId) => caseId switch
    {
        "cp-case-a" => ("Case A", "narrow-body"),
        "cp-case-b" => ("Case B", "regional"),
        "cp-case-c" => ("Case C", "wide-body"),
        _ => null,
    };

    private static readonly CasePackCaseStates EmptyStates = new(null, null, null, null, null, null);
    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new { contractVersion = ContractVersion, error = new { code, message } }, statusCode: statusCode);
}
