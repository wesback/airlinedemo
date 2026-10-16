using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.Reviews;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class MockResponseReassessmentTests : IDisposable
{
    private const string CaseId = "case-001";
    private const string ScenarioTime = "2026-10-14T00:00:00Z";
    private const string BasisId = "basis-7";
    private const string EvidenceText = "The engine record is missing its signed return-readiness certificate.";
    private const string ResponseText = "Synthetic return-readiness certificate reference CERT-0042.";
    private static readonly ProcessingCoverage Complete = new(1, 1, 0, 0);
    private sealed record Session(string Cookie, string Csrf);

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"mock-response-{Guid.NewGuid():N}.json");
        public bool FailPersist { get; set; }

        public Fixture(bool independentlySupportsResponse = false)
        {
            Cases = new CaseStateStore(_path, () =>
            {
                if (FailPersist) throw new IOException("injected case-state write failure");
            });
            Scope = Cases.CreateCase(CaseId, "presenter");
            var initial = CaseStateStore.CreateBasisVersion(1, "original evidence", string.Empty);
            Cases.Commit(Scope, 0, new CaseCommit(initial, null, "presenter", "submit-evidence", "basis 1"));
            Inbox = new MockRequestInbox();
            Assessments = new AssessmentStore();
            Basis = CurrentBasis();
            if (independentlySupportsResponse)
            {
                var documents = Basis.Documents.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                documents.Add("independent-response", BasisDocument.Create("independent-response", [ResponseText]));
                Basis = Basis with { Documents = documents };
            }

            Assessments.SetCurrentBasis(CaseId, Basis);
            Service = new MockResponseService(Cases, Inbox, Assessments, TimeProvider.System);
            FirstRequest = AddRequest(Inbox, Basis, "delivery-event-1", "request-report-1");
            Assessments.Append(new AssessmentRecord(
                CaseId, BasisId, 1, "Request", "eligible missing requirement", [], FirstRequest.RequestId, null));
        }
        public CaseStateStore Cases { get; }
        public CaseScope Scope { get; }
        public MockRequestInbox Inbox { get; }
        public AssessmentStore Assessments { get; }
        public AssessmentBasis Basis { get; }
        public MockResponseService Service { get; }
        public DeliveryReceipt FirstRequest { get; }
        public MockResponseCommand Command(string eventId = "response-event-1", string reportId = "response-report-1",
            string? requestId = null, string? basisId = null, int basisVersion = 1, string? scenarioTime = null,
            string body = ResponseText) =>
            new(eventId, reportId, requestId ?? FirstRequest.RequestId,
                basisId ?? BasisId, basisVersion, scenarioTime ?? ScenarioTime, body);
        public void Dispose()
        {
            foreach (var path in new[] { _path, _path + ".lock", _path + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("The mock response path must not send an HTTP request.");
        }
    }
    private readonly string _caseStatePath = Path.Combine(Path.GetTempPath(), $"mock-response-endpoint-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        foreach (var path in new[] { _caseStatePath, _caseStatePath + ".lock", _caseStatePath + ".tmp" })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
    private static AssessmentBasis CurrentBasis() =>
        new(BasisId, 1, ScenarioTime, Complete, false, new Dictionary<string, BasisDocument>
            { ["source-records"] = BasisDocument.Create("source-records", [EvidenceText]) });

    private static DeliveryReceipt AddRequest(MockRequestInbox inbox, AssessmentBasis basis, string eventId, string reportId)
    {
        var typedValue = "signed return-readiness certificate";
        var witness = new DocumentWitness("source-records", 1, EvidenceText);
        var claim = ClaimValidator.Validate(new InvestigationClaim(typedValue, [witness]), ClaimKind.MissingRequirement, basis.Documents, typedValue);
        var envelope = new DeliveryEnvelope(eventId, CaseId, reportId, basis.BasisId, basis.BasisVersion, basis.ScenarioTime,
            "Engine", "ENG-123", typedValue);
        var result = inbox.Submit(envelope, new DeliveryFinding(basis.Coverage, [claim], basis.IdentityAmbiguous, basis.BasisId, basis.BasisVersion),
            basis.ScenarioTime);
        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        return result.Receipt!;
    }
    private WebApplicationFactory<Program> CreateFactory(CountingHandler? outbound = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["Demo:Passcode"] = "test-passcode", ["CaseState:Path"] = _caseStatePath }));
            if (outbound is not null) builder.ConfigureTestServices(services => services.AddSingleton<HttpMessageHandler>(outbound));
        });
    private static async Task<Session> SignInAsync(HttpClient client, string userId = "presenter")
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = "test-passcode" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Session(cookie, body.GetProperty("csrfToken").GetString()!);
    }
    private static Task<HttpResponseMessage> PostResponseAsync(HttpClient client, Session? session, string caseId, MockResponseCommand command)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/cases/{caseId}/mock-responses")
        { Content = JsonContent.Create(command) };
        if (session is not null)
        {
            request.Headers.Add("Cookie", session.Cookie);
            request.Headers.Add("X-CSRF-Token", session.Csrf);
        }

        return client.SendAsync(request);
    }
    private static MockResponseCommand Response(DeliveryReceipt request, string eventId, string reportId, string basisId = BasisId) =>
        new(eventId, reportId, request.RequestId, basisId, 1, ScenarioTime, ResponseText);

    private static DeliveryReceipt Seed(IServiceProvider services, string caseId = CaseId)
    {
        var cases = services.GetRequiredService<ICaseStateStore>();
        var scope = cases.CreateCase(caseId, "presenter");
        cases.Commit(scope, 0, new CaseCommit(CaseStateStore.CreateBasisVersion(1, "original evidence", string.Empty),
            null, "presenter", "submit-evidence", "basis 1"));
        var basis = CurrentBasis();
        var assessments = services.GetRequiredService<AssessmentStore>();
        assessments.SetCurrentBasis(caseId, basis);
        var request = AddRequest(services.GetRequiredService<MockRequestInbox>(), basis, "delivery-event-1", "request-report-1");
        assessments.Append(new AssessmentRecord(caseId, BasisId, 1, "Request", "eligible missing requirement", [], request.RequestId, null));
        return request;
    }
    [Fact]
    public async Task Response_requires_authorized_current_request_and_basis()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var seeded = Seed(factory.Services);
        var session = await SignInAsync(client);

        using var unauthenticated = await PostResponseAsync(client, null, CaseId, Response(seeded, "event-unauth", "report-unauth"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var forgedCase = await PostResponseAsync(client, session, "case-forged", Response(seeded, "event-case", "report-case"));
        Assert.Equal(HttpStatusCode.NotFound, forgedCase.StatusCode);

        using var forgedRequest = await PostResponseAsync(client, session, CaseId,
            Response(seeded with { RequestId = "REQ-forged" }, "event-request", "report-request"));
        Assert.Equal(HttpStatusCode.NotFound, forgedRequest.StatusCode);
        Assert.DoesNotContain(seeded.RequestId, await forgedRequest.Content.ReadAsStringAsync());

        using var forgedBasis = await PostResponseAsync(client, session, CaseId, Response(seeded, "event-basis", "report-basis", "basis-forged"));
        Assert.Equal(HttpStatusCode.Conflict, forgedBasis.StatusCode);
        using var timeMismatch = await PostResponseAsync(client, session, CaseId,
            Response(seeded, "event-date", "report-date") with { ScenarioTime = "2026-01-01T00:00:00Z" });
        Assert.Equal(HttpStatusCode.BadRequest, timeMismatch.StatusCode);

        using var accepted = await PostResponseAsync(client, session, CaseId, Response(seeded, "event-valid", "report-valid"));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var cases = factory.Services.GetRequiredService<ICaseStateStore>();
        var state = cases.Read(cases.AuthorizeCase(CaseId, "presenter"));
        Assert.Equal((2, 3, 1), (state.Basis.Count, state.Revision, state.ReceiptIds.Count));
    }
    [Fact]
    public async Task Replay_conflict_date_race_and_fault_are_atomic()
    {
        using (var fixture = new Fixture())
        {
            var command = fixture.Command();
            var accepted = fixture.Service.Accept(fixture.Scope, "presenter", command);
            var replay = fixture.Service.Accept(fixture.Scope, "presenter", command);
            var changed = fixture.Service.Accept(fixture.Scope, "presenter", command with { Body = "changed response body" });
            var changedEnvelope = fixture.Service.Accept(fixture.Scope, "presenter",
                command with { ReportId = "different-response-report" });
            var reusedRequest = fixture.Service.Accept(fixture.Scope, "presenter",
                fixture.Command(eventId: "response-event-3", reportId: "response-report-3"));
            var reusedReport = fixture.Service.Accept(fixture.Scope, "presenter",
                fixture.Command(eventId: "response-event-2", reportId: "response-report-1",
                    requestId: AddRequest(fixture.Inbox, fixture.Basis, "delivery-event-2", "request-report-2").RequestId));

            Assert.Equal(MockResponseOutcome.Accepted, accepted.Outcome);
            Assert.Equal(MockResponseOutcome.Replayed, replay.Outcome);
            Assert.Equal(accepted.Receipt, replay.Receipt);
            Assert.Equal(MockResponseOutcome.Conflict, changed.Outcome);
            Assert.Equal(MockResponseOutcome.Conflict, changedEnvelope.Outcome);
            Assert.Equal(MockResponseOutcome.Conflict, reusedRequest.Outcome);
            Assert.Equal(MockResponseOutcome.Conflict, reusedReport.Outcome);
            Assert.Equal(2, fixture.Cases.Read(fixture.Scope).Basis.Count);
            Assert.Single(fixture.Cases.Read(fixture.Scope).ReceiptIds);
            Assert.Single(fixture.Assessments.List(CaseId), item => item.BasisVersion == 2);
        }
        using (var fixture = new Fixture())
        {
            var before = fixture.Cases.Read(fixture.Scope);
            var mismatch = fixture.Service.Accept(fixture.Scope, "presenter",
                fixture.Command(eventId: "date-event", scenarioTime: "2026-01-01T00:00:00Z"));
            var after = fixture.Cases.Read(fixture.Scope);
            Assert.Equal(MockResponseOutcome.ScenarioTimeMismatch, mismatch.Outcome);
            Assert.Equal(before, after);
        }
        using (var fixture = new Fixture())
        {
            var command = fixture.Command();
            var results = await Task.WhenAll(Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => fixture.Service.Accept(fixture.Scope, "presenter", command))));
            Assert.Single(results, result => result.Outcome == MockResponseOutcome.Accepted);
            Assert.Equal(31, results.Count(result => result.Outcome == MockResponseOutcome.Replayed));
            Assert.Single(fixture.Cases.Read(fixture.Scope).Audit, audit => audit.Action == MockResponseService.AcceptedAction);
        }
        using (var fixture = new Fixture())
        {
            var before = fixture.Cases.Read(fixture.Scope);
            var assessmentsBefore = fixture.Assessments.List(CaseId);
            fixture.FailPersist = true;
            Assert.Throws<IOException>(() =>
                fixture.Service.Accept(fixture.Scope, "presenter", fixture.Command()));
            var after = fixture.Cases.Read(fixture.Scope);
            Assert.Equal(before.Revision, after.Revision);
            Assert.Equal(before.Basis, after.Basis);
            Assert.Equal(before.ReceiptIds, after.ReceiptIds);
            Assert.Equal(before.Audit, after.Audit);
            Assert.Equal(assessmentsBefore, fixture.Assessments.List(CaseId));
            Assert.Single(fixture.Inbox.Requests);
        }
    }
    [Fact]
    public void New_response_basis_preserves_history_and_suppresses_stale_actions()
    {
        using var fixture = new Fixture();
        var firstBasis = fixture.Cases.Read(fixture.Scope).Basis[0];
        var decision = TechnicalReviewPolicy.Decide(fixture.Cases, [CaseId], CaseEndpoints.ReviewerRole,
            "reviewer", CaseId, new TechnicalReviewCommand(1, 1, "accept", "Evidence supports return."), "trace-1");
        Assert.Equal(TechnicalReviewOutcome.Recorded, decision.Outcome);
        var assessmentsBefore = fixture.Assessments.List(CaseId);

        var result = fixture.Service.Accept(fixture.Scope, "presenter", fixture.Command());

        Assert.Equal(MockResponseOutcome.Accepted, result.Outcome);
        Assert.Equal(2, result.Receipt!.BasisVersion);
        var state = fixture.Cases.Read(fixture.Scope);
        Assert.Equal(firstBasis, state.Basis[0]);
        Assert.Equal(2, state.Basis.Count);
        Assert.NotEqual(state.Basis[0].BasisDigest, state.Basis[1].BasisDigest);
        Assert.Equal(4, state.Audit.Count);
        Assert.Equal(assessmentsBefore, fixture.Assessments.List(CaseId).Take(assessmentsBefore.Count));
        var reassessment = fixture.Assessments.List(CaseId)[^1];
        Assert.Equal("Review", reassessment.Outcome);
        Assert.Equal("no claim", reassessment.Reason);
        Assert.Empty(reassessment.Claims);
        var reassessedBasis = fixture.Assessments.CurrentBasis(CaseId)!;
        Assert.Equal(2, reassessedBasis.BasisVersion);
        Assert.Equal(ResponseText, reassessedBasis.Documents[result.Receipt!.DocumentId].Pages[0]);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, BasisDocument>)reassessedBasis.Documents).Add("mutated", BasisDocument.Create("mutated", ["Injected"])));
        Assert.Single(fixture.Inbox.Requests);
        Assert.False(TechnicalReviewPolicy.Decisions(state).Single().Current);
        Assert.False(TechnicalReviewPolicy.AuthorizesRequest(state));
        var workItem = Assert.Single(state.WorkItems!);
        Assert.Equal("records-investigation", workItem.CapabilityId);
        Assert.Equal("open", workItem.State);
    }
    [Fact]
    public void Response_is_supported_only_by_independently_established_evidence()
    {
        using var fixture = new Fixture(independentlySupportsResponse: true);

        var result = fixture.Service.Accept(fixture.Scope, "presenter", fixture.Command());

        Assert.Equal(MockResponseOutcome.Accepted, result.Outcome);
        var reassessment = fixture.Assessments.List(CaseId)[^1];
        Assert.Equal("Baseline", reassessment.Outcome);
        Assert.Equal("supported baseline", reassessment.Reason);
        var claim = Assert.Single(reassessment.Claims);
        Assert.Equal("Baseline", claim.Kind);
        Assert.Equal(ResponseText, claim.Value);
        var witness = Assert.Single(claim.Witnesses);
        Assert.Equal("independent-response", witness.DocumentId);
        Assert.Equal(1, witness.Page);
        Assert.NotEqual(result.Receipt!.DocumentId, witness.DocumentId);
    }
    [Fact]
    public void Prior_unverified_response_cannot_support_a_later_response()
    {
        using var fixture = new Fixture();
        var first = fixture.Service.Accept(fixture.Scope, "presenter", fixture.Command());
        Assert.Equal(MockResponseOutcome.Accepted, first.Outcome);

        var basis = fixture.Assessments.CurrentBasis(CaseId)!;
        var request = AddRequest(fixture.Inbox, basis, "delivery-event-2", "request-report-2");
        var second = fixture.Service.Accept(fixture.Scope, "presenter",
            new MockResponseCommand("response-event-2", "response-report-2", request.RequestId,
                basis.BasisId, basis.BasisVersion, basis.ScenarioTime, ResponseText));

        Assert.Equal(MockResponseOutcome.Accepted, second.Outcome);
        var reassessment = fixture.Assessments.List(CaseId)[^1];
        Assert.Equal("Review", reassessment.Outcome);
        Assert.Equal("no claim", reassessment.Reason);
        Assert.Empty(reassessment.Claims);
        Assert.Equal(3, fixture.Assessments.CurrentBasis(CaseId)!.BasisVersion);
    }
    [Fact]
    public async Task Response_does_not_close_request_or_dispatch_externally()
    {
        var outbound = new CountingHandler();
        using var factory = CreateFactory(outbound);
        using var client = factory.CreateClient();
        var seeded = Seed(factory.Services);
        var session = await SignInAsync(client);

        using var response = await PostResponseAsync(client, session, CaseId, Response(seeded, "event-response", "report-response"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var request = Assert.Single(factory.Services.GetRequiredService<MockRequestInbox>().Requests);
        Assert.Equal(seeded, request);
        Assert.Equal(0, outbound.Calls);
        var cases = factory.Services.GetRequiredService<ICaseStateStore>();
        var scope = cases.AuthorizeCase(CaseId, "presenter");
        Assert.Single(cases.Read(scope).Audit, audit => audit.Action == MockResponseService.AcceptedAction);
        var inbox = factory.Services.GetRequiredService<MockRequestInbox>();
        var assessments = factory.Services.GetRequiredService<AssessmentStore>();
        var priorAssessment = Assert.Single(assessments.List(CaseId), item => item.BasisVersion == 2);
        Assert.Equal("Review", priorAssessment.Outcome);
        Assert.Equal("no claim", priorAssessment.Reason);
        Assert.Empty(priorAssessment.Claims);
        var basis = assessments.CurrentBasis(CaseId)!;
        var repeated = new AssessmentExecutionService(cases, inbox, assessments).Assess(
            scope,
            basis,
            []);
        Assert.Equal(("Review", "no claim", 2), (repeated.Outcome, repeated.Reason, repeated.BasisVersion));
        Assert.Single(inbox.Requests);
    }
}
