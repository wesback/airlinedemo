using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Dashboard;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Workspace;

public static class WorkspaceEndpoints
{
    public const int ContractVersion = 1;
    private const int MaxItems = 200;

    private static readonly HashSet<string> WorklistRoles = new(StringComparer.Ordinal)
    {
        "presenter",
        CaseEndpoints.ReviewerRole,
        "finance-reviewer",
        "legal-reviewer",
    };

    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v{version:int}/workspace/worklist", GetWorklist);
        return app;
    }

    private static IResult GetWorklist(
        int version,
        int? contractVersion,
        HttpContext http,
        DemoSessionStore sessions,
        ICaseStateStore store,
        TimeProvider time)
    {
        if (version != ContractVersion)
            return Results.NotFound();

        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null)
            return DemoAuthEndpoints.Unauthenticated();

        if (!WorklistRoles.Contains(session.User.Role))
            return Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden);

        if (contractVersion.HasValue && contractVersion.Value != ContractVersion)
            return Results.Json(new { error = "Unsupported contract version." }, statusCode: StatusCodes.Status400BadRequest);

        var items = new List<WorklistItem>();
        var unavailable = new List<string>();
        var now = time.GetUtcNow();
        foreach (var caseId in session.User.CaseScopes.Distinct(StringComparer.Ordinal))
        {
            var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
            if (scope is null)
            {
                unavailable.Add(caseId);
                continue;
            }

            var state = store.Read(scope);
            foreach (var item in CapabilityProjection.Build(caseId, session.User.Role, state, now).Queue)
            {
                items.Add(new WorklistItem(caseId, item.ItemId, item.CapabilityId, item.State, item.Owner, item.AgeSeconds, item.Label));
            }
        }

        var ordered = items
            .OrderByDescending(item => item.ageSeconds)
            .ThenBy(item => item.caseId, StringComparer.Ordinal)
            .ThenBy(item => item.itemId, StringComparer.Ordinal)
            .Take(MaxItems + 1)
            .ToList();
        var truncated = ordered.Count > MaxItems;
        var resultItems = truncated ? ordered.Take(MaxItems).ToList() : ordered;

        return Results.Ok(new WorklistResponse(ContractVersion, resultItems, unavailable, truncated));
    }

    private sealed record WorklistItem(
        string caseId,
        string itemId,
        string capabilityId,
        string state,
        string owner,
        long ageSeconds,
        string label);

    private sealed record WorklistResponse(
        int contractVersion,
        IReadOnlyList<WorklistItem> items,
        IReadOnlyList<string> unavailableCaseIds,
        bool truncated);
}
