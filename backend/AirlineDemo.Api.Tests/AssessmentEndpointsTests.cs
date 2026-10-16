using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class AssessmentEndpointsTests
{
    private const string Passcode = "test-passcode";
    private const string CaseId = "case-001";
    private const string OtherCaseId = "case-002";
    private const string Owner = "demo-owner";
    private const string Value = "pending reserve approval";
    private const string QuoteSentinel = "QUOTE-SENTINEL-77";
    private const string DocumentSentinel = "DOCUMENT-TEXT-SENTINEL-55";
    private const string PromptSentinel = "PROMPT-SENTINEL-99";
    private const string ScenarioTime = "2026-10-14T00:00:00Z";
    private const string AssessmentPath = $"/api/cases/{CaseId}/assessments";
    private const string RequestsPath = $"/api/cases/{CaseId}/requests";
    private const string Quote = QuoteSentinel + " " + Value;
    private static readonly ProcessingCoverage Complete = new(2, 2, 0, 0);

    private sealed record Session(string Cookie, string Csrf);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"assessment-endpoints-{Guid.NewGuid():N}.json");
        private readonly CaseStateStore _caseStore;
        public readonly WebApplicationFactory<Program> Factory;
        public readonly HttpClient Client;
        public AssessmentStore Assessments => Factory.Services.GetRequiredService<AssessmentStore>();
        public MockRequestInbox Inbox => Factory.Services.GetRequiredService<MockRequestInbox>();
        public AssessmentExecutionService Execution => Factory.Services.GetRequiredService<AssessmentExecutionService>();
        public ICaseStateStore Cases => Factory.Services.GetRequiredService<ICaseStateStore>();

        public Harness()
        {
            _caseStore = new CaseStateStore(_statePath);
            _caseStore.CreateCase(CaseId, Owner);
            _caseStore.CreateCase(OtherCaseId, Owner);
            var stateStore = _caseStore;
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Passcode"] = Passcode }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ICaseStateStore>();
                    services.AddSingleton<ICaseStateStore>(stateStore);
                });
            });
            Client = Factory.CreateClient();
            Assessments.SetCurrentBasis(CaseId, Basis());
            Assessments.SetCurrentBasis(OtherCaseId, Basis());
        }

        public AssessmentBasis Basis() => new(
            "basis-current",
            7,
            ScenarioTime,
            Complete,
            false,
            new Dictionary<string, BasisDocument>
            {
                ["doc-17"] = BasisDocument.Create("doc-17", [Quote, DocumentSentinel + " " + PromptSentinel]),
            });

        public AssessmentClaim Claim() =>
            new(ClaimKind.MissingRequirement, Value, "Engine", "ENG-123", [new DocumentWitness("doc-17", 1, Quote)]);

        public CaseScope Scope(string caseId = CaseId) => Cases.AuthorizeCase(caseId, Owner);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            if (File.Exists(_statePath)) File.Delete(_statePath);
        }
    }

    private static async Task<Session> SignInAsync(HttpClient client, string userId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await client.PostAsJsonAsync(
            "/api/auth/sign-in", new { userId, passcode = Passcode }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return new Session(cookie, body.RootElement.GetProperty("csrfToken").GetString()!);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Session? session, bool withToken = true, string? token = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post) request.Content = JsonContent.Create(new
        {
            claims = new[]
            {
                new
                {
                    kind = "MissingRequirement",
                    value = Value,
                    component = "Engine",
                    serialNumber = "ENG-123",
                    witnesses = new[] { new { documentId = "doc-17", page = 1, quote = Quote } },
                },
            },
        });
        if (session is not null)
        {
            request.Headers.Add("Cookie", session.Cookie);
            if (withToken) request.Headers.Add("X-CSRF-Token", token ?? session.Csrf);
        }

        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, Session? session = null, bool withToken = true, string? token = null)
    {
        using var request = Request(method, path, session, withToken, token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static DeliveryReceipt SeedReceipt(Harness h, string caseId, string reportId, string basisId, int version)
    {
        var basis = h.Assessments.CurrentBasis(caseId)!;
        var validated = ClaimValidator.Validate(
            new InvestigationClaim(Value, [new DocumentWitness("doc-17", 1, Quote)]),
            ClaimKind.MissingRequirement,
            basis.Documents,
            Value);
        var envelope = new DeliveryEnvelope(
            $"event-{caseId}-{reportId}", caseId, reportId, basisId, version, ScenarioTime, "Engine", "ENG-123", Value);
        var finding = new DeliveryFinding(Complete, [validated], false, basisId, version);
        var result = h.Inbox.Submit(envelope, finding, ScenarioTime);
        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        var receipt = result.Receipt!;
        var scope = h.Scope(caseId);
        var state = h.Cases.Read(scope);
        h.Cases.Commit(scope, state.Revision, new CaseCommit(
            null, receipt.RequestId, "test", "seed-request", reportId, RequestReceipt: receipt));
        return receipt;
    }

    [Fact]
    public async Task Post_assessment_returns_current_typed_result_and_get_returns_same_record()
    {
        await using var h = new Harness();
        var presenter = await SignInAsync(h.Client, "presenter");
        using var post = await SendAsync(h.Client, HttpMethod.Post, AssessmentPath, presenter);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var postBody = await post.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var postJson = JsonDocument.Parse(postBody);
        var assessment = postJson.RootElement;
        var id = assessment.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal("Request", assessment.GetProperty("outcome").GetString());
        Assert.Equal(CaseId, assessment.GetProperty("caseId").GetString());
        Assert.Equal("basis-current", assessment.GetProperty("basisId").GetString());
        Assert.Equal(7, assessment.GetProperty("basisVersion").GetInt32());
        var claim = Assert.Single(assessment.GetProperty("claims").EnumerateArray());
        Assert.Equal("MissingRequirement", claim.GetProperty("kind").GetString());
        Assert.Equal(Value, claim.GetProperty("value").GetString());
        Assert.Equal("Engine", claim.GetProperty("component").GetString());
        Assert.Equal("ENG-123", claim.GetProperty("serialNumber").GetString());
        var witness = Assert.Single(claim.GetProperty("witnesses").EnumerateArray());
        Assert.Equal("doc-17", witness.GetProperty("documentId").GetString());
        Assert.Equal(1, witness.GetProperty("page").GetInt32());
        var receipt = Assert.Single(h.Cases.Read(h.Scope()).RequestReceipts!);
        Assert.Equal(receipt.RequestId, assessment.GetProperty("requestId").GetString());
        var stored = Assert.Single(h.Cases.Read(h.Scope()).Assessments!);
        Assert.Equal(receipt.RequestId, stored.RequestId);
        Assert.Equal("Request", stored.Outcome);

        using var get = await SendAsync(h.Client, HttpMethod.Get, $"{AssessmentPath}/{id}", presenter);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var getBody = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(postBody, getBody);

        using var missing = await SendAsync(h.Client, HttpMethod.Get, $"{AssessmentPath}/missing-assessment", presenter);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        foreach (var sentinel in new[] { QuoteSentinel, DocumentSentinel, PromptSentinel })
        {
            Assert.DoesNotContain(sentinel, postBody, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, getBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Request_list_is_case_scoped_and_returns_exact_delivery_state()
    {
        await using var h = new Harness();
        var first = SeedReceipt(h, CaseId, "report-first", "basis-first", 3);
        var other = SeedReceipt(h, OtherCaseId, "report-other", "basis-other", 4);
        var reviewer = await SignInAsync(h.Client, "reviewer");

        using var response = await SendAsync(h.Client, HttpMethod.Get, RequestsPath, reviewer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(responseBody);
        var item = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(first.RequestId, item.GetProperty("requestId").GetString());
        Assert.Equal(CaseId, item.GetProperty("caseId").GetString());
        Assert.Equal("report-first", item.GetProperty("reportId").GetString());
        Assert.Equal("basis-first", item.GetProperty("basisId").GetString());
        Assert.Equal(3, item.GetProperty("basisVersion").GetInt32());
        Assert.Equal(MockRequestTemplate.Version, item.GetProperty("templateVersion").GetString());
        Assert.Equal(first.Body, item.GetProperty("body").GetString());
        Assert.Equal("delivered", item.GetProperty("state").GetString());
        foreach (var sentinel in new[] { QuoteSentinel, DocumentSentinel, PromptSentinel })
            Assert.DoesNotContain(sentinel, responseBody, StringComparison.Ordinal);

        using var outOfScope = await SendAsync(h.Client, HttpMethod.Get, $"/api/cases/{OtherCaseId}/requests", reviewer);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        var deniedBody = await outOfScope.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(other.RequestId, deniedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(other.Body, deniedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assessment_routes_require_session_role_scope_and_anti_forgery()
    {
        await using var h = new Harness();
        var seeded = h.Execution.Assess(h.Scope(), h.Assessments.CurrentBasis(CaseId)!, [h.Claim()]);
        var seededId = seeded.AssessmentId;
        var receipt = Assert.Single(h.Cases.Read(h.Scope()).RequestReceipts!);
        var presenter = await SignInAsync(h.Client, "presenter");
        var reviewer = await SignInAsync(h.Client, "reviewer");
        var finance = await SignInAsync(h.Client, "fin-1");
        var legal = await SignInAsync(h.Client, "legal-1");
        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Post, AssessmentPath),
            (HttpMethod.Get, $"{AssessmentPath}/{seededId}"),
            (HttpMethod.Get, RequestsPath),
        };

        foreach (var (method, path) in routes)
        {
            await AssertDeniedAndUnchanged(h, method, path, null, HttpStatusCode.Unauthorized, seededId);
            foreach (var deniedRole in new[] { finance, legal })
                await AssertDeniedAndUnchanged(h, method, path, deniedRole, HttpStatusCode.Forbidden, seededId);
            await AssertDeniedAndUnchanged(h, method, path, reviewer, HttpStatusCode.Forbidden, seededId, withToken: false);
            await AssertDeniedAndUnchanged(h, method, path, reviewer, HttpStatusCode.Forbidden, seededId, token: "invalid-token");

            var outOfScopePath = path.Replace(CaseId, OtherCaseId, StringComparison.Ordinal);
            await AssertDeniedAndUnchanged(h, method, outOfScopePath, reviewer, HttpStatusCode.NotFound, seededId);
        }

        using var presenterAllowed = await SendAsync(h.Client, HttpMethod.Get, RequestsPath, presenter);
        Assert.Equal(HttpStatusCode.OK, presenterAllowed.StatusCode);
    }

    private static async Task AssertDeniedAndUnchanged(
        Harness h,
        HttpMethod method,
        string path,
        Session? session,
        HttpStatusCode expected,
        string protectedAssessmentId,
        bool withToken = true,
        string? token = null)
    {
        var scope = h.Scope();
        var otherScope = h.Scope(OtherCaseId);
        var assessments = JsonSerializer.Serialize(h.Cases.Read(scope).Assessments);
        var otherAssessments = JsonSerializer.Serialize(h.Cases.Read(otherScope).Assessments);
        var receipts = JsonSerializer.Serialize(h.Cases.Read(scope).RequestReceipts);
        var otherReceipts = JsonSerializer.Serialize(h.Cases.Read(otherScope).RequestReceipts);
        var workItems = JsonSerializer.Serialize(h.Cases.Read(scope).WorkItems);
        var otherWorkItems = JsonSerializer.Serialize(h.Cases.Read(otherScope).WorkItems);
        using var response = await SendAsync(h.Client, method, path, session, withToken, token);
        Assert.Equal(expected, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Value, responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(protectedAssessmentId, responseBody, StringComparison.Ordinal);
        foreach (var receipt in h.Cases.Read(scope).RequestReceipts!)
        {
            Assert.DoesNotContain(receipt.RequestId, responseBody, StringComparison.Ordinal);
            Assert.DoesNotContain(receipt.Body, responseBody, StringComparison.Ordinal);
        }
        Assert.Equal(assessments, JsonSerializer.Serialize(h.Cases.Read(scope).Assessments));
        Assert.Equal(otherAssessments, JsonSerializer.Serialize(h.Cases.Read(otherScope).Assessments));
        Assert.Equal(receipts, JsonSerializer.Serialize(h.Cases.Read(scope).RequestReceipts));
        Assert.Equal(otherReceipts, JsonSerializer.Serialize(h.Cases.Read(otherScope).RequestReceipts));
        Assert.Equal(workItems, JsonSerializer.Serialize(h.Cases.Read(scope).WorkItems));
        Assert.Equal(otherWorkItems, JsonSerializer.Serialize(h.Cases.Read(otherScope).WorkItems));
    }
}
