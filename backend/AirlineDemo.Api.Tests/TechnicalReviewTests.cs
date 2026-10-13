using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class TechnicalReviewTests : IDisposable
{
    private const string Passcode = "test-passcode";
    private const string CaseId = "case-001";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tech-review-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public TechnicalReviewTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = Passcode,
                    ["CaseState:Path"] = Path.Combine(_dir, "cases.json"),
                })));
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed record Session(string Cookie, string Csrf);

    private CaseStateStore Store => _factory.Services.GetRequiredService<CaseStateStore>();

    /// <summary>Creates case-001 with one basis version and returns its revision after the basis commit.</summary>
    private long SeedCaseWithBasis(int versions = 1)
    {
        var scope = Store.CreateCase(CaseId, "presenter");
        long revision = 0;
        for (var v = 1; v <= versions; v++)
        {
            var previous = v == 1 ? string.Empty : Store.GetBasisVersion(scope, v - 1).BasisDigest;
            var basis = CaseStateStore.CreateBasisVersion(v, "evidence-" + v, previous);
            revision = Store.Commit(scope, revision, new CaseCommit(basis, null, "presenter", "submit-package", "basis " + v)).State.Revision;
        }
        return revision;
    }

    private async Task<Session> SignInAsync(string userId)
    {
        var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Session(cookie, body.GetProperty("csrfToken").GetString()!);
    }

    private async Task<HttpResponseMessage> PostDecisionAsync(string caseId, Session? session, bool withCsrf, object body)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/cases/{caseId}/technical-review/decisions")
        {
            Content = JsonContent.Create(body),
        };
        if (session is not null) request.Headers.Add("Cookie", session.Cookie);
        if (session is not null && withCsrf) request.Headers.Add("X-CSRF-Token", session.Csrf);
        return await client.SendAsync(request);
    }

    private static object Decide(long revision, int basis = 1, string decision = "accept", string? reason = "Checked against basis.") =>
        new { expectedRevision = revision, basisVersion = basis, decision, reason };

    [Fact]
    public async Task Authorized_reviewer_records_reasoned_decision_with_audit_provenance()
    {
        var revision = SeedCaseWithBasis();
        var reviewer = await SignInAsync("reviewer");

        using var response = await PostDecisionAsync(CaseId, reviewer, withCsrf: true, Decide(revision));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = Store.Read(Store.AuthorizeCase(CaseId, "presenter"));
        var decision = TechnicalReviewEndpoints.CurrentDecision(state);
        Assert.NotNull(decision);
        Assert.Equal("accept", decision!.Outcome);
        Assert.Equal("Checked against basis.", decision.Reason);
        Assert.Equal(1, decision.BasisVersion);
        Assert.True(decision.IsCurrent);
        Assert.Equal("reviewer", state.Audit[^1].Actor);
        Assert.Equal(TechnicalReviewEndpoints.DecisionAction, state.Audit[^1].Action);
        Assert.True(TechnicalReviewEndpoints.AuthorizesAcceptance(state));
    }

    [Fact]
    public async Task Unauthorized_role_is_denied_and_creates_no_decision()
    {
        var revision = SeedCaseWithBasis();
        var presenter = await SignInAsync("presenter");

        using var response = await PostDecisionAsync(CaseId, presenter, withCsrf: true, Decide(revision));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(TechnicalReviewEndpoints.CurrentDecision(Store.Read(Store.AuthorizeCase(CaseId, "presenter"))));
    }

    [Fact]
    public async Task Missing_reason_is_denied_without_decision()
    {
        var revision = SeedCaseWithBasis();
        var reviewer = await SignInAsync("reviewer");

        using var response = await PostDecisionAsync(CaseId, reviewer, withCsrf: true, Decide(revision, reason: "   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(TechnicalReviewEndpoints.CurrentDecision(Store.Read(Store.AuthorizeCase(CaseId, "presenter"))));
    }

    [Fact]
    public async Task Missing_anti_forgery_token_is_denied_without_decision()
    {
        var revision = SeedCaseWithBasis();
        var reviewer = await SignInAsync("reviewer");

        using var response = await PostDecisionAsync(CaseId, reviewer, withCsrf: false, Decide(revision));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(TechnicalReviewEndpoints.CurrentDecision(Store.Read(Store.AuthorizeCase(CaseId, "presenter"))));
    }

    [Fact]
    public async Task Decision_on_stale_basis_is_denied()
    {
        var revision = SeedCaseWithBasis();
        var reviewer = await SignInAsync("reviewer");

        using var response = await PostDecisionAsync(CaseId, reviewer, withCsrf: true, Decide(revision, basis: 0));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(TechnicalReviewEndpoints.CurrentDecision(Store.Read(Store.AuthorizeCase(CaseId, "presenter"))));
    }

    [Fact]
    public async Task Out_of_scope_case_reveals_nothing()
    {
        SeedCaseWithBasis();
        Store.CreateCase("case-002", "presenter");
        var reviewer = await SignInAsync("reviewer");

        using var response = await PostDecisionAsync("case-002", reviewer, withCsrf: true, Decide(0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Later_basis_keeps_prior_decision_but_marks_it_non_current()
    {
        var revision = SeedCaseWithBasis();
        var reviewer = await SignInAsync("reviewer");
        using (var first = await PostDecisionAsync(CaseId, reviewer, withCsrf: true, Decide(revision)))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var scope = Store.AuthorizeCase(CaseId, "presenter");
        var basis2 = CaseStateStore.CreateBasisVersion(2, "evidence-2", Store.GetBasisVersion(scope, 1).BasisDigest);
        Store.Commit(scope, Store.Read(scope).Revision, new CaseCommit(basis2, null, "presenter", "submit-package", "basis 2"));

        var state = Store.Read(scope);
        var decision = TechnicalReviewEndpoints.CurrentDecision(state);
        Assert.NotNull(decision);
        Assert.Equal("accept", decision!.Outcome);
        Assert.False(decision.IsCurrent);
        Assert.False(TechnicalReviewEndpoints.AuthorizesAcceptance(state));
        Assert.Contains(state.Audit, a => a.Action == TechnicalReviewEndpoints.DecisionAction);
    }
}
