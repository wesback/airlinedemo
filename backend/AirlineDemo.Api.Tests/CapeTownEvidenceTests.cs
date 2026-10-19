using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class CapeTownEvidenceTests
{
    private const string RequirementId = "REQ-CAPE-TOWN";
    private const string Quote = "The Cape Town registration reference needs counsel review.";
    private static readonly string[] Sources = ["technical", "maintenance", "finance", "records", "contracts"];

    [Fact]
    public void PayloadContractAcceptsExactValuesAndRejectsEveryInvalidCase()
    {
        foreach (var idera in new[] { "present", "absent", "not_recorded" })
        foreach (var declarationNoted in new[] { true, false })
        {
            var payload = Payload(idera, declarationNoted);
            var record = Assert.Single(ParseFixture(JsonSerializer.Serialize(payload)));
            Assert.Equal(new CapeTownInterestEvidence("SYN-INT-CT-001", "SYN-REG-CT-001", idera, declarationNoted),
                Assert.IsType<CapeTownInterestEvidence>(record.Payload.CapeTownInterest));
        }
        var valid = Payload("present", true);
        var rejected = new List<(string Case, string Payload)>();
        foreach (var missing in new[] { "internationalInterestRef", "registrationRef", "idera", "stateDeclarationNoted" })
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(valid))!;
            fields.Remove(missing);
            rejected.Add(($"missing {missing}", JsonSerializer.Serialize(fields)));
        }
        rejected.Add(("empty internationalInterestRef", JsonSerializer.Serialize(Payload("present", true, internationalInterestRef: ""))));
        rejected.Add(("empty registrationRef", JsonSerializer.Serialize(Payload("present", true, registrationRef: ""))));
        rejected.Add(("unlisted idera", JsonSerializer.Serialize(Payload("UNLISTED", true))));
        rejected.Add(("stateDeclarationNoted string", """{"internationalInterestRef":"SYN-INT-CT-001","registrationRef":"SYN-REG-CT-001","idera":"present","stateDeclarationNoted":"true"}"""));
        rejected.Add(("extra field", """{"internationalInterestRef":"SYN-INT-CT-001","registrationRef":"SYN-REG-CT-001","idera":"present","stateDeclarationNoted":true,"unexpected":"value"}"""));
        rejected.Add(("relationship field", $$"""{"internationalInterestRef":"SYN-INT-CT-001","registrationRef":"SYN-REG-CT-001","idera":"present","stateDeclarationNoted":true,"relatedRequirementId":"{{RequirementId}}"}"""));
        foreach (var invalid in rejected)
            Assert.Throws<InvalidDataException>(() => ParseFixture(invalid.Payload));
    }

    [Fact]
    public void LegalConclusionFieldsAreRefusedIndividually()
    {
        var source = new ContractsSource();
        var published = ReadAll(source);
        var publishedContents = published.Select(SerializeRecord).ToArray();
        Assert.Equal(ContractsSource.CoreManifest.RecordCounts["contracts"], published.Count);
        foreach (var field in new[] { "priority", "remedy", "outcome", "recoverability" })
        {
            var payload = Payload("not_recorded", false);
            var json = JsonSerializer.SerializeToElement(payload);
            var values = json.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
            values[field] = JsonSerializer.SerializeToElement("synthetic conclusion");
            Assert.Throws<InvalidDataException>(() => ParseFixture(JsonSerializer.Serialize(values)));

            var current = ReadAll(source);
            Assert.Equal(published.Count, current.Count);
            for (var index = 0; index < published.Count; index++)
                Assert.Same(published[index], current[index]);
            Assert.Equal(publishedContents, current.Select(SerializeRecord));
        }
        Assert.Equal(ContractsSource.CoreManifest.RecordCounts["contracts"], ReadAll(source).Count);
    }

    [Fact]
    public void EvidenceRequiresExactCapeTownFlagAndNeverProducesLegalOutcome()
    {
        using var matching = Assess(new SpecialistReviewFlag(RequirementId, "cape_town", "specialist_review_required"),
            RequirementId, reliesOnCapeTownEvidence: true);
        Assert.Equal("Review", matching.Record.Outcome);
        Assert.Equal("specialist review required", matching.Record.Reason);
        Assert.Equal(new CapeTownInterestEvidence("SYN-INT-CT-001", "SYN-REG-CT-001", "not_recorded", false),
            Assert.Single(matching.Record.Claims).CapeTownEvidence!.Single());
        Assert.Contains(matching.Record.Claims.Single().Witnesses, witness =>
            witness.DocumentId == "source:contracts:con-cape-town-interest-001:v1");
        Assert.Empty(matching.Inbox.Requests);
        Assert.NotNull(matching.Record.WorkItemId);

        var exposed = JsonSerializer.Serialize(matching.Record.Claims.Single().CapeTownEvidence, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"internationalInterestRef\":\"SYN-INT-CT-001\"", exposed, StringComparison.Ordinal);
        foreach (var conclusion in new[] { "priority", "remedy", "outcome", "recoverability" })
            Assert.DoesNotContain(conclusion, exposed, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidDataException>(() => Assess(null, RequirementId, reliesOnCapeTownEvidence: true));
        Assert.Throws<InvalidDataException>(() => Assess(
            new SpecialistReviewFlag(RequirementId, "other", "specialist_review_required"),
            RequirementId, reliesOnCapeTownEvidence: true));
        Assert.Throws<InvalidDataException>(() => Assess(
            new SpecialistReviewFlag("REQ-CAPE-TOWN-OTHER", "cape_town", "specialist_review_required"),
            RequirementId, reliesOnCapeTownEvidence: true));
        Assert.Throws<InvalidDataException>(() => Assess(
            new SpecialistReviewFlag(RequirementId, "cape_town", "specialist_review_required"),
            "req-cape-town", reliesOnCapeTownEvidence: true));
        Assert.Throws<InvalidDataException>(() => Assess(
            new SpecialistReviewFlag(RequirementId, "cape_town", "specialist_review_required"),
            null, reliesOnCapeTownEvidence: true));
    }

    [Fact]
    public async Task PublicAssessmentRequiresExactCapeTownFlagForEveryClaimWhenEvidenceIsPinned()
    {
        await using var endpoint = new AssessmentEndpointScenario();

        using (var matching = await endpoint.SubmitAsync(RequirementId,
                   [new SpecialistReviewFlag(RequirementId, "cape_town", "specialist_review_required")]))
        {
            Assert.Equal(System.Net.HttpStatusCode.Created, matching.StatusCode);
            var body = await matching.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("Review", json.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("specialist review required", json.RootElement.GetProperty("reason").GetString());
            var claim = Assert.Single(json.RootElement.GetProperty("claims").EnumerateArray());
            Assert.Equal(RequirementId, claim.GetProperty("relatedRequirementId").GetString());
            Assert.Equal("specialist_review_required", claim.GetProperty("specialistResult").GetString());
            var capeTownEvidence = Assert.Single(claim.GetProperty("capeTownEvidence").EnumerateArray());
            Assert.Equal("SYN-INT-CT-001", capeTownEvidence.GetProperty("internationalInterestRef").GetString());
            Assert.Equal("SYN-REG-CT-001", capeTownEvidence.GetProperty("registrationRef").GetString());
            Assert.Equal("not_recorded", capeTownEvidence.GetProperty("idera").GetString());
            Assert.False(capeTownEvidence.GetProperty("stateDeclarationNoted").GetBoolean());
            foreach (var conclusion in new[] { "priority", "remedy", "outcome", "recoverability" })
                Assert.DoesNotContain(conclusion, capeTownEvidence.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }

        await AssertRejected(endpoint.SubmitAsync(RequirementId, []));
        await AssertRejected(endpoint.SubmitAsync(RequirementId, [], false));
        await AssertRejected(endpoint.SubmitAsync(RequirementId, [], true));
        await AssertRejected(endpoint.SubmitAsync(RequirementId,
            [new SpecialistReviewFlag(RequirementId, "other", "specialist_review_required")]));
        await AssertRejected(endpoint.SubmitAsync(RequirementId,
            [new SpecialistReviewFlag("REQ-CAPE-TOWN-OTHER", "cape_town", "specialist_review_required")]));
        await AssertRejected(endpoint.SubmitAsync("req-cape-town",
            [new SpecialistReviewFlag(RequirementId, "cape_town", "specialist_review_required")]));
        await AssertRejected(endpoint.SubmitAsync(null,
            [new SpecialistReviewFlag(RequirementId, "cape_town", "specialist_review_required")]));
        await AssertRejected(endpoint.SubmitAsync("REQ-OTHER", []));
    }

    [Fact]
    public void ValidCapeTownFixtureUsesPinnedPublicKey()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sources");
        var pinned = SourceFixtureLoader.LoadPinned(directory);
        Assert.Contains("contracts", pinned.Keys);
        var record = Assert.Single(ReadAll(new ContractsSource()), item => item.Kind == "cape_town_interest");
        Assert.True(record.Synthetic);
        Assert.Equal("con-cape-town-interest-001", record.RecordId);
        Assert.Equal(new CapeTownInterestEvidence("SYN-INT-CT-001", "SYN-REG-CT-001", "not_recorded", false),
            record.Payload.CapeTownInterest);
    }

    private static IReadOnlyList<SourceRecord<ContractsPayload>> ParseFixture(string payload)
    {
        var fixture = Encoding.UTF8.GetBytes(
            $$"""{"sourceId":"contracts","records":[{"recordId":"synthetic-cape-town-001","recordVersion":1,"asOf":"2026-10-01","caseId":"case-001","kind":"cape_town_interest","synthetic":true,"payload":{{payload}}}]}""");
        var manifest = new SourceFixtureManifest
        {
            SchemaVersion = SourceFixtureManifest.SupportedSchemaVersion,
            SourceFixtureVersion = SourceFixtureManifest.SupportedSourceFixtureVersion,
            ScenarioDate = "2026-10-01",
            Sha256 = Convert.ToHexString(SHA256.HashData(fixture)).ToLowerInvariant(),
            RecordCounts = Sources.ToDictionary(source => source, source => source == "contracts" ? 1 : 0),
        };
        return ContractsSource.ParseFixture("test-contracts.json", manifest, fixture);
    }
    private static object Payload(string idera, bool declarationNoted,
        string internationalInterestRef = "SYN-INT-CT-001", string registrationRef = "SYN-REG-CT-001") =>
        new { internationalInterestRef, registrationRef, idera, stateDeclarationNoted = declarationNoted };

    private static IReadOnlyList<SourceRecord<ContractsPayload>> ReadAll(ContractsSource source) =>
        new[] { "case-001", "case-a", "case-b", "case-c" }
            .SelectMany(caseId => Assert.IsType<SourceReadSucceeded<IReadOnlyList<SourceRecord<ContractsPayload>>>>(
                source.ListAsync(caseId).GetAwaiter().GetResult()).Value)
            .ToArray();
    private static string SerializeRecord(SourceRecord<ContractsPayload> record) =>
        JsonSerializer.Serialize(new
        {
            record.SourceId,
            record.RecordId,
            record.RecordVersion,
            record.AsOf,
            record.CaseId,
            record.Kind,
            record.Stale,
            record.Synthetic,
            Fields = record.Payload.Fields.OrderBy(field => field.Key, StringComparer.Ordinal),
            record.Payload.CapeTownInterest,
        });

    private static AssessmentScenario Assess(
        SpecialistReviewFlag? flag,
        string? relatedRequirementId,
        string claimValue = Quote,
        string witnessQuote = Quote,
        bool reliesOnCapeTownEvidence = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cape-town-evidence-{Guid.NewGuid():N}.json");
        var store = new CaseStateStore(path);
        var scope = store.CreateCase("cape-town-case", "presenter");
        var inbox = new MockRequestInbox();
        var assessments = new AssessmentStore(store);
        var pins = new List<SourceBasisPin> { CreateInterestPin() };
        if (flag is not null)
            pins.Add(CreateFlagPin(flag));
        var basis = new AssessmentBasis("basis-cape-town", 1, "2026-10-01T00:00:00Z",
            new ProcessingCoverage(1, 1, 0, 0), false,
            new Dictionary<string, BasisDocument> { ["doc-1"] = BasisDocument.Create("doc-1", [witnessQuote]) },
            pins);
        assessments.SetCurrentBasis(scope.CaseId, basis);
        var claims = new[]
        {
            new AssessmentClaim(ClaimKind.MissingRequirement, claimValue, "Engine", "ENG-1",
                [new DocumentWitness("doc-1", 1, witnessQuote)], relatedRequirementId,
                ReliesOnCapeTownEvidence: reliesOnCapeTownEvidence),
        };
        try
        {
            var record = new AssessmentExecutionService(store, inbox, assessments).Assess(scope, basis, claims);
            return new AssessmentScenario(path, record, inbox);
        }
        catch
        {
            foreach (var file in new[] { path, path + ".lock", path + ".tmp" })
                if (File.Exists(file)) File.Delete(file);
            throw;
        }
    }

    private static SourceBasisPin CreateInterestPin() =>
        Pin("con-cape-town-interest-001", "cape_town_interest", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["internationalInterestRef"] = "SYN-INT-CT-001",
            ["registrationRef"] = "SYN-REG-CT-001",
            ["idera"] = "not_recorded",
            ["stateDeclarationNoted"] = "false",
        });

    private static SourceBasisPin CreateFlagPin(SpecialistReviewFlag flag) =>
        Pin("flag-1", "specialist_flag", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["relatedRequirementId"] = flag.RelatedRequirementId,
            ["topic"] = flag.Topic,
            ["result"] = flag.Result,
        });

    private static SourceBasisPin Pin(string recordId, string kind, IReadOnlyDictionary<string, string> fields)
    {
        var content = JsonSerializer.Serialize(new
        {
            sourceId = "contracts",
            recordId,
            recordVersion = 1,
            kind,
            payload = new { fields },
        });
        return new SourceBasisPin("contracts", recordId, 1, content,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant());
    }

    private static async Task AssertRejected(Task<HttpResponseMessage> responseTask)
    {
        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private sealed class AssessmentEndpointScenario : IAsyncDisposable
    {
        private const string CaseId = "cape-town-endpoint-case";
        private const string Owner = "cape-town-presenter";
        private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"cape-town-endpoint-{Guid.NewGuid():N}.json");
        private readonly CaseStateStore _caseStore;
        private readonly AssessmentStore _assessments;
        private readonly HttpClient _client;
        private readonly DemoSession _session;
        private readonly WebApplication _app;

        public AssessmentEndpointScenario()
        {
            _caseStore = new CaseStateStore(_statePath);
            _caseStore.CreateCase(CaseId, Owner);
            _assessments = new AssessmentStore(_caseStore);
            var inbox = new MockRequestInbox();
            var sessions = new DemoSessionStore(TimeProvider.System);
            _session = sessions.Create(new DemoUser(
                Owner, "Cape Town Presenter", EvidenceEndpoints.PresenterRole, [CaseId]));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<DemoSessionStore>(sessions);
            builder.Services.AddSingleton<ICaseStateStore>(_caseStore);
            builder.Services.AddSingleton(_assessments);
            builder.Services.AddSingleton(inbox);
            builder.Services.AddSingleton(new AssessmentExecutionService(_caseStore, inbox, _assessments));
            _app = builder.Build();
            _app.UseDeveloperExceptionPage();
            _app.MapAssessmentEndpoints();
            _app.StartAsync().GetAwaiter().GetResult();
            _client = _app.GetTestClient();
        }

        public async Task<HttpResponseMessage> SubmitAsync(
            string? relatedRequirementId,
            IReadOnlyList<SpecialistReviewFlag> flags,
            bool? relianceField = null)
        {
            var pins = new List<SourceBasisPin> { CreateInterestPin() };
            pins.AddRange(flags.Select(CreateFlagPin));
            _assessments.SetCurrentBasis(CaseId, new AssessmentBasis(
                "basis-cape-town-endpoint",
                1,
                "2026-10-01T00:00:00Z",
                new ProcessingCoverage(1, 1, 0, 0),
                false,
                new Dictionary<string, BasisDocument>
                {
                    ["doc-1"] = BasisDocument.Create("doc-1", [Quote]),
                },
                pins));

            var claim = new Dictionary<string, object?>
            {
                ["kind"] = "MissingRequirement",
                ["value"] = "return package",
                ["component"] = "Engine",
                ["serialNumber"] = "ENG-1",
                ["witnesses"] = Array.Empty<object>(),
                ["relatedRequirementId"] = relatedRequirementId,
                ["requirementId"] = RequirementId,
                ["evidenceRegime"] = "EASA",
            };
            if (relianceField is not null)
                claim["reliesOnCapeTownEvidence"] = relianceField.Value;
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"/api/cases/{CaseId}/assessments")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { claims = new[] { claim } }),
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={_session.Id}");
            request.Headers.Add(DemoAuthEndpoints.CsrfHeaderName, _session.CsrfToken);
            return await _client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.DisposeAsync();
            foreach (var file in new[] { _statePath, _statePath + ".lock", _statePath + ".tmp" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    private sealed class AssessmentScenario(string path, AssessmentRecord record, MockRequestInbox inbox) : IDisposable
    {
        public AssessmentRecord Record { get; } = record;
        public MockRequestInbox Inbox { get; } = inbox;

        public void Dispose()
        {
            foreach (var file in new[] { path, path + ".lock", path + ".tmp" })
                if (File.Exists(file)) File.Delete(file);
        }
    }
}
