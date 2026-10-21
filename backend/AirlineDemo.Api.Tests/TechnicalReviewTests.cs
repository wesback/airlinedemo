using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AirlineDemo.Api.Tests;

public sealed class TechnicalReviewTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string CaseId = "case-a";
    private const string Reviewer = "reviewer-1";
    private static readonly string[] InScope = [CaseId];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tech-review-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private CaseStateStore NewStore() => new(Path.Combine(_dir, "cases.json"));

    private static (CaseStateStore Store, long Revision) WithBasis(CaseStateStore store, string content)
    {
        var scope = store.CreateCase(CaseId, Owner);
        var basis = CaseStateStore.CreateBasisVersion(1, content, string.Empty);
        store.Commit(scope, 0, new CaseCommit(basis, null, "presenter", "submit-evidence", "basis 1"));
        return (store, 1);
    }

    private static TechnicalReviewCommand Accept(long revision, int basis, string? reason = "Evidence supports return.") =>
        new(revision, basis, "accept", reason);

    [Fact]
    public void Authorized_reviewer_records_reasoned_decision_with_provenance()
    {
        var (store, rev) = WithBasis(NewStore(), "evidence v1");

        var result = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1), "trace-1");

        Assert.Equal(TechnicalReviewOutcome.Recorded, result.Outcome);
        var decision = result.Decision!;
        Assert.Equal(Reviewer, decision.Actor);
        Assert.Equal("Evidence supports return.", decision.Reason);
        Assert.Equal(1, decision.BasisVersion);
        Assert.Equal("approved", decision.Outcome);
        Assert.True(decision.Current);
        var audit = store.ReadAudit(store.AuthorizeCase(CaseId, Owner)).Single(a => a.Action == TechnicalReviewPolicy.DecisionAction);
        Assert.Equal(Reviewer, audit.Actor);
        Assert.Equal(2, audit.Revision);
        Assert.Equal(audit.Sequence, decision.Sequence);
        Assert.Equal(audit.At, decision.At);
    }

    [Theory]
    [InlineData("presenter")]
    [InlineData("viewer")]
    [InlineData("executive")]
    public void Unauthorized_role_is_denied_without_decision(string role)
    {
        var (store, rev) = WithBasis(NewStore(), "evidence v1");
        var before = store.Read(store.AuthorizeCase(CaseId, Owner)).Revision;

        var result = TechnicalReviewPolicy.Decide(store, InScope, role, "someone", CaseId, Accept(rev, 1), "trace-1");

        Assert.Equal(TechnicalReviewOutcome.Forbidden, result.Outcome);
        Assert.Equal(before, store.Read(store.AuthorizeCase(CaseId, Owner)).Revision);
        Assert.Empty(TechnicalReviewPolicy.Decisions(store.Read(store.AuthorizeCase(CaseId, Owner))));
    }

    [Fact]
    public void Missing_or_blank_reason_is_denied_without_decision()
    {
        var (store, rev) = WithBasis(NewStore(), "evidence v1");
        var scope = store.AuthorizeCase(CaseId, Owner);

        var missing = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1, reason: null), "trace-1");
        var blank = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1, reason: "   "), "trace-2");

        Assert.Equal(TechnicalReviewOutcome.Invalid, missing.Outcome);
        Assert.Equal(TechnicalReviewOutcome.Invalid, blank.Outcome);
        Assert.Equal(rev, store.Read(scope).Revision);
        Assert.Empty(TechnicalReviewPolicy.Decisions(store.Read(scope)));
    }

    [Fact]
    public void Case_outside_scope_is_not_found_without_decision()
    {
        var (store, rev) = WithBasis(NewStore(), "evidence v1");

        var result = TechnicalReviewPolicy.Decide(store, ["other-case"], CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1), "trace-1");

        Assert.Equal(TechnicalReviewOutcome.NotFound, result.Outcome);
        Assert.Empty(TechnicalReviewPolicy.Decisions(store.Read(store.AuthorizeCase(CaseId, Owner))));
    }

    [Fact]
    public void Stale_basis_or_revision_is_denied_without_decision()
    {
        var (store, rev) = WithBasis(NewStore(), "evidence v1");

        var staleBasis = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 0), "trace-1");
        var staleRevision = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev - 1, 1), "trace-2");

        Assert.Equal(TechnicalReviewOutcome.Stale, staleBasis.Outcome);
        Assert.Equal(TechnicalReviewOutcome.Stale, staleRevision.Outcome);
        Assert.Empty(TechnicalReviewPolicy.Decisions(store.Read(store.AuthorizeCase(CaseId, Owner))));
    }

    [Fact]
    public void Later_basis_marks_prior_acceptance_non_current_and_stops_it_authorizing()
    {
        var store = NewStore();
        var (_, rev) = WithBasis(store, "evidence v1");
        var decided = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1), "trace-1");
        Assert.True(TechnicalReviewPolicy.AuthorizesRequest(store.Read(store.AuthorizeCase(CaseId, Owner))));

        var scope = store.AuthorizeCase(CaseId, Owner);
        var next = CaseStateStore.CreateBasisVersion(2, "evidence v2", store.Read(scope).Basis[0].BasisDigest);
        store.Commit(scope, decided.Decision!.Revision, new CaseCommit(next, null, "presenter", "submit-evidence", "basis 2"));

        var state = store.Read(scope);
        var prior = Assert.Single(TechnicalReviewPolicy.Decisions(state));
        Assert.False(prior.Current);
        Assert.Equal(1, prior.BasisVersion);
        Assert.Equal("Evidence supports return.", prior.Reason);
        Assert.False(TechnicalReviewPolicy.AuthorizesRequest(state));
    }

    [Fact]
    public void Later_rejection_on_same_basis_revokes_earlier_acceptance()
    {
        var store = NewStore();
        var (_, rev) = WithBasis(store, "evidence v1");
        var accepted = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1), "trace-1");
        Assert.True(TechnicalReviewPolicy.AuthorizesRequest(store.Read(store.AuthorizeCase(CaseId, Owner))));

        var rejected = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            new TechnicalReviewCommand(accepted.Decision!.Revision, 1, "reject", "Evidence no longer supports return."), "trace-2");

        Assert.Equal(TechnicalReviewOutcome.Recorded, rejected.Outcome);
        var state = store.Read(store.AuthorizeCase(CaseId, Owner));
        Assert.Equal(2, TechnicalReviewPolicy.Decisions(state).Count);
        Assert.False(TechnicalReviewPolicy.AuthorizesRequest(state));
    }

    [Fact]
    public void Decision_on_superseded_basis_cannot_be_recorded()
    {
        var store = NewStore();
        var (_, rev) = WithBasis(store, "evidence v1");
        var scope = store.AuthorizeCase(CaseId, Owner);
        var next = CaseStateStore.CreateBasisVersion(2, "evidence v2", store.Read(scope).Basis[0].BasisDigest);
        store.Commit(scope, rev, new CaseCommit(next, null, "presenter", "submit-evidence", "basis 2"));

        var result = TechnicalReviewPolicy.Decide(store, InScope, CaseEndpoints.ReviewerRole, Reviewer, CaseId,
            Accept(rev, 1), "trace-1");

        Assert.Equal(TechnicalReviewOutcome.Stale, result.Outcome);
        Assert.Empty(TechnicalReviewPolicy.Decisions(store.Read(scope)));
    }

    private const string Passcode = "test-passcode";
    private const string HttpCase = "case-001";
    private const string OtherCase = "case-002";

    private sealed record HttpSession(string Cookie, string Csrf);

    private static WebApplicationFactory<Program> CreateFactory(string path) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = Passcode,
                    ["CaseState:Path"] = path,
                })));

    /// <summary>Seeds case-001 (reviewer scope) with basis 1 and case-002 (reviewer out of scope), both owned by the presenter.</summary>
    private static void SeedCases(string path)
    {
        var store = new CaseStateStore(path);
        var scope = store.CreateCase(HttpCase, "presenter");
        store.Commit(scope, 0, new CaseCommit(CaseStateStore.CreateBasisVersion(1, "evidence v1", string.Empty), null, "presenter", "submit-evidence", "basis 1"));
        store.CreateCase(OtherCase, "presenter");
    }

    private static CaseState ReadCase(string path, string caseId)
    {
        var store = new CaseStateStore(path);
        return store.Read(store.AuthorizeCase(caseId, "presenter"));
    }

    private static async Task<HttpSession> SignInAsync(HttpClient client, string userId, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new HttpSession(cookie, body.GetProperty("csrfToken").GetString()!);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, HttpSession? session, bool withCsrf, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        if (session is not null) request.Headers.Add("Cookie", session.Cookie);
        if (session is not null && withCsrf) request.Headers.Add("X-CSRF-Token", session.Csrf);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request, ct);
    }

    private static object AcceptBody(long revision, int basis, string? reason = "Evidence supports return.") =>
        new { expectedRevision = revision, expectedBasisVersion = basis, decision = "accept", reason };

    private static string DecisionUrl(string caseId) => $"/api/cases/{caseId}/technical-reviews";

    [Fact]
    public async Task Endpoint_records_decision_with_audit_provenance()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "cases.json");
        SeedCases(path);
        using var factory = CreateFactory(path);
        using var client = factory.CreateClient();
        var reviewer = await SignInAsync(client, "reviewer", ct);

        using var response = await SendAsync(client, HttpMethod.Post, DecisionUrl(HttpCase), reviewer, true, AcceptBody(1, 1), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("reviewer", decision.GetProperty("actor").GetString());
        Assert.True(decision.GetProperty("current").GetBoolean());
        var state = ReadCase(path, HttpCase);
        Assert.Equal(2, state.Revision);
        var audit = Assert.Single(state.Audit, a => a.Action == TechnicalReviewPolicy.DecisionAction);
        Assert.Equal("reviewer", audit.Actor);
        Assert.Equal(decision.GetProperty("sequence").GetInt64(), audit.Sequence);
        Assert.Equal(audit.At.UtcTicks, decision.GetProperty("at").GetDateTimeOffset().UtcTicks);
    }

    [Fact]
    public async Task Endpoint_denies_missing_anti_forgery_token_without_mutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "cases.json");
        SeedCases(path);
        using var factory = CreateFactory(path);
        using var client = factory.CreateClient();
        var reviewer = await SignInAsync(client, "reviewer", ct);

        using var response = await SendAsync(client, HttpMethod.Post, DecisionUrl(HttpCase), reviewer, false, AcceptBody(1, 1), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, ReadCase(path, HttpCase).Revision);
        Assert.Empty(TechnicalReviewPolicy.Decisions(ReadCase(path, HttpCase)));
    }

    [Fact]
    public async Task Endpoint_ignores_forged_actor_role_and_case_identity_without_mutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "cases.json");
        SeedCases(path);
        using var factory = CreateFactory(path);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter", ct);
        var reviewer = await SignInAsync(client, "reviewer", ct);

        var forged = new
        {
            expectedRevision = 1,
            expectedBasisVersion = 1,
            decision = "accept",
            reason = "Evidence supports return.",
            actorId = "reviewer",
            actor = "reviewer",
            role = "reviewer",
            userRole = "reviewer",
            caseId = OtherCase,
            caseScope = OtherCase,
        };
        using var post = await SendAsync(client, HttpMethod.Post, DecisionUrl(HttpCase), presenter, true, forged, ct);
        using var postOther = await SendAsync(client, HttpMethod.Post, DecisionUrl(OtherCase), reviewer, true, forged, ct);

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, postOther.StatusCode);
        Assert.DoesNotContain("decisions", await post.Content.ReadAsStringAsync(ct));
        Assert.Equal(1, ReadCase(path, HttpCase).Revision);
        Assert.Empty(TechnicalReviewPolicy.Decisions(ReadCase(path, HttpCase)));
        Assert.Empty(ReadCase(path, OtherCase).Audit);
    }

    [Fact]
    public async Task Endpoint_hides_out_of_scope_case_on_read_and_write_without_mutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "cases.json");
        SeedCases(path);
        using var factory = CreateFactory(path);
        using var client = factory.CreateClient();
        var reviewer = await SignInAsync(client, "reviewer", ct);

        using var post = await SendAsync(client, HttpMethod.Post, DecisionUrl(OtherCase), reviewer, true, AcceptBody(1, 1), ct);
        using var get = await SendAsync(client, HttpMethod.Get, DecisionUrl(OtherCase), reviewer, false, null, ct);

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.DoesNotContain("decisions", await get.Content.ReadAsStringAsync(ct));
        Assert.Empty(ReadCase(path, OtherCase).Audit);
    }

    [Fact]
    public async Task Endpoint_denies_presenter_stale_and_reasonless_commands_without_mutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "cases.json");
        SeedCases(path);
        using var factory = CreateFactory(path);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter", ct);
        var reviewer = await SignInAsync(client, "reviewer", ct);

        using var forbidden = await SendAsync(client, HttpMethod.Post, DecisionUrl(HttpCase), presenter, true, AcceptBody(1, 1), ct);
        using var stale = await SendAsync(client, HttpMethod.Post, DecisionUrl(HttpCase), reviewer, true, AcceptBody(0, 1), ct);
        using var noReason = await SendAsync(client, HttpMethod.Post, DecisionUrl(HttpCase), reviewer, true, AcceptBody(1, 1, reason: null), ct);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(1, ReadCase(path, HttpCase).Revision);
        Assert.Empty(TechnicalReviewPolicy.Decisions(ReadCase(path, HttpCase)));
    }

    [Fact]
    public async Task Decision_is_reconstructed_from_audit_and_marked_non_current_after_new_basis()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_dir, "cases.json");
        SeedCases(path);
        string traceId;
        using (var factory = CreateFactory(path))
        using (var client = factory.CreateClient())
        {
            var reviewer = await SignInAsync(client, "reviewer", ct);
            using var post = await SendAsync(client, HttpMethod.Post, DecisionUrl(HttpCase), reviewer, true, AcceptBody(1, 1), ct);
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            traceId = (await post.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("traceId").GetString()!;
        }

        var store = new CaseStateStore(path);
        var scope = store.AuthorizeCase(HttpCase, "presenter");
        var previousDigest = store.Read(scope).Basis[0].BasisDigest;
        store.Commit(scope, 2, new CaseCommit(CaseStateStore.CreateBasisVersion(2, "evidence v2", previousDigest), null, "presenter", "submit-evidence", "basis 2"));

        using var restarted = CreateFactory(path);
        using var client2 = restarted.CreateClient();
        var reviewer2 = await SignInAsync(client2, "reviewer", ct);
        using var get = await SendAsync(client2, HttpMethod.Get, DecisionUrl(HttpCase), reviewer2, false, null, ct);
        var text = await get.Content.ReadAsStringAsync(ct);
        var json = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.False(json.GetProperty("authorizesRequest").GetBoolean());
        Assert.Equal(2, json.GetProperty("basisVersion").GetInt32());
        var decision = json.GetProperty("decisions")[0];
        Assert.False(decision.GetProperty("current").GetBoolean());
        Assert.Equal(1, decision.GetProperty("basisVersion").GetInt32());
        Assert.Equal("Evidence supports return.", decision.GetProperty("reason").GetString());
        Assert.Equal(traceId, decision.GetProperty("traceId").GetString());
        Assert.DoesNotContain("evidence v1", text);
    }
}
