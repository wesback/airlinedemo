using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class CasePackCaseATests : IAsyncDisposable
{
    private const string CaseId = "cp-case-a";
    private const string BasisId = "cp-case-a-basis";
    private const string ScenarioTime = "2026-10-14T00:00:00Z";
    private const string RequirementId = "SYN-REQ-CP-A-001";
    private const string MissingRequirement = "signed life-limited-parts history record";
    private const string Component = "Engine";
    private const string SerialNumber = "ENG-CP-A-001";
    private const string ResponseBody =
        "Synthetic demonstration response: signed history reference SYN-RESPONSE-CP-A-001 is provided for ENG-CP-A-001.";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"case-pack-case-a-{Guid.NewGuid():N}.json");
    private readonly CaseStateStore _caseStore;
    private readonly CountingHandler _outbound = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    private sealed record Session(string Cookie, string Csrf);

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Case A processing must not make an external HTTP request.");
        }
    }

    public CasePackCaseATests()
    {
        _caseStore = new CaseStateStore(_statePath);
        var scope = _caseStore.CreateCase(CaseId, "presenter");
        var evidence = FixtureFiles();
        _caseStore.Commit(scope, 0, new CaseCommit(
            CaseStateStore.CreateBasisVersion(1, string.Join('\n', evidence.Values), string.Empty),
            null, "presenter", "submit-evidence", "synthetic Case A evidence basis"));

        var stateStore = _caseStore;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = "test-passcode",
                ["CaseState:Path"] = _statePath,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICaseStateStore>();
                services.AddSingleton<ICaseStateStore>(stateStore);
                services.RemoveAll<AssessmentStore>();
                services.AddSingleton(sp => new AssessmentStore(sp.GetRequiredService<ICaseStateStore>()));
                services.RemoveAll<AssessmentExecutionService>();
                services.AddSingleton(sp => new AssessmentExecutionService(
                    sp.GetRequiredService<ICaseStateStore>(),
                    sp.GetRequiredService<MockRequestInbox>(),
                    sp.GetRequiredService<AssessmentStore>()));
                services.AddSingleton<HttpMessageHandler>(_outbound);
            });
        });
        _client = _factory.CreateClient();

        var documents = evidence.ToDictionary(
            pair => pair.Key,
            pair => BasisDocument.Create(pair.Key, [pair.Value]),
            StringComparer.Ordinal);
        _factory.Services.GetRequiredService<AssessmentStore>().SetCurrentBasis(CaseId,
            new AssessmentBasis(BasisId, 1, ScenarioTime, new ProcessingCoverage(documents.Count, documents.Count, 0, 0),
                false, documents, [ApplicabilityPin()]));
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        foreach (var path in new[] { _statePath, _statePath + ".lock", _statePath + ".tmp" })
            if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void AC003_CaseAFixtureMatchesPackContract()
    {
        var root = FixtureRoot();
        var fixturePath = Path.Combine(root, "case-a.json");
        Assert.True(File.Exists(fixturePath), $"Case A fixture is missing: {fixturePath}");
        var fixture = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();
        var files = ReadFixtureFiles(fixture, root);

        var cases = new (string Name, Action<JsonObject, Dictionary<string, string>> Change, bool Valid)[]
        {
            ("fixture contract", (_, _) => { }, true),
            ("eight documents", (json, contents) => RemoveDocuments(json, contents, 8), true),
            ("twelve documents", (json, contents) => AddDocuments(json, contents, 12), true),
            ("seven documents", (json, contents) => RemoveDocuments(json, contents, 7), false),
            ("thirteen documents", (json, contents) => AddDocuments(json, contents, 13), false),
            ("missing case synthetic marker", (json, _) => json.Remove("synthetic"), false),
            ("false case synthetic marker", (json, _) => json["synthetic"] = false, false),
            ("missing document synthetic marker", (json, _) => Documents(json)[0]!.AsObject().Remove("synthetic"), false),
            ("false document synthetic marker", (json, _) => Documents(json)[0]!["synthetic"] = false, false),
            ("empty case generator reference", (json, _) => json["generatorReference"] = " ", false),
            ("empty document generator reference", (json, _) => Documents(json)[0]!["generatorReference"] = "", false),
            ("empty component mapping", (json, _) => Components(json)[0]!["evidenceDocumentPaths"] = new JsonArray(), false),
            ("mapping to Case B document", (json, _) =>
                Components(json)[0]!["evidenceDocumentPaths"]![0] = "case-b/maintenance-history.txt", false),
        };

        foreach (var row in cases)
        {
            var candidate = fixture.DeepClone().AsObject();
            var candidateFiles = new Dictionary<string, string>(files, StringComparer.Ordinal);
            row.Change(candidate, candidateFiles);
            var errors = ValidateFixture(candidate, candidateFiles);
            Assert.True(row.Valid == (errors.Count == 0), $"{row.Name}: {string.Join("; ", errors)}");
        }
    }

    [Fact]
    public async Task AC004_CaseACompletesAndReplaysWithoutDuplicates()
    {
        var quote = File.ReadAllText(Path.Combine(FixtureRoot(), "case-a", "llp-history-gap.txt")).Trim();
        var presenter = await SignInAsync();
        var assessmentRequest = AssessmentRequest(presenter, quote);

        using var firstAssessmentResponse = await _client.SendAsync(assessmentRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, firstAssessmentResponse.StatusCode);
        using var firstAssessment = JsonDocument.Parse(
            await firstAssessmentResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Request", firstAssessment.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(BasisId, firstAssessment.RootElement.GetProperty("basisId").GetString());
        Assert.Equal(1, firstAssessment.RootElement.GetProperty("basisVersion").GetInt32());
        var assessmentId = firstAssessment.RootElement.GetProperty("id").GetString();
        var requestId = firstAssessment.RootElement.GetProperty("requestId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(assessmentId));
        Assert.False(string.IsNullOrWhiteSpace(requestId));

        using var firstRequestResponse = await GetRequestsAsync(presenter);
        var firstRequestJson = JsonDocument.Parse(await firstRequestResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var request = Assert.Single(firstRequestJson.RootElement.EnumerateArray());
        Assert.Equal(requestId, request.GetProperty("requestId").GetString());
        Assert.Equal("delivered", request.GetProperty("state").GetString());
        Assert.Equal(
            $"Synthetic demonstration request. For {Component} {SerialNumber}, please provide {MissingRequirement}. Evidence basis {BasisId} v1. Reference {requestId}.",
            request.GetProperty("body").GetString());
        Assert.Equal(0, _outbound.Calls);

        using var replayedAssessmentResponse = await _client.SendAsync(
            AssessmentRequest(presenter, quote), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, replayedAssessmentResponse.StatusCode);
        using var replayedAssessment = JsonDocument.Parse(
            await replayedAssessmentResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(assessmentId, replayedAssessment.RootElement.GetProperty("id").GetString());
        Assert.Equal(requestId, replayedAssessment.RootElement.GetProperty("requestId").GetString());

        var responseCommand = new MockResponseCommand(
            "cp-case-a-response-event-1", "cp-case-a-response-report-1", requestId!, BasisId, 1, ScenarioTime, ResponseBody);
        using var firstMockResponse = await PostMockResponseAsync(presenter, responseCommand);
        Assert.Equal(HttpStatusCode.Created, firstMockResponse.StatusCode);
        var firstReceipt = await firstMockResponse.Content.ReadFromJsonAsync<MockResponseReceipt>(
            WebJson, TestContext.Current.CancellationToken);
        Assert.NotNull(firstReceipt);
        Assert.Equal(requestId, firstReceipt.RequestId);
        Assert.Equal(BasisId, firstReceipt.RequestBasisId);
        Assert.Equal(1, firstReceipt.RequestBasisVersion);
        Assert.Equal(BasisId, firstReceipt.BasisId);
        Assert.Equal(2, firstReceipt.BasisVersion);
        Assert.Equal(0, _outbound.Calls);

        using var replayedMockResponse = await PostMockResponseAsync(presenter, responseCommand);
        Assert.Equal(HttpStatusCode.OK, replayedMockResponse.StatusCode);
        var replayedReceipt = await replayedMockResponse.Content.ReadFromJsonAsync<MockResponseReceipt>(
            WebJson, TestContext.Current.CancellationToken);
        Assert.Equal(firstReceipt, replayedReceipt);

        using var finalRequestsResponse = await GetRequestsAsync(presenter);
        using var finalRequests = JsonDocument.Parse(
            await finalRequestsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var onlyRequest = Assert.Single(finalRequests.RootElement.EnumerateArray());
        Assert.Equal(requestId, onlyRequest.GetProperty("requestId").GetString());
        Assert.Equal("delivered", onlyRequest.GetProperty("state").GetString());

        var cases = _factory.Services.GetRequiredService<ICaseStateStore>();
        var caseScope = cases.AuthorizeCase(CaseId, "presenter");
        var state = cases.Read(caseScope);
        Assert.Equal(2, state.Basis.Count);
        Assert.Equal(1, state.Basis[0].Version);
        Assert.Equal(2, state.Basis[1].Version);
        Assert.Equal(string.Join('\n', FixtureFiles().Values), state.Basis[0].Content);
        Assert.Contains(ResponseBody, state.Basis[1].Content, StringComparison.Ordinal);
        var storedResponses = state.Audit
            .Where(audit => audit.Action == MockResponseService.AcceptedAction)
            .ToArray();
        var storedResponse = Assert.Single(storedResponses);
        using var storedResponseDetail = JsonDocument.Parse(storedResponse.Detail);
        Assert.Equal(requestId,
            storedResponseDetail.RootElement.GetProperty("receipt").GetProperty("requestId").GetString());

        var history = _factory.Services.GetRequiredService<AssessmentStore>().List(CaseId);
        Assert.Equal(2, history.Count);
        Assert.Equal((BasisId, 1, "Request"), (history[0].BasisId, history[0].BasisVersion, history[0].Outcome));
        Assert.Equal((BasisId, 2, "Review"), (history[1].BasisId, history[1].BasisVersion, history[1].Outcome));
        Assert.Equal("no claim", history[1].Reason);
        Assert.Equal(requestId, history[0].RequestId);
        Assert.Equal(0, _outbound.Calls);
    }

    private async Task<Session> SignInAsync()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/auth/sign-in", new { userId = "presenter", passcode = "test-passcode" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var sessionId = cookie[(cookie.IndexOf('=') + 1)..];
        _factory.Services.GetRequiredService<DemoSessionStore>().GrantScope(sessionId, CaseId);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return new Session(cookie, body.RootElement.GetProperty("csrfToken").GetString()!);
    }

    private HttpRequestMessage AssessmentRequest(Session session, string quote)
    {
        var request = AuthorizedRequest(HttpMethod.Post, $"/api/cases/{CaseId}/assessments", session);
        request.Content = JsonContent.Create(new
        {
            claims = new[]
            {
                new
                {
                    kind = nameof(ClaimKind.MissingRequirement),
                    value = MissingRequirement,
                    component = Component,
                    serialNumber = SerialNumber,
                    witnesses = new[] { new { documentId = "case-a/llp-history-gap.txt", page = 1, quote } },
                    requirementId = RequirementId,
                    evidenceRegime = "EASA",
                },
            },
        });
        return request;
    }

    private Task<HttpResponseMessage> PostMockResponseAsync(Session session, MockResponseCommand command)
    {
        var request = AuthorizedRequest(HttpMethod.Post, $"/api/cases/{CaseId}/mock-responses", session);
        request.Content = JsonContent.Create(command);
        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> GetRequestsAsync(Session session) =>
        _client.SendAsync(AuthorizedRequest(HttpMethod.Get, $"/api/cases/{CaseId}/requests", session),
            TestContext.Current.CancellationToken);

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string path, Session session)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Add("X-CSRF-Token", session.Csrf);
        return request;
    }

    private static SourceBasisPin ApplicabilityPin()
    {
        var content = JsonSerializer.Serialize(new
        {
            kind = "applicability",
            payload = new
            {
                fields = new Dictionary<string, string>
                {
                    ["requirementId"] = RequirementId,
                    ["regime"] = "EASA",
                    ["responsibleParty"] = "lessee-operator",
                },
            },
        });
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return new SourceBasisPin("technical", "app-" + RequirementId, 1, content, digest);
    }

    private static string FixtureRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "AirlineDemo.Api", "Fixtures", "aircraft-case-pack"));

    private static Dictionary<string, string> FixtureFiles()
    {
        var root = FixtureRoot();
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "case-a.json")))!.AsObject();
        return ReadFixtureFiles(fixture, root);
    }

    private static Dictionary<string, string> ReadFixtureFiles(JsonObject fixture, string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in Documents(fixture))
        {
            var path = document!["path"]!.GetValue<string>();
            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"Case A document is missing: {path}");
            files.Add(path, File.ReadAllText(fullPath));
        }

        return files;
    }

    private static List<string> ValidateFixture(JsonObject fixture, IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        if (fixture["caseId"]?.GetValue<string>() != CaseId) errors.Add("caseId");
        if (fixture["synthetic"]?.GetValue<bool>() != true) errors.Add("synthetic");
        if (string.IsNullOrWhiteSpace(fixture["generatorReference"]?.GetValue<string>())) errors.Add("generatorReference");
        if (fixture["aircraftCount"]?.GetValue<int>() != 1) errors.Add("aircraftCount");
        if (fixture["engineCount"]?.GetValue<int>() != 1) errors.Add("engineCount");

        var documentNodes = Documents(fixture);
        if (documentNodes.Count is < 8 or > 12) errors.Add("documents count");
        var documentPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in documentNodes)
        {
            if (node is not JsonObject document) { errors.Add("document object"); continue; }
            var path = document["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path) || !documentPaths.Add(path)) errors.Add("document path");
            if (document["synthetic"]?.GetValue<bool>() != true) errors.Add("document synthetic");
            if (string.IsNullOrWhiteSpace(document["generatorReference"]?.GetValue<string>())) errors.Add("document generatorReference");
            if (string.IsNullOrWhiteSpace(path) || !files.TryGetValue(path, out var content) || string.IsNullOrWhiteSpace(content))
                errors.Add("document text");
        }

        if (!documentPaths.SetEquals(files.Keys)) errors.Add("document/file paths");
        var componentNodes = Components(fixture);
        if (componentNodes.Count != 2) errors.Add("components count");
        foreach (var node in componentNodes)
        {
            if (node is not JsonObject component) { errors.Add("component object"); continue; }
            if (string.IsNullOrWhiteSpace(component["componentId"]?.GetValue<string>())) errors.Add("componentId");
            if (component["evidenceDocumentPaths"] is not JsonArray mapping || mapping.Count == 0)
            {
                errors.Add("evidenceDocumentPaths");
                continue;
            }

            foreach (var mapped in mapping)
            {
                var path = mapped?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(path) || !documentPaths.Contains(path)) errors.Add("evidence mapping");
            }
        }

        return errors;
    }

    private static JsonArray Documents(JsonObject fixture) => fixture["documents"]!.AsArray();
    private static JsonArray Components(JsonObject fixture) => fixture["components"]!.AsArray();

    private static void RemoveDocuments(JsonObject fixture, Dictionary<string, string> files, int count)
    {
        var documents = Documents(fixture);
        while (documents.Count > count)
        {
            var last = documents[^1]!;
            var path = last["path"]!.GetValue<string>();
            files.Remove(path);
            foreach (var component in Components(fixture))
            {
                var mapping = component!["evidenceDocumentPaths"]!.AsArray();
                for (var index = mapping.Count - 1; index >= 0; index--)
                    if (mapping[index]!.GetValue<string>() == path) mapping.RemoveAt(index);
            }
            documents.RemoveAt(documents.Count - 1);
        }
    }

    private static void AddDocuments(JsonObject fixture, Dictionary<string, string> files, int count)
    {
        var documents = Documents(fixture);
        while (documents.Count < count)
        {
            var path = $"case-a/boundary-document-{documents.Count + 1}.txt";
            documents.Add(new JsonObject
            {
                ["path"] = path,
                ["synthetic"] = true,
                ["generatorReference"] = "boundary-test-generator",
            });
            files.Add(path, "Synthetic boundary test text.");
        }
    }
}
