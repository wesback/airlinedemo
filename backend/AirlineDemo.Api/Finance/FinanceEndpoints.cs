using System.Collections.Concurrent;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Obligations;

namespace AirlineDemo.Api.Finance;

/// <summary>Deterministic inputs for one case: finance reports, plus the obligation and its notice drafts.</summary>
public sealed record FinanceInputs(
    UsageReport? Usage,
    RateReport? Rates,
    Obligation? Obligation,
    IReadOnlyList<NoticeDraft> NoticeDrafts);

public sealed class FinanceInputStore
{
    private readonly ConcurrentDictionary<string, FinanceInputs> _inputs = new(StringComparer.Ordinal);

    public void Set(string caseId, FinanceInputs inputs) => _inputs[caseId] = inputs;

    public FinanceInputs? Find(string caseId) => _inputs.TryGetValue(caseId, out var inputs) ? inputs : null;
}

public static class FinanceEndpoints
{
    public const string ReservePeriod = "2026-09";

    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cases/{caseId}/reserve", GetReserve);
        app.MapGet("/api/cases/{caseId}/obligations", GetObligations);
        return app;
    }

    private static IResult GetReserve(string caseId, HttpContext http, DemoSessionStore sessions, FinanceInputStore inputs)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!session.User.CaseScopes.Contains(caseId)) return Results.NotFound();

        var input = inputs.Find(caseId);
        return Results.Ok(ReserveCalculator.Calculate($"RSV-{caseId}", 1, ReservePeriod, input?.Usage, input?.Rates));
    }

    private static IResult GetObligations(string caseId, HttpContext http, DemoSessionStore sessions, FinanceInputStore inputs, TimeProvider time)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) return DemoAuthEndpoints.Unauthenticated();
        if (!session.User.CaseScopes.Contains(caseId)) return Results.NotFound();

        var input = inputs.Find(caseId);
        if (input?.Obligation is null) return Results.NotFound();
        return Results.Ok(ObligationEvaluator.Evaluate(input.Obligation, time.GetUtcNow(), input.NoticeDrafts));
    }
}
