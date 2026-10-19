using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class SourcePersonalDataTests
{
    private const string CaseId = "case-001";
    private const string MarkedRecordId = "rec-doc-arr-0012";
    private static readonly string[] Sources = ["technical", "maintenance", "finance", "records", "contracts"];

    [Fact]
    public void MarkerMustBeBooleanAndRefusalPreservesPublishedRecords()
    {
        using var key = RSA.Create(2048);
        (string? Marker, bool Accepted)[] inputs =
        [
            ("true", true),
            ("false", true),
            (null, false),
            ("null", false),
            ("\"true\"", false),
        ];

        foreach (var (marker, accepted) in inputs)
        {
            using var directory = new TempDirectory();
            WriteFixtureSet(directory.Path, key, marker);
            var store = new SourceFixtureStore(SeedState());
            var before = store.State;

            if (accepted)
            {
                Assert.Equal(
                    SourceFixturePublication.Published,
                    SourceFixtureLoader.LoadAndPublish(directory.Path, key.ExportSubjectPublicKeyInfo(), store));
                Assert.Equal(2, store.State.Packs.Count);
                Assert.Equal(6, store.State.SourceRecords.Count);
            }
            else
            {
                Assert.Throws<SourceFixtureRefusedException>(() =>
                    SourceFixtureLoader.LoadAndPublish(directory.Path, key.ExportSubjectPublicKeyInfo(), store));
                AssertStateEqual(before, store.State);
            }
        }
    }

    [Fact]
    public async Task MarkedRecordsExposeOnlyIdentifiersVersionAndMarker()
    {
        using var directory = new TempDirectory();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<DemoSessionStore>();
        builder.Services.AddSingleton<ICaseStateStore>(_ => new CaseStateStore(Path.Combine(directory.Path, "case-state.json")));
        builder.Services.AddSingleton<ISourceReadAdmissionStore, InMemorySourceReadAdmissionStore>();
        builder.Services.AddSingleton<SourceModeStore>();
        builder.Services.AddSingleton<SourceReadBudget>();
        builder.Services.AddSingleton<ISourceSystem<RecordsPayload>, RecordsSource>();

        await using var app = builder.Build();
        app.MapSourceReadEndpoints();
        await app.StartAsync();
        app.Services.GetRequiredService<ICaseStateStore>().CreateCase(CaseId, "test-owner");
        var session = app.Services.GetRequiredService<DemoSessionStore>()
            .Create(new DemoUser("personal-data-reader", "Test", "technical-reviewer", [CaseId]));
        using var client = app.GetTestClient();

        using var listRequest = Request($"/api/v1/cases/{CaseId}/sources/records/records", session.Id);
        using var listResponse = await client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listBody = await listResponse.Content.ReadAsStringAsync();
        using var list = JsonDocument.Parse(listBody);
        var listRoot = list.RootElement;
        AssertProperties(listRoot, "contractVersion", "caseId", "sourceId", "records");
        Assert.Equal(1, listRoot.GetProperty("contractVersion").GetInt32());
        Assert.Equal(CaseId, listRoot.GetProperty("caseId").GetString());
        Assert.Equal("records", listRoot.GetProperty("sourceId").GetString());

        var records = listRoot.GetProperty("records");
        Assert.Equal(3, records.GetArrayLength());
        var marked = records.EnumerateArray().Single(record => record.GetProperty("recordId").GetString() == MarkedRecordId);
        AssertRecordShape(marked);
        Assert.Equal("DOC-03", marked.GetProperty("payload").GetProperty("documentId").GetString());
        Assert.Equal("1", marked.GetProperty("payload").GetProperty("documentVersion").GetString());
        Assert.True(marked.GetProperty("payload").GetProperty("containsPersonalData").GetBoolean());

        using var getRequest = Request($"/api/v1/cases/{CaseId}/sources/records/records/{MarkedRecordId}", session.Id);
        using var getResponse = await client.SendAsync(getRequest);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadAsStringAsync();
        using var get = JsonDocument.Parse(getBody);
        var getRoot = get.RootElement;
        AssertProperties(getRoot, "contractVersion", "caseId", "sourceId", "record");
        Assert.Equal(1, getRoot.GetProperty("contractVersion").GetInt32());
        Assert.Equal(CaseId, getRoot.GetProperty("caseId").GetString());
        Assert.Equal("records", getRoot.GetProperty("sourceId").GetString());
        AssertRecordShape(getRoot.GetProperty("record"));
        Assert.Equal(MarkedRecordId, getRoot.GetProperty("record").GetProperty("recordId").GetString());
        Assert.True(getRoot.GetProperty("record").GetProperty("payload").GetProperty("containsPersonalData").GetBoolean());

        foreach (var responseBody in new[] { listBody, getBody })
        {
            Assert.DoesNotContain("subjectName", responseBody, StringComparison.Ordinal);
            Assert.DoesNotContain("licenseNumber", responseBody, StringComparison.Ordinal);
            Assert.DoesNotContain("SYN-PERSON-NAME-01", responseBody, StringComparison.Ordinal);
            Assert.DoesNotContain("SYN-LICENSE-NUMBER-01", responseBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UpdatedFixtureVerifiesAndTamperingWithoutDetachedSignatureFailsAtomically()
    {
        using var key = RSA.Create(2048);
        using var directory = new TempDirectory();
        WriteFixtureSet(directory.Path, key, "false");

        var fixturePath = Path.Combine(directory.Path, "records.json");
        var changedFixture = File.ReadAllBytes(fixturePath).Append((byte)'\n').ToArray();
        File.WriteAllBytes(fixturePath, changedFixture);
        var manifestPath = Path.Combine(directory.Path, "records.manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["sha256"] = Convert.ToHexString(SHA256.HashData(changedFixture)).ToLowerInvariant();
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest));

        var store = new SourceFixtureStore(SeedState());
        var before = store.State;
        Assert.Throws<SourceFixtureRefusedException>(() =>
            SourceFixtureLoader.LoadAndPublish(directory.Path, key.ExportSubjectPublicKeyInfo(), store));
        AssertStateEqual(before, store.State);

        Assert.Equal(5, SourceFixtureLoader.LoadPinned(CommittedSources()).Count);
    }

    private static HttpRequestMessage Request(string path, string sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"__Host-demo-session={sessionId}");
        return request;
    }

    private static void AssertRecordShape(JsonElement record)
    {
        AssertProperties(
            record,
            "sourceId", "recordId", "recordVersion", "asOf", "caseId", "kind", "stale", "payload", "synthetic");
        var payload = record.GetProperty("payload");
        AssertProperties(payload, "documentId", "documentVersion", "containsPersonalData");
        Assert.Equal("records", record.GetProperty("sourceId").GetString());
        Assert.Equal(MarkedRecordId, record.GetProperty("recordId").GetString());
        Assert.Equal(1, record.GetProperty("recordVersion").GetInt32());
        Assert.Equal("2026-10-01", record.GetProperty("asOf").GetString());
        Assert.Equal(CaseId, record.GetProperty("caseId").GetString());
        Assert.Equal("document_index", record.GetProperty("kind").GetString());
        Assert.False(record.GetProperty("stale").GetBoolean());
        Assert.True(record.GetProperty("synthetic").GetBoolean());
        Assert.Equal("DOC-03", payload.GetProperty("documentId").GetString());
        Assert.Equal("1", payload.GetProperty("documentVersion").GetString());
        Assert.True(payload.GetProperty("containsPersonalData").GetBoolean());
    }

    private static void AssertProperties(JsonElement element, params string[] expected)
    {
        Assert.Equal(
            expected.OrderBy(property => property, StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).OrderBy(property => property, StringComparer.Ordinal));
    }

    private static string CommittedSources() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sources");

    private static void WriteFixtureSet(string directory, RSA signingKey, string? marker)
    {
        foreach (var source in Sources)
        {
            var payload = marker is null
                ? """{"documentId":"SYN-DOC0001","documentVersion":"1"}"""
                : $$"""{"documentId":"SYN-DOC0001","documentVersion":"1","containsPersonalData":{{marker}}}""";
            var fixture = source == "records"
                ? Encoding.UTF8.GetBytes($$"""{"sourceId":"records","records":[{"recordId":"rec-doc-0001","caseId":"case-001","payload":{{payload}}}]}""")
                : JsonSerializer.SerializeToUtf8Bytes(new
                {
                    sourceId = source,
                    records = new[] { new { caseId = CaseId } },
                });
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "1",
                sourceFixtureVersion = "2026.10.1",
                scenarioDate = "2026-10-01",
                owningCaseIds = new[] { CaseId },
                sha256 = Convert.ToHexString(SHA256.HashData(fixture)).ToLowerInvariant(),
                recordCounts = Sources.ToDictionary(name => name, _ => 1, StringComparer.Ordinal),
            });

            File.WriteAllBytes(Path.Combine(directory, $"{source}.json"), fixture);
            File.WriteAllBytes(Path.Combine(directory, $"{source}.manifest.json"), manifest);
            File.WriteAllBytes(
                Path.Combine(directory, $"{source}.manifest.sig"),
                signingKey.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        }
    }

    private static PublishedFixtureState SeedState() => new(
        [new StoredFixturePack("2026.09.1", "seed-digest")],
        [new StoredSourceRecord("technical", "2026.09.1", 0, CaseId)],
        [new StoredBasisPin("technical", "2026.09.1", "seed-digest")],
        [new StoredAuditEvent(1, "source-fixtures.published", "2026.09.1")],
        [new StoredCaseDatum(CaseId, "seed-data")]);

    private static void AssertStateEqual(PublishedFixtureState expected, PublishedFixtureState actual)
    {
        Assert.Equal(expected.Packs, actual.Packs);
        Assert.Equal(expected.SourceRecords, actual.SourceRecords);
        Assert.Equal(expected.BasisPins, actual.BasisPins);
        Assert.Equal(expected.AuditEvents, actual.AuditEvents);
        Assert.Equal(expected.CaseData, actual.CaseData);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
