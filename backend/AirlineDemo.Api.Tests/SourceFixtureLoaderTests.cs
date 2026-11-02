using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.SourceSystems;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AirlineDemo.Api.Tests;

public sealed class SourceFixtureLoaderTests
{
    private const string ValidDigest = "3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b";

    private static SourceFixtureManifest ValidManifest() => new()
    {
        SchemaVersion = "1",
        SourceFixtureVersion = "2026.10.1",
        ScenarioDate = "2026-10-01",
        Sha256 = ValidDigest,
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 4,
            ["maintenance"] = 3,
            ["finance"] = 5,
            ["records"] = 6,
            ["contracts"] = 2,
        },
    };

    // AC-004: a manifest with supported metadata and all declared counts validates.
    [Fact]
    public void ManifestMetadataAcceptsSupportedValues()
    {
        var manifest = ValidManifest();

        Assert.Equal("1", manifest.SchemaVersion);
        Assert.Equal("2026.10.1", manifest.SourceFixtureVersion);
        Assert.Equal("2026-10-01", manifest.ScenarioDate);
        Assert.Equal(ValidDigest, manifest.Sha256);
        Assert.Equal(4, manifest.RecordCounts["technical"]);
        Assert.Empty(manifest.Validate());
        Assert.Empty((manifest with { SourceFixtureVersion = "2026.10.2" }).Validate());
        Assert.Equal(["sourceFixtureVersion '2026.10.3' is not supported; expected '2026.10.1' or '2026.10.2'."],
            (manifest with { SourceFixtureVersion = "2026.10.3" }).Validate());
    }

    // AC-004: unsupported schema version, invalid scenario date, malformed digest and a missing source count are rejected.
    [Fact]
    public void ManifestMetadataRejectsInvalidValues()
    {
        var unsupportedSchema = ValidManifest() with { SchemaVersion = "2" };
        Assert.Equal(
            ["schemaVersion '2' is not supported; expected '1'."],
            unsupportedSchema.Validate());

        var invalidDate = ValidManifest() with { ScenarioDate = "2026-02-30" };
        Assert.Equal(
            ["scenarioDate '2026-02-30' must be a valid date in YYYY-MM-DD form."],
            invalidDate.Validate());

        var malformedDigest = ValidManifest() with { Sha256 = ValidDigest[..63] };
        Assert.Equal(
            ["sha256 must be 64 hexadecimal characters."],
            malformedDigest.Validate());

        var digestWithTrailingNewline = ValidManifest() with { Sha256 = ValidDigest + "\n" };
        Assert.Equal(
            ["sha256 must be 64 hexadecimal characters."],
            digestWithTrailingNewline.Validate());

        var missingCount = ValidManifest();
        var counts = new Dictionary<string, int>(missingCount.RecordCounts);
        counts.Remove("finance");
        missingCount = missingCount with { RecordCounts = counts };
        Assert.Equal(
            ["recordCounts is missing a count for source 'finance'."],
            missingCount.Validate());
    }

    private static readonly string[] Sources = ["technical", "maintenance", "finance", "records", "contracts"];
    private static readonly string[] OwningCaseIds = ["arr-core", "case-a", "case-b", "case-c"];

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void FixtureSetLoadsWithoutSignaturesInEveryEnvironment(string environment)
    {
        using var directory = new TempDirectory();
        foreach (var file in Directory.GetFiles(CommittedSources(), "*.json"))
        {
            File.Copy(file, Path.Combine(directory.Path, Path.GetFileName(file)));
        }

        Assert.Empty(Directory.GetFiles(directory.Path, "*.sig"));
        var fixtures = SourceFixtureLoader.Load(directory.Path, environment: environment);
        Assert.Equal(5, fixtures.Count);
        Assert.All(Sources, source => Assert.Contains(source, fixtures.Keys));
    }

    // AC-004: all five sources load when their manifest checks pass.
    [Fact]
    public void ValidManifestLoadsAfterManifestValidation()
    {
        using var dir = new TempDirectory();
        WriteSet(dir.Path);

        var loaded = SourceFixtureLoader.Load(dir.Path);

        Assert.Equal(5, loaded.Count);
        foreach (var source in Sources)
        {
            Assert.Equal("2026.10.1", loaded[source].Manifest.SourceFixtureVersion);
            Assert.Equal(4, loaded[source].Manifest.RecordCounts[source]);
        }
    }

    // AC-005: unsupported schema, wrong scenario date, wrong digest or wrong count refuses registration.
    [Fact]
    public void InvalidSchemaDateDigestOrCountIsRefused()
    {
        using (var dir = new TempDirectory())
        {
            WriteSet(dir.Path, schemaVersion: "2");
            AssertRefused(dir.Path, "schemaVersion '2' is not supported; expected '1'.");
        }

        using (var dir = new TempDirectory())
        {
            WriteSet(dir.Path, scenarioDate: "2026-10-02");
            AssertRefused(dir.Path, "scenarioDate must be 2026-10-01.");
        }

        using (var dir = new TempDirectory())
        {
            WriteSet(dir.Path, digest: new string('0', 64));
            AssertRefused(dir.Path, "fixture SHA-256 digest does not match the manifest.");
        }

        using (var dir = new TempDirectory())
        {
            WriteSet(dir.Path, declaredCount: 5);
            AssertRefused(dir.Path, "declared record count 5 does not match 4 records.");
        }
    }

    [Theory]
    [InlineData("sha256", "sha256 must be 64 hexadecimal characters.")]
    [InlineData("recordCounts", "recordCounts is required.")]
    public void NullManifestFieldsAreRefused(string field, string reason)
    {
        using var dir = new TempDirectory();
        WriteSet(dir.Path);
        var path = Path.Combine(dir.Path, "technical.manifest.json");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(path))!;
        manifest[field] = JsonSerializer.SerializeToElement<string?>(null);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest));

        AssertRefused(dir.Path, reason);
    }

    [Fact]
    public void ChangedFixtureAndMatchingDigestAreAcceptedWithoutSignatures()
    {
        using var dir = new TempDirectory();
        WriteSet(dir.Path);
        var fixturePath = Path.Combine(dir.Path, "technical.json");
        var changedFixture = File.ReadAllBytes(fixturePath).Append((byte)'\n').ToArray();
        File.WriteAllBytes(fixturePath, changedFixture);
        File.WriteAllBytes(Path.Combine(dir.Path, "technical.manifest.json"), ManifestBytes(changedFixture));

        Assert.Empty(Directory.GetFiles(dir.Path, "*.sig"));
        Assert.Equal(changedFixture, SourceFixtureLoader.Load(dir.Path)["technical"].FixtureBytes);
    }

    [Fact]
    public void MissingManifestIsRefused()
    {
        using var dir = new TempDirectory();
        WriteSet(dir.Path);
        File.Delete(Path.Combine(dir.Path, "technical.manifest.json"));
        AssertRefused(dir.Path, "manifest file 'technical.manifest.json' is missing.");
    }

    // AC-005: a record whose caseId is not a declared owning case refuses registration.
    [Fact]
    public void CaseOwnershipMismatchIsRefused()
    {
        using var dir = new TempDirectory();
        WriteSet(dir.Path, recordCaseIds: ["arr-core", "case-a", "case-b", "case-x"]);

        AssertRefused(dir.Path, "record caseId 'case-x' is not a declared owning case.");
    }

    // AC-004: a fixture set whose sourceFixtureVersion is empty is refused.
    [Fact]
    public void UnsupportedSourceFixtureVersionIsRefused()
    {
        using var dir = new TempDirectory();
        WriteSet(dir.Path, sourceFixtureVersion: string.Empty);

        AssertRefused(dir.Path, "sourceFixtureVersion is required.");
    }

    // AC-004: a fixture set whose sourceFixtureVersion is unknown (non-empty) is refused.
    [Fact]
    public void UnknownNonEmptySourceFixtureVersionIsRefused()
    {
        using var dir = new TempDirectory();
        WriteSet(dir.Path, sourceFixtureVersion: "2026.09.9");

        AssertRefused(dir.Path, "sourceFixtureVersion '2026.09.9' is not supported; expected '2026.10.1' or '2026.10.2'.");
    }

    // AC-005, NFR-002: an invalid digest or publication failure leaves every published collection unchanged.
    [Fact]
    public void ValidationOrPublicationFailureLeavesPublishedStateUnchanged()
    {
        using var validDir = new TempDirectory();
        using var badDir = new TempDirectory();
        WriteSet(validDir.Path);
        WriteSet(badDir.Path);
        File.AppendAllText(Path.Combine(badDir.Path, "technical.json"), "\n");

        var seed = new PublishedFixtureState(
            [new StoredFixturePack("2026.09.1", "aa")],
            [new StoredSourceRecord("technical", "2026.09.1", 0, "arr-core")],
            [new StoredBasisPin("technical", "2026.09.1", "aa")],
            [new StoredAuditEvent(1, "source-fixtures.published", "2026.09.1")],
            [new StoredCaseDatum("arr-core", "unrelated case data")]);
        var store = new SourceFixtureStore(seed);
        var before = Snapshot(store.State);

        var error = Assert.Throws<SourceFixtureRefusedException>(() => SourceFixtureLoader.LoadAndPublish(badDir.Path, store));
        Assert.Equal("Source fixture 'technical' refused: fixture SHA-256 digest does not match the manifest.", error.Message);
        AssertSnapshot(before, store.State);

        Assert.Throws<InvalidOperationException>(() =>
            SourceFixtureLoader.LoadAndPublish(validDir.Path, store, _ => throw new InvalidOperationException("publication failed")));
        AssertSnapshot(before, store.State);
    }

    // AC-005, NFR-002: the same stored version with the same digest is a no-op; with different content it is refused and the store is unchanged.
    [Fact]
    public void ReusedVersionWithDifferentContentIsRefused()
    {
        using var validDir = new TempDirectory();
        using var changedDir = new TempDirectory();
        WriteSet(validDir.Path);
        WriteSet(changedDir.Path, recordCaseIds: ["arr-core", "case-a", "case-b", "arr-core"]);

        var store = new SourceFixtureStore(PublishedFixtureState.Empty);
        Assert.Equal(SourceFixturePublication.Published, SourceFixtureLoader.LoadAndPublish(validDir.Path, store));
        var before = Snapshot(store.State);

        Assert.Equal(SourceFixturePublication.AlreadyPublished, SourceFixtureLoader.LoadAndPublish(validDir.Path, store));
        AssertSnapshot(before, store.State);

        var error = Assert.Throws<SourceFixtureRefusedException>(() => SourceFixtureLoader.LoadAndPublish(changedDir.Path, store));
        Assert.Equal("Source fixture version '2026.10.1' is already stored with different content; publication is refused.", error.Message);
        AssertSnapshot(before, store.State);
    }

    private static string CommittedSources() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sources");

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*.json")) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }

    private static List<JsonElement> CommittedRecords(string source)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CommittedSources(), $"{source}.json")));
        return document.RootElement.GetProperty("records").EnumerateArray().Select(record => record.Clone()).ToList();
    }

    // AC-004: every record in each committed fixture carries a JSON boolean true synthetic marker.
    [Fact]
    public void AC004_all_fixture_records_are_synthetic()
    {
        foreach (var source in Sources)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CommittedSources(), $"{source}.json")));
            foreach (var record in document.RootElement.GetProperty("records").EnumerateArray())
            {
                Assert.Equal(JsonValueKind.True, record.GetProperty("synthetic").ValueKind);
            }
        }
    }

    // AC-004: the default dependency-injection registrations are exactly the five fixture-backed source implementations,
    // and none returns records for an unknown case.
    [Fact]
    public async Task AC004_default_sources_return_no_records_for_unknown_case()
    {
        using var factory = new WebApplicationFactory<Program>();
        var services = factory.Services;

        await AssertUnknownCaseAsync<TechnicalPayload, TechnicalSource>(services);
        await AssertUnknownCaseAsync<MaintenancePayload, MaintenanceSource>(services);
        await AssertUnknownCaseAsync<FinancePayload, FinanceSource>(services);
        await AssertUnknownCaseAsync<RecordsPayload, RecordsSource>(services);
        await AssertUnknownCaseAsync<ContractsPayload, ContractsSource>(services);
    }

    // AC-004: each CoreManifest and committed sidecar describes the exact fixture bytes and record count.
    [Fact]
    public void AC004_default_di_manifests_match_all_fixture_sets()
    {
        var coreManifests = new Dictionary<string, SourceFixtureManifest>
        {
            ["technical"] = TechnicalSource.CoreManifest,
            ["maintenance"] = MaintenanceSource.CoreManifest,
            ["finance"] = FinanceSource.CoreManifest,
            ["records"] = RecordsSource.CoreManifest,
            ["contracts"] = ContractsSource.CoreManifest,
        };
        var counts = Sources.ToDictionary(source => source, source => CommittedRecords(source).Count);

        foreach (var source in Sources)
        {
            var fixtureBytes = File.ReadAllBytes(Path.Combine(CommittedSources(), $"{source}.json"));
            using var fixture = JsonDocument.Parse(fixtureBytes);
            var recordCount = fixture.RootElement.GetProperty("records").GetArrayLength();
            var digest = Convert.ToHexString(SHA256.HashData(fixtureBytes)).ToLowerInvariant();

            AssertManifestMetadata(coreManifests[source], source, digest, recordCount);

            using var sidecar = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CommittedSources(), $"{source}.manifest.json")));
            AssertManifestMetadata(sidecar.RootElement, source, digest, recordCount);
            foreach (var (countSource, expectedCount) in counts)
            {
                Assert.Equal(expectedCount, sidecar.RootElement.GetProperty("recordCounts").GetProperty(countSource).GetInt32());
            }
        }
    }

    private static async Task AssertUnknownCaseAsync<TPayload, TSource>(IServiceProvider services)
        where TPayload : class
        where TSource : class, ISourceSystem<TPayload>
    {
        var registeredSources = services.GetServices<ISourceSystem<TPayload>>();
        var source = Assert.IsType<TSource>(Assert.Single(registeredSources));
        var result = Assert.IsType<SourceReadSucceeded<IReadOnlyList<SourceRecord<TPayload>>>>(
            await source.ListAsync("case-unknown"));

        Assert.Empty(result.Value);
    }

    private static void AssertManifestMetadata(
        SourceFixtureManifest manifest,
        string source,
        string digest,
        int recordCount)
    {
        Assert.Equal("1", manifest.SchemaVersion);
        Assert.Equal("2026.10.2", manifest.SourceFixtureVersion);
        Assert.Equal("2026-10-01", manifest.ScenarioDate);
        Assert.Equal(digest, manifest.Sha256);
        Assert.Equal(recordCount, manifest.RecordCounts[source]);
    }

    private static void AssertManifestMetadata(JsonElement manifest, string source, string digest, int recordCount)
    {
        Assert.Equal("1", manifest.GetProperty("schemaVersion").GetString());
        Assert.Equal("2026.10.2", manifest.GetProperty("sourceFixtureVersion").GetString());
        Assert.Equal("2026-10-01", manifest.GetProperty("scenarioDate").GetString());
        Assert.Equal(digest, manifest.GetProperty("sha256").GetString());
        Assert.Equal(recordCount, manifest.GetProperty("recordCounts").GetProperty(source).GetInt32());
    }

    // AC-004: the core case and Cases A, B and C each have records in all five sources, with the scenario each case exists to show.
    [Fact]
    public void AC004_fixtures_cover_core_and_three_scenarios_in_every_source()
    {
        string[] coreCases = ["case-001", "case-a", "case-b", "case-c"];
        foreach (var source in Sources)
        {
            // The maintenance fixture also owns the copilot showcase case, which carries one task card.
            string[] expectedCases = source == "maintenance" ? [.. coreCases, "cp-case-a"] : coreCases;
            var records = CommittedRecords(source);
            Assert.Equal(expectedCases, records.Select(record => record.GetProperty("caseId").GetString()!).Distinct().Order().ToArray()
                .Select(id => id).OrderBy(id => Array.IndexOf(expectedCases, id)).ToArray());
            foreach (var caseId in expectedCases)
            {
                Assert.Contains(records, record => record.GetProperty("caseId").GetString() == caseId);
            }

            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CommittedSources(), $"{source}.manifest.json")));
            Assert.Equal(expectedCases, manifest.RootElement.GetProperty("owningCaseIds").EnumerateArray().Select(id => id.GetString()!).ToArray());
            Assert.Equal(records.Count, manifest.RootElement.GetProperty("recordCounts").GetProperty(source).GetInt32());
        }

        foreach (var record in CommittedRecords("records"))
        {
            var payload = record.GetProperty("payload");
            Assert.True(payload.GetProperty("containsPersonalData").ValueKind is JsonValueKind.True or JsonValueKind.False);
            // Only the record that carries subjectName and licenseNumber is marked true; every other record is false.
            var carriesPersonalFields = payload.TryGetProperty("subjectName", out _) && payload.TryGetProperty("licenseNumber", out _);
            Assert.Equal(carriesPersonalFields, payload.GetProperty("containsPersonalData").GetBoolean());
        }
        Assert.Single(CommittedRecords("records"), record => record.GetProperty("payload").GetProperty("containsPersonalData").GetBoolean());

        // Case A: a maintenance history record that explains the missing-history finding.
        Assert.Contains(CommittedRecords("maintenance"), record =>
            record.GetProperty("caseId").GetString() == "case-a" && record.GetProperty("kind").GetString() == "maintenance_history");
        // Case B: the technical engine identity (serial SYN-SN-77201) that the case-b release document contradicts (it states SYN-SN-77210).
        Assert.Contains(CommittedRecords("technical"), record =>
            record.GetProperty("caseId").GetString() == "case-b" && record.GetProperty("kind").GetString() == "component_identity"
            && record.GetProperty("payload").GetProperty("serialNumber").GetString() == "SYN-SN-77201");
        Assert.Contains(CommittedRecords("records"), record =>
            record.GetProperty("caseId").GetString() == "case-b" && record.GetProperty("payload").GetProperty("documentType").GetString() == "component-release");
        // Case C: a baseline in every source and a records entry for the blocked document.
        Assert.Contains(CommittedRecords("records"), record =>
            record.GetProperty("caseId").GetString() == "case-c" && record.GetProperty("payload").GetProperty("parseStatus").GetString() == "blocked");

        // A record whose case ID is outside the manifest's owning IDs is refused.
        using var dir = new TempDirectory();
        WriteSet(dir.Path, recordCaseIds: ["arr-core", "case-a", "case-b", "case-x"]);
        AssertRefused(dir.Path, "record caseId 'case-x' is not a declared owning case.");
    }

    // AC-005: a fixture whose digest no longer matches is refused without changing published state.
    [Fact]
    public void AC005_fixture_digest_mismatch_refuses_publication_without_state_change()
    {
        using var dir = new TempDirectory();
        WriteSet(dir.Path);

        var fixturePath = Path.Combine(dir.Path, "technical.json");
        File.AppendAllText(fixturePath, "\n");

        var seed = new PublishedFixtureState(
            [new StoredFixturePack("2026.09.1", "aa")],
            [new StoredSourceRecord("technical", "2026.09.1", 0, "arr-core")],
            [new StoredBasisPin("technical", "2026.09.1", "aa")],
            [new StoredAuditEvent(1, "source-fixtures.published", "2026.09.1")],
            [new StoredCaseDatum("arr-core", "unrelated case data"), new StoredCaseDatum("case-a", "another case")]);
        var store = new SourceFixtureStore(seed);
        var before = Snapshot(store.State);

        var error = Assert.Throws<SourceFixtureRefusedException>(() => SourceFixtureLoader.LoadAndPublish(dir.Path, store));
        Assert.Equal("Source fixture 'technical' refused: fixture SHA-256 digest does not match the manifest.", error.Message);
        AssertSnapshot(before, store.State);
        Assert.Equal(seed.Packs, store.State.Packs);
        Assert.Equal(seed.SourceRecords, store.State.SourceRecords);
        Assert.Equal(seed.BasisPins, store.State.BasisPins);
        Assert.Equal(seed.AuditEvents, store.State.AuditEvents);
        Assert.Equal(seed.CaseData, store.State.CaseData);
    }

    [Fact]
    public void EveryFixtureDigestIsValidatedWithoutSignatures()
    {
        foreach (var source in Sources)
        {
            using var dir = new TempDirectory();
            CopyDirectory(CommittedSources(), dir.Path);
            File.AppendAllText(Path.Combine(dir.Path, $"{source}.json"), "\n");
            var error = Assert.Throws<SourceFixtureRefusedException>(() => SourceFixtureLoader.Load(dir.Path));
            Assert.Equal($"Source fixture '{source}' refused: fixture SHA-256 digest does not match the manifest.", error.Message);
        }
    }

    private static (IReadOnlyList<StoredFixturePack> Packs, IReadOnlyList<StoredSourceRecord> Records, IReadOnlyList<StoredBasisPin> Pins, IReadOnlyList<StoredAuditEvent> Audits, IReadOnlyList<StoredCaseDatum> Cases) Snapshot(PublishedFixtureState state) =>
        (state.Packs.ToList(), state.SourceRecords.ToList(), state.BasisPins.ToList(), state.AuditEvents.ToList(), state.CaseData.ToList());

    private static void AssertSnapshot(
        (IReadOnlyList<StoredFixturePack> Packs, IReadOnlyList<StoredSourceRecord> Records, IReadOnlyList<StoredBasisPin> Pins, IReadOnlyList<StoredAuditEvent> Audits, IReadOnlyList<StoredCaseDatum> Cases) expected,
        PublishedFixtureState actual)
    {
        Assert.Equal(expected.Packs, actual.Packs);
        Assert.Equal(expected.Records, actual.SourceRecords);
        Assert.Equal(expected.Pins, actual.BasisPins);
        Assert.Equal(expected.Audits, actual.AuditEvents);
        Assert.Equal(expected.Cases, actual.CaseData);
    }

    private static void AssertRefused(string directory, string reason) =>
        Assert.Equal($"Source fixture 'technical' refused: {reason}",
            Assert.Throws<SourceFixtureRefusedException>(() => SourceFixtureLoader.Load(directory)).Message);

    private static void WriteSet(
        string directory,
        string schemaVersion = "1",
        string scenarioDate = "2026-10-01",
        string? digest = null,
        int declaredCount = 4,
        string[]? recordCaseIds = null,
        string sourceFixtureVersion = "2026.10.1")
    {
        var caseIds = recordCaseIds ?? OwningCaseIds;
        foreach (var source in Sources)
        {
            var records = caseIds.Select(caseId => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["caseId"] = caseId,
            }).ToArray();
            if (source == "records")
            {
                foreach (var record in records)
                {
                    record["payload"] = new { containsPersonalData = false };
                }
            }

            var fixture = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                sourceId = source,
                records,
            }));
            var manifest = ManifestBytes(fixture, schemaVersion, scenarioDate, digest, declaredCount, sourceFixtureVersion);

            File.WriteAllBytes(Path.Combine(directory, $"{source}.json"), fixture);
            File.WriteAllBytes(Path.Combine(directory, $"{source}.manifest.json"), manifest);
        }
    }

    private static byte[] ManifestBytes(
        byte[] fixture,
        string schemaVersion = "1",
        string scenarioDate = "2026-10-01",
        string? digest = null,
        int declaredCount = 4,
        string sourceFixtureVersion = "2026.10.1") => JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion,
            sourceFixtureVersion,
            scenarioDate,
            owningCaseIds = OwningCaseIds,
            sha256 = digest ?? Convert.ToHexString(SHA256.HashData(fixture)).ToLowerInvariant(),
            recordCounts = Sources.ToDictionary(source => source, _ => declaredCount),
        });

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
