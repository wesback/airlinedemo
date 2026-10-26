using System.Net;
using System.Net.Http.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class DemoCaseSeederTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "demo-case-seeder-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string PathFor(string name = "cases.json") => Path.Combine(_dir, name);

    private static IConfiguration Configuration(string? value)
    {
        var values = new Dictionary<string, string?>();
        if (value is not null) values["Demo:SeedCases"] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void Seeds_the_five_demo_cases_owned_by_presenter_when_enabled()
    {
        var inner = new CaseStateStore(PathFor());
        var store = new RecordingStore(inner);
        DemoCaseSeeder.Seed(Configuration("true"), store);

        Assert.Equal(["case-001", "case-002", "cp-case-a", "cp-case-b", "cp-case-c"], store.CreatedCaseIds);
        Assert.Equal("presenter", store.OwnerOf("case-001"));
        Assert.Equal("presenter", store.OwnerOf("case-002"));
        Assert.Equal("presenter", store.OwnerOf("cp-case-a"));
        Assert.Equal("presenter", store.OwnerOf("cp-case-b"));
        Assert.Equal("presenter", store.OwnerOf("cp-case-c"));
        Assert.Null(store.OwnerOf("case-003"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    public void Seeding_runs_only_when_the_setting_parses_as_true(string? value, bool created)
    {
        var store = new CaseStateStore(PathFor());
        DemoCaseSeeder.Seed(Configuration(value), store);
        Assert.Equal(created ? "presenter" : null, store.OwnerOf("case-001"));
        if (!created) Assert.Null(store.OwnerOf("cp-case-a"));
    }

    [Fact]
    public void Seeding_leaves_existing_and_race_created_cases_unchanged()
    {
        var path = PathFor();
        var inner = new CaseStateStore(path);
        var existingScope = inner.CreateCase("case-001", "original-owner");
        var basis = CaseStateStore.CreateBasisVersion(1, "existing basis", string.Empty);
        inner.Commit(existingScope, 0, new CaseCommit(basis, "existing-receipt", "reviewer", "submit", "existing audit"));

        var existingBefore = inner.Read(existingScope);
        var race = new RaceCreatingStore(inner, "cp-case-a", "race-owner", "race basis");
        DemoCaseSeeder.Seed(Configuration("true"), race);

        var raceScope = inner.AuthorizeCase("cp-case-a", "race-owner");
        var raceBefore = race.RaceStateBeforeDuplicate ?? throw new InvalidOperationException("Race case state was not captured.");
        DemoCaseSeeder.Seed(Configuration("true"), race);

        var existingAfter = inner.Read(inner.AuthorizeCase("case-001", "original-owner"));
        Assert.Equal(existingBefore.Owner, existingAfter.Owner);
        Assert.Equal(existingBefore.Revision, existingAfter.Revision);
        Assert.Equal(existingBefore.Basis, existingAfter.Basis);
        Assert.Equal(existingBefore.ReceiptIds, existingAfter.ReceiptIds);
        Assert.Equal(existingBefore.Audit, existingAfter.Audit);
        Assert.Equal(existingBefore.ReceiptFingerprints, existingAfter.ReceiptFingerprints);
        var raceAfter = inner.Read(raceScope);
        Assert.Equal(raceBefore.Owner, raceAfter.Owner);
        Assert.Equal(raceBefore.Revision, raceAfter.Revision);
        Assert.Equal(raceBefore.Basis, raceAfter.Basis);
        Assert.Equal(raceBefore.ReceiptIds, raceAfter.ReceiptIds);
        Assert.Equal(raceBefore.Audit, raceAfter.Audit);
        Assert.Equal(raceBefore.ReceiptFingerprints, raceAfter.ReceiptFingerprints);
        Assert.Equal("race-owner", raceAfter.Owner);
        Assert.Equal(1, raceAfter.Revision);
        Assert.Single(raceAfter.Basis);
        Assert.Equal(["race-receipt"], raceAfter.ReceiptIds);
        Assert.Single(raceAfter.Audit);
        Assert.Equal("presenter", inner.OwnerOf("case-002"));
        Assert.Equal("presenter", inner.OwnerOf("cp-case-b"));
        Assert.Equal("presenter", inner.OwnerOf("cp-case-c"));
    }

    [Fact]
    public void Seeding_creates_empty_cases_without_basis_evidence_or_audit()
    {
        var store = new CaseStateStore(PathFor());
        DemoCaseSeeder.Seed(Configuration("true"), store);

        foreach (var caseId in new[] { "case-001", "case-002", "cp-case-a", "cp-case-b", "cp-case-c" })
        {
            var state = store.Read(store.AuthorizeCase(caseId, "presenter"));
            Assert.Equal(0, state.Revision);
            Assert.Empty(state.Basis);
            Assert.Empty(state.ReceiptIds);
            Assert.Empty(state.Audit);
        }

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:SeedCases"] = "true",
                ["Demo:Passcode"] = "test-passcode",
                ["Persistence:CaseStateProvider"] = "File",
                ["CaseState:Path"] = PathFor("host-cases.json"),
            })));
        var hostStore = factory.Services.GetRequiredService<ICaseStateStore>();
        var packages = factory.Services.GetRequiredService<EvidencePackageStore>();
        foreach (var caseId in new[] { "case-001", "case-002", "cp-case-a", "cp-case-b", "cp-case-c" })
        {
            var state = hostStore.Read(hostStore.AuthorizeCase(caseId, "presenter"));
            Assert.Equal(caseId, state.CaseId);
            Assert.Equal("presenter", state.Owner);
            Assert.Equal(0, state.Revision);
            Assert.Empty(state.Basis);
            Assert.Empty(state.ReceiptIds);
            Assert.Empty(state.Audit);
            Assert.Empty(packages.ListForCase(caseId));
        }
    }

    [Fact]
    public async Task Executive_reads_case_a_through_the_host_when_seeding_is_enabled()
    {
        var passcode = "test-passcode";
        var path = PathFor("executive-cases.json");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = passcode,
                ["Persistence:CaseStateProvider"] = "File",
                ["CaseState:Path"] = path,
                ["Demo:SeedCases"] = "true",
            })));
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var passcodeResponse = await client.PostAsJsonAsync("/api/auth/passcode", new { passcode }, ct);
        Assert.Equal(HttpStatusCode.NoContent, passcodeResponse.StatusCode);
        using var signIn = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId = "exec-1", passcode }, ct);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        var sessionCookie = signIn.Headers.GetValues("Set-Cookie")
            .First(cookie => cookie.StartsWith(DemoAuthEndpoints.CookieName + "=", StringComparison.Ordinal))
            .Split(';')[0];

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/cases/cp-case-a");
        allowedRequest.Headers.Add("Cookie", sessionCookie);
        using var allowed = await client.SendAsync(allowedRequest, ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/cases/case-001");
        deniedRequest.Headers.Add("Cookie", sessionCookie);
        using var denied = await client.SendAsync(deniedRequest, ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    private sealed class RecordingStore(ICaseStateStore inner) : ICaseStateStore
    {
        public List<string> CreatedCaseIds { get; } = [];

        public string? OwnerOf(string caseId) => inner.OwnerOf(caseId);
        public CaseScope AuthorizeCase(string caseId, string owner) => inner.AuthorizeCase(caseId, owner);
        public CaseScope CreateCase(string caseId, string owner)
        {
            CreatedCaseIds.Add(caseId);
            return inner.CreateCase(caseId, owner);
        }
        public CaseState Read(CaseScope scope) => inner.Read(scope);
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => inner.ReadAudit(scope);
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit) => inner.Commit(scope, expectedRevision, commit);
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => inner.ListWorkItems(scope);
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) => inner.CreateWorkItem(scope, expectedRevision, draft, actor);
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) => inner.ChangeWorkItemState(scope, expectedRevision, itemId, state, actor);
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => inner.AppendTrace(scope, trace);
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) => inner.FindTrace(scope, caseId, runId, investigationId);
    }

    private sealed class RaceCreatingStore(ICaseStateStore inner, string raceCaseId, string raceOwner, string basisContent) : ICaseStateStore
    {
        public CaseState? RaceStateBeforeDuplicate { get; private set; }

        public string? OwnerOf(string caseId)
        {
            var owner = inner.OwnerOf(caseId);
            if (caseId == raceCaseId && owner is null)
            {
                var scope = inner.CreateCase(raceCaseId, raceOwner);
                var basis = CaseStateStore.CreateBasisVersion(1, basisContent, string.Empty);
                inner.Commit(scope, 0, new CaseCommit(basis, "race-receipt", "race-creator", "seed-race", "race audit"));
                RaceStateBeforeDuplicate = inner.Read(scope);
                return null;
            }
            return owner;
        }

        public CaseScope AuthorizeCase(string caseId, string owner) => inner.AuthorizeCase(caseId, owner);
        public CaseScope CreateCase(string caseId, string owner) => inner.CreateCase(caseId, owner);
        public CaseState Read(CaseScope scope) => inner.Read(scope);
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => inner.ReadAudit(scope);
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit) => inner.Commit(scope, expectedRevision, commit);
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => inner.ListWorkItems(scope);
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) => inner.CreateWorkItem(scope, expectedRevision, draft, actor);
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) => inner.ChangeWorkItemState(scope, expectedRevision, itemId, state, actor);
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => inner.AppendTrace(scope, trace);
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) => inner.FindTrace(scope, caseId, runId, investigationId);
    }
}
