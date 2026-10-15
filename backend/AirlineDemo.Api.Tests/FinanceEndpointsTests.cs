using System.Net;
using System.Net.Http.Json;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Obligations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

/// <summary>
/// Criterion-to-test map (issue #184):
/// AC1 reserve route: Scoped_gets_return_reserve_data (valid session and in-scope case, plus 401 and 404 denials).
/// AC2 obligation route: Scoped_gets_return_obligation_data (valid session and in-scope case, plus 401 and 404 denials).
/// AC3 mapping and scope checks: both tests fail if a route is unmapped or its session or case-scope check is removed.
/// </summary>
public sealed class FinanceEndpointsTests
{
    private const string Passcode = "test-passcode";
    private const string Case = "case-001";
    private static readonly DateTimeOffset AsOf = new(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Passcode"] = Passcode }));
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FixedTimeProvider(AsOf)));
        });

    private static async Task<string> SignInCookieAsync(HttpClient client, string userId)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    private static UsageReport Usage() => new("USE-2026-09", "2026-09", [new UsageEntry("FH-A", 100m), new UsageEntry("CYC-B", 4m)]);

    private static RateReport Rates() => new("RATE-2026-09", "2026-09", [new RateEntry("FH-A", "2026-09", 0.10m), new RateEntry("CYC-B", "2026-09", 12.345m)]);

    private static Obligation Obligation() => new(
        "OBL-1",
        2,
        new DateOnly(2026, 10, 10),
        new DateOnly(2026, 10, 14),
        new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero),
        [new LinkedRequirement("REQ-1", RequirementEvidence.Satisfied, 2)],
        new TechnicalReview("reviewer-1", true, 2));

    [Fact]
    public async Task Scoped_gets_return_reserve_data()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<FinanceInputStore>().Set(Case, new FinanceInputs(Usage(), Rates(), null, []));

        using var unauthenticated = await client.GetAsync($"/api/cases/{Case}/reserve");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var cookie = await SignInCookieAsync(client, "presenter");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/cases/{Case}/reserve");
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var draft = await response.Content.ReadFromJsonAsync<ReserveDraftDto>();
        Assert.NotNull(draft);
        Assert.Equal("RSV-case-001", draft!.DraftId);
        Assert.Equal(1, draft.Version);
        Assert.Equal("2026-09", draft.Period);
        Assert.Equal(ReserveDraftStatus.ReadyForReview, draft.Status);
        Assert.Equal("USE-2026-09", draft.UsageReportId);
        Assert.Equal("RATE-2026-09", draft.RateReportId);
        Assert.Equal(2, draft.Lines.Count);
        Assert.Equal(59.38m, draft.PayableTotal);
        Assert.Empty(draft.Blockers);
        Assert.Empty(draft.Discrepancies);
        Assert.False(string.IsNullOrWhiteSpace(draft.CalculationBasis));

        using var outOfScope = await SendAsync(client, $"/api/cases/case-999/reserve", cookie);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);

        var reviewerCookie = await SignInCookieAsync(client, "reviewer");
        using var reviewerOutOfScope = await SendAsync(client, "/api/cases/case-002/reserve", reviewerCookie);
        Assert.Equal(HttpStatusCode.NotFound, reviewerOutOfScope.StatusCode);
    }

    [Fact]
    public async Task Scoped_gets_return_obligation_data()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var obligation = Obligation();
        factory.Services.GetRequiredService<FinanceInputStore>().Set(Case, new FinanceInputs(null, null, obligation, []));
        var expected = ObligationEvaluator.Evaluate(obligation, AsOf, []);

        using var unauthenticated = await client.GetAsync($"/api/cases/{Case}/obligations");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var cookie = await SignInCookieAsync(client, "presenter");
        using var response = await SendAsync(client, $"/api/cases/{Case}/obligations", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var evaluation = await response.Content.ReadFromJsonAsync<ObligationEvaluation>();
        Assert.NotNull(evaluation);
        Assert.Equal(expected.State, evaluation!.State);
        Assert.Equal(expected.Reasons, evaluation.Reasons);
        Assert.Empty(evaluation.Drafts);
        Assert.Equal(expected.NewDraft is null, evaluation.NewDraft is null);

        using var outOfScope = await SendAsync(client, "/api/cases/case-999/obligations", cookie);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);

        var reviewerCookie = await SignInCookieAsync(client, "reviewer");
        using var reviewerOutOfScope = await SendAsync(client, "/api/cases/case-002/obligations", reviewerCookie);
        Assert.Equal(HttpStatusCode.NotFound, reviewerOutOfScope.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private sealed record ReserveDraftDto(
        string DraftId,
        int Version,
        string Period,
        ReserveDraftStatus Status,
        string? UsageReportId,
        string? RateReportId,
        IReadOnlyList<ReserveLine> Lines,
        decimal? PayableTotal,
        IReadOnlyList<BlockReason> Blockers,
        IReadOnlyList<string> Discrepancies,
        string CalculationBasis);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
