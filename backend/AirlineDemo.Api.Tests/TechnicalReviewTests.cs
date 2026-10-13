using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class TechnicalReviewTests : IDisposable
{
    private const string Passcode = "test-passcode";
    private const string EvidenceText = "confidential-evidence-v1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "technical-review-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TechnicalReviewTests()
    {
        var path = Path.Combine(_dir, "cases.json");
        Seed(path);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = Passcode,
                ["Cases:StatePath"] = path,
            })));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed record Session(string Cookie, string Csrf);

    private CaseStateStore Store => _factory.Services.GetRequiredService<CaseStateStore>();

    private static void Seed(string path)
    {
        var store = new CaseStateStore(path);
        foreach (var caseId in new[] { "case-001", "case-002" })
        {
            store.CreateCase(caseId, "system");
            AddBasis(store, caseId, EvidenceText);
        }
    }

    private static void AddBasis(CaseStateStore store, string caseId, string content)
    {
        var scope = store.AuthorizeExistingCase(caseId);
        var state = store.Read(scope);
        var prior = state.Basis.Count == 0 ? string.Empty : state.Basis[^1].BasisDigest;
        var next = CaseStateStore.CreateBasisVersion(state.Basis.Count + 1, content, prior);
        store.Commit(scope, state.Revision, new CaseCommit(next, null, "system", "submit-package", "basis " + next.Version));
    }

    private async Task<Session> SignInAsync(string userId, CancellationToken ct)
    {
        using var response = await _client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new Session(cookie, body.GetProperty("csrfToken").GetString()!);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Session session, bool withCsrf, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", session.Cookie);
        if (withCsrf) request.Headers.Add("X-CSRF-Token", session.Csrf);
        if (body is not null) request.Content = JsonContent.Create(body);
        return _client.SendAsync(request, ct);
    }

    private Task<HttpResponseMessage> DecideAsync(Session session, string caseId, object body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/api/cases/{caseId}/technical-review/decisions", session, withCsrf: true, body, ct);

    private long Revision(string caseId) => Store.Read(Store.AuthorizeExistingCase(caseId)).Revision;

    private int DecisionCount(string caseId) =>
        Store.ReadAudit(Store.AuthorizeExistingCase(caseId)).Count(e => e.Action == TechnicalReviewEndpoints.DecisionAction);

    private BasisVersion CurrentBasis(string caseId) =>
        Store.Read(Store.AuthorizeExistingCase(caseId)).Basis[^1];

    private static object Command(long revision, BasisVersion basis, string decision = "accept", string? reason = "Evidence supports release.") =>
        new { expectedRevision = revision, decision, reason, basisVersion = basis.Version, basisDigest = basis.BasisDigest };

    // AC: M-08
    [Fact]
    public async Task Authorized_reviewer_rejection_is_recorded_and_does_not_authorize_acceptance()
    {
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync("reviewer", ct);
        var revision = Revision("case-001");
        var basis = CurrentBasis("case-001");

        using var response = await DecideAsync(reviewer, "case-001", Command(revision, basis, "reject", "Evidence does not support release."), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("rejected", body.GetProperty("disposition").GetString());
        Assert.False(body.GetProperty("authorizesAcceptance").GetBoolean());
        Assert.DoesNotContain(EvidenceText, body.GetRawText());

        var recorded = body.GetProperty("decisions").EnumerateArray().Single();
        Assert.Equal("reject", recorded.GetProperty("outcome").GetString());
        Assert.True(recorded.GetProperty("current").GetBoolean());

        var decision = Store.ReadAudit(Store.AuthorizeExistingCase("case-001")).Last();
        Assert.Equal(TechnicalReviewEndpoints.DecisionAction, decision.Action);
        Assert.Equal("reviewer", decision.Actor);
        Assert.Contains("Evidence does not support release.", decision.Detail);
        Assert.DoesNotContain(EvidenceText, decision.Detail);
    }

    // AC: M-08
    [Fact]
    public async Task Authorized_reviewer_records_reasoned_decision_against_current_basis()
    {
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync("reviewer", ct);
        var revision = Revision("case-001");
        var basis = CurrentBasis("case-001");

        using var response = await DecideAsync(reviewer, "case-001", Command(revision, basis), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("accepted", body.GetProperty("disposition").GetString());
        Assert.True(body.GetProperty("authorizesAcceptance").GetBoolean());
        Assert.DoesNotContain(EvidenceText, body.GetRawText());

        var recorded = body.GetProperty("decisions").EnumerateArray().Single();
        Assert.True(recorded.GetProperty("sequence").GetInt64() > 0);
        Assert.NotEqual(default, recorded.GetProperty("at").GetDateTimeOffset());
        Assert.False(string.IsNullOrEmpty(recorded.GetProperty("traceId").GetString()));

        var decision = Store.ReadAudit(Store.AuthorizeExistingCase("case-001")).Last();
        Assert.Equal(TechnicalReviewEndpoints.DecisionAction, decision.Action);
        Assert.Equal("reviewer", decision.Actor);
        Assert.Equal(revision + 1, decision.Revision);
        Assert.Equal(decision.Sequence, recorded.GetProperty("sequence").GetInt64());
        Assert.NotEqual(default, decision.At);
        Assert.Contains("Evidence supports release.", decision.Detail);
        Assert.Contains(basis.BasisDigest, decision.Detail);
        Assert.Contains(recorded.GetProperty("traceId").GetString()!, decision.Detail);
        Assert.DoesNotContain(EvidenceText, decision.Detail);
    }

    // AC: M-02
    [Fact]
    public async Task Forged_role_actor_and_case_fields_from_non_reviewer_are_denied_without_mutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var presenter = await SignInAsync("presenter", ct);
        var revision = Revision("case-001");
        var basis = CurrentBasis("case-001");

        using var response = await DecideAsync(presenter, "case-001", new
        {
            expectedRevision = revision,
            decision = "accept",
            reason = "Evidence supports release.",
            basisVersion = basis.Version,
            basisDigest = basis.BasisDigest,
            role = CaseEndpoints.ReviewerRole,
            actor = "reviewer",
            caseId = "case-002",
        }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(revision, Revision("case-001"));
        Assert.Equal(0, DecisionCount("case-001"));
        Assert.Equal(0, DecisionCount("case-002"));
    }

    // AC: M-02
    [Fact]
    public async Task Forged_actor_and_case_fields_from_reviewer_are_ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync("reviewer", ct);
        var revision = Revision("case-001");
        var basis = CurrentBasis("case-001");
        var otherRevision = Revision("case-002");

        using var response = await DecideAsync(reviewer, "case-001", new
        {
            expectedRevision = revision,
            decision = "accept",
            reason = "Evidence supports release.",
            basisVersion = basis.Version,
            basisDigest = basis.BasisDigest,
            actor = "presenter",
            reviewerId = "presenter",
            role = "presenter",
            caseId = "case-002",
        }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = Store.ReadAudit(Store.AuthorizeExistingCase("case-001")).Last();
        Assert.Equal("reviewer", decision.Actor);
        Assert.Equal(otherRevision, Revision("case-002"));
        Assert.Equal(0, DecisionCount("case-002"));
    }

    // AC: M-02
    [Fact]
    public async Task Non_reviewer_role_is_denied_without_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        var presenter = await SignInAsync("presenter", ct);
        var revision = Revision("case-001");

        using var response = await DecideAsync(presenter, "case-001", Command(revision, CurrentBasis("case-001")), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(revision, Revision("case-001"));
        Assert.Equal(0, DecisionCount("case-001"));
    }

    // AC: M-08
    [Fact]
    public async Task Decision_without_reason_is_denied_without_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync("reviewer", ct);
        var revision = Revision("case-001");

        using var missing = await DecideAsync(reviewer, "case-001", Command(revision, CurrentBasis("case-001"), reason: null), ct);
        using var blank = await DecideAsync(reviewer, "case-001", Command(revision, CurrentBasis("case-001"), reason: "   "), ct);

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(revision, Revision("case-001"));
        Assert.Equal(0, DecisionCount("case-001"));
    }

    // AC: M-10
    [Fact]
    public async Task Decision_against_stale_basis_is_denied_without_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync("reviewer", ct);
        var revision = Revision("case-001");
        var basis = CurrentBasis("case-001");

        using var response = await DecideAsync(reviewer, "case-001", new { expectedRevision = revision, decision = "accept", reason = "ok", basisVersion = basis.Version, basisDigest = "forged" }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(revision, Revision("case-001"));
        Assert.Equal(0, DecisionCount("case-001"));
    }

    // AC: M-02
    [Fact]
    public async Task Decision_without_anti_forgery_token_is_denied_without_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync("reviewer", ct);
        var revision = Revision("case-001");

        using var response = await SendAsync(HttpMethod.Post, "/api/cases/case-001/technical-review/decisions", reviewer, withCsrf: false,
            Command(revision, CurrentBasis("case-001")), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(revision, Revision("case-001"));
        Assert.Equal(0, DecisionCount("case-001"));
    }

    // AC: M-02
    [Fact]
    public async Task Stale_revision_is_denied_without_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync("reviewer", ct);
        var revision = Revision("case-001");

        using var response = await DecideAsync(reviewer, "case-001", Command(revision - 1, CurrentBasis("case-001")), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(revision, Revision("case-001"));
        Assert.Equal(0, DecisionCount("case-001"));
    }

    // AC: M-02
    [Fact]
    public async Task Cross_case_access_reveals_nothing_and_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync("reviewer", ct);
        var revision = Revision("case-002");

        using var read = await SendAsync(HttpMethod.Get, "/api/cases/case-002/technical-review", reviewer, withCsrf: false, null, ct);
        using var write = await DecideAsync(reviewer, "case-002", Command(revision, CurrentBasis("case-002")), ct);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.Equal(revision, Revision("case-002"));
        Assert.Equal(0, DecisionCount("case-002"));
    }

    // AC: M-10
    [Fact]
    public async Task Later_basis_marks_prior_decision_non_current_and_keeps_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var reviewer = await SignInAsync("reviewer", ct);
        var firstBasis = CurrentBasis("case-001");
        using (var accepted = await DecideAsync(reviewer, "case-001", Command(Revision("case-001"), firstBasis), ct))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        AddBasis(Store, "case-001", "evidence-v2");

        using var view = await SendAsync(HttpMethod.Get, "/api/cases/case-001/technical-review", reviewer, withCsrf: false, null, ct);
        var body = await view.Content.ReadFromJsonAsync<JsonElement>(ct);
        var decisions = body.GetProperty("decisions");
        Assert.Single(decisions.EnumerateArray());
        Assert.False(decisions[0].GetProperty("current").GetBoolean());
        Assert.False(body.GetProperty("authorizesAcceptance").GetBoolean());
        Assert.Equal("awaiting-review", body.GetProperty("disposition").GetString());
        Assert.Equal(firstBasis.BasisDigest, decisions[0].GetProperty("basisDigest").GetString());

        using var replay = await DecideAsync(reviewer, "case-001", Command(Revision("case-001"), firstBasis), ct);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(1, DecisionCount("case-001"));
    }
}
