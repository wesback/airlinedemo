using System.Net;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class ReadinessSummaryTests
{
    private const string CaseId = "case-001";

    [Fact]
    public async Task Counts_come_from_case_state_and_ambiguous_items_are_not_gaps()
    {
        using var harness = new Harness();
        harness.SeedHeadline();

        using var response = await harness.Get($"/api/cases/{CaseId}/readiness?role=finance-reviewer&caseId=case-002", CaseId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var summary = json.RootElement;
        Assert.Equal(1, summary.GetProperty("gapsFound").GetInt32());
        Assert.Equal(1, summary.GetProperty("requestsSent").GetInt32());
        Assert.Equal(1, summary.GetProperty("heldForReview").GetInt32());
        Assert.Equal(2, summary.GetProperty("notProcessed").GetInt32());
    }

    [Fact]
    public async Task Out_of_scope_requests_return_no_summary()
    {
        using var harness = new Harness();

        using var unauthenticated = await harness.Client.GetAsync(
            $"/api/cases/{CaseId}/readiness", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.DoesNotContain("sentence", await unauthenticated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);

        using var outOfScope = await harness.Get("/api/cases/case-002/readiness", CaseId);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        Assert.DoesNotContain("sentence", await outOfScope.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Headline_case_summary_matches_the_story()
    {
        using var harness = new Harness();
        harness.SeedHeadline();
        using var response = await harness.Get($"/api/cases/{CaseId}/readiness", CaseId);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var summary = json.RootElement;

        Assert.Equal(9, summary.GetProperty("monthsUntilReturn").GetInt32());
        Assert.Equal(1, summary.GetProperty("gapsFound").GetInt32());
        Assert.Equal(1, summary.GetProperty("requestsSent").GetInt32());
        Assert.Equal(1, summary.GetProperty("heldForReview").GetInt32());
        Assert.Equal("Return in 9 months. 1 records gap found, 1 routine request sent, 1 item held for internal review, 2 documents not processed.",
            summary.GetProperty("sentence").GetString());
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "readiness-summary-" + Guid.NewGuid().ToString("N"));
        public readonly CaseStateStore Store;
        public readonly WebApplicationFactory<Program> Factory;
        public readonly HttpClient Client;
        public EvidencePackageStore Packages => Factory.Services.GetRequiredService<EvidencePackageStore>();

        public Harness()
        {
            Store = new CaseStateStore(Path.Combine(_directory, "cases.json"));
            Store.CreateCase(CaseId, "case-owner");
            Store.CreateCase("case-002", "case-owner");
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["CaseState:Path"] = Path.Combine(_directory, "host-cases.json") }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ICaseStateStore>();
                    services.AddSingleton<ICaseStateStore>(Store);
                });
            });
            Client = Factory.CreateClient();
        }

        public void SeedHeadline()
        {
            var scope = Store.AuthorizeCase(CaseId, "case-owner");
            var receipt = new DeliveryReceipt("event-1", "request-1", CaseId, "report-1", "basis-1", 1, "1", "mock request");
            var requestAssessment = new CaseAssessment("assessment-gap", CaseId, "basis-1", 1, "Request", "validated missing history",
                [Claim("MissingRequirement")], receipt.RequestId, null);
            Store.Commit(scope, 0, new CaseCommit(null, receipt.EventId, "assessment-service", "assessment-delivered", "delivered",
                Assessment: requestAssessment, RequestReceipt: receipt));

            var ambiguousAssessment = new CaseAssessment("assessment-ambiguous", CaseId, "basis-1", 1, "Review", "ambiguous identity",
                [Claim("MissingRequirement")], null, "review-item");
            Store.Commit(scope, 1, new CaseCommit(null, null, "assessment-service", "assessment", "held for review",
                Assessment: ambiguousAssessment));
            Store.CreateWorkItem(scope, 2, new WorkItemDraft("review-item", "records-investigation", "open", CaseEndpoints.ReviewerRole), "assessment-service");
            Packages.Submit("package-1", CaseId, () => new EvidencePackageOutcome("partial",
            [
                new EvidenceDocumentResult("accepted.pdf", "accepted", "accepted", 1, "digest"),
                new EvidenceDocumentResult("failed.pdf", "rejected-retryable", "failed processing", null, null),
                new EvidenceDocumentResult("partial.pdf", "rejected-permanent", "unprocessed", null, null),
            ]));
        }

        public Task<HttpResponseMessage> Get(string path, string caseScope)
        {
            var session = Factory.Services.GetRequiredService<DemoSessionStore>()
                .Create(new DemoUser("presenter-1", "Test presenter", EvidenceEndpoints.PresenterRole, [caseScope]));
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");
            return Client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        private static AssessedClaim Claim(string kind) => new(kind, "back-to-birth history", "Engine", "ENG-123", []);
    }
}
