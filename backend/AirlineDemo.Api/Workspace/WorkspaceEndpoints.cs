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
        app.MapGet("/api/v{version:int}/workspace/portfolio", GetPortfolio);
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

    private static IResult GetPortfolio(
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

        if (session.User.Role is not ("executive" or "presenter"))
            return Results.Json(new { error = "Role not permitted." }, statusCode: StatusCodes.Status403Forbidden);

        if (contractVersion.HasValue && contractVersion.Value != ContractVersion)
            return Results.Json(new { error = "Unsupported contract version." }, statusCode: StatusCodes.Status400BadRequest);

        var cases = new List<PortfolioCase>();
        var unavailable = new List<string>();
        foreach (var caseId in session.User.CaseScopes.Distinct(StringComparer.Ordinal))
        {
            var scope = TechnicalReviewPolicy.ScopeFor(store, session.User.CaseScopes, caseId);
            if (scope is null)
            {
                unavailable.Add(caseId);
                continue;
            }

            var state = store.Read(scope);
            var openItems = (state.WorkItems ?? [])
                .Where(item => item.State == "open")
                .ToList();
            var technical = openItems.Count(item => item.Owner == "technical-reviewer");
            var finance = openItems.Count(item => item.Owner == "finance-reviewer");
            var legal = openItems.Count(item => item.Owner == "legal-reviewer");
            var presenter = openItems.Count(item => item.Owner == "presenter");

            string? nextOwner = null;
            long? oldestAge = null;
            if (openItems.Count > 0)
            {
                var oldest = openItems
                    .Select(item => new
                    {
                        Item = item,
                        Age = Math.Max(0L, (long)Math.Floor((time.GetUtcNow() - item.CreatedAt).TotalSeconds))
                    })
                    .OrderByDescending(row => row.Age)
                    .ThenBy(row => row.Item.ItemId, StringComparer.Ordinal)
                    .First();
                nextOwner = oldest.Item.Owner;
                oldestAge = oldest.Age;
            }

            PortfolioActivity? latestActivity = null;
            var latest = state.Audit.OrderByDescending(eventItem => eventItem.Sequence).FirstOrDefault();
            if (latest is not null)
                latestActivity = new PortfolioActivity(latest.Sequence, latest.Actor,
                    CapabilityProjection.LabelForAuditAction(latest.Action), latest.Action);

            cases.Add(new PortfolioCase(caseId,
                new Dictionary<string, int>
                {
                    ["technical-reviewer"] = technical,
                    ["finance-reviewer"] = finance,
                    ["legal-reviewer"] = legal,
                    ["presenter"] = presenter,
                }, nextOwner, oldestAge, latestActivity));
        }

        return Results.Ok(new PortfolioResponse(ContractVersion, cases, unavailable));
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

    private sealed record PortfolioResponse(
        int contractVersion,
        IReadOnlyList<PortfolioCase> cases,
        IReadOnlyList<string> unavailableCaseIds);

    private sealed record PortfolioCase(
        string caseId,
        IReadOnlyDictionary<string, int> openItemsByRole,
        string? nextDecisionOwner,
        long? oldestOpenAgeSeconds,
        PortfolioActivity? latestActivity);

    private sealed record PortfolioActivity(
        long sequence,
        string actor,
        string label,
        string outcome);
}
