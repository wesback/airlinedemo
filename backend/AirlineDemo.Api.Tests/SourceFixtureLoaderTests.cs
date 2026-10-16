using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.SourceSystems;

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

    // AC-004: all five sources load only when every detached signature and manifest check passes.
    [Fact]
    public void ValidManifestLoadsAfterSignatureAndManifestValidation()
    {
        using var key = RSA.Create(2048);
        using var dir = new TempDirectory();
        WriteSet(dir.Path, key);

        var loaded = SourceFixtureLoader.Load(dir.Path, key.ExportSubjectPublicKeyInfo());

        Assert.Equal(5, loaded.Count);
        foreach (var source in Sources)
        {
            Assert.Equal("2026.10.1", loaded[source].Manifest.SourceFixtureVersion);
            Assert.Equal(4, loaded[source].Manifest.RecordCounts[source]);
        }
    }

    // AC-005: an invalid signature, unsupported schema, wrong scenario date, wrong digest or wrong count refuses registration.
    [Fact]
    public void InvalidSignatureSchemaDateDigestOrCountIsRefused()
    {
        using var key = RSA.Create(2048);

        using (var dir = new TempDirectory())
        {
            WriteSet(dir.Path, key);
            var signaturePath = Path.Combine(dir.Path, "technical.manifest.sig");
            var signature = File.ReadAllBytes(signaturePath);
            signature[0] ^= 0xFF;
            File.WriteAllBytes(signaturePath, signature);
            AssertRefused(dir.Path, key);
        }

        using (var dir = new TempDirectory())
        {
            WriteSet(dir.Path, key, schemaVersion: "2");
            AssertRefused(dir.Path, key);
        }

        using (var dir = new TempDirectory())
        {
            WriteSet(dir.Path, key, scenarioDate: "2026-10-02");
            AssertRefused(dir.Path, key);
        }

        using (var dir = new TempDirectory())
        {
            WriteSet(dir.Path, key, digest: new string('0', 64));
            AssertRefused(dir.Path, key);
        }

        using (var dir = new TempDirectory())
        {
            WriteSet(dir.Path, key, declaredCount: 5);
            AssertRefused(dir.Path, key);
        }
    }

    // AC-005: a missing signature, a missing trust key, or a signature from another key refuses registration,
    // including when the fixture and its co-located digest are both changed without re-signing.
    [Fact]
    public void UntrustedOrMissingSignatureAndTrustKeyAreRefused()
    {
        using var key = RSA.Create(2048);

        using (var dir = new TempDirectory())
        {
            WriteSet(dir.Path, key);
            File.Delete(Path.Combine(dir.Path, "technical.manifest.sig"));
            AssertRefused(dir.Path, key);
        }

        using (var dir = new TempDirectory())
        {
            WriteSet(dir.Path, key);
            Assert.Throws<SourceFixtureRefusedException>(() => SourceFixtureLoader.Load(dir.Path, null));
        }

        using (var dir = new TempDirectory())
        {
            using var otherKey = RSA.Create(2048);
            WriteSet(dir.Path, otherKey);
            AssertRefused(dir.Path, key);
        }

        using (var dir = new TempDirectory())
        {
            WriteSet(dir.Path, key);
            var fixturePath = Path.Combine(dir.Path, "technical.json");
            var changedFixture = File.ReadAllBytes(fixturePath).Append((byte)'\n').ToArray();
            File.WriteAllBytes(fixturePath, changedFixture);
            File.WriteAllBytes(Path.Combine(dir.Path, "technical.manifest.json"), ManifestBytes(changedFixture));
            AssertRefused(dir.Path, key);
        }
    }

    // AC-005: a record whose caseId is not a declared owning case refuses registration.
    [Fact]
    public void CaseOwnershipMismatchIsRefused()
    {
        using var key = RSA.Create(2048);
        using var dir = new TempDirectory();
        WriteSet(dir.Path, key, recordCaseIds: ["arr-core", "case-a", "case-b", "case-x"]);

        AssertRefused(dir.Path, key);
    }

    // AC-004: a signed fixture set whose sourceFixtureVersion is empty is refused, even though its signature verifies.
    [Fact]
    public void UnsupportedSourceFixtureVersionIsRefused()
    {
        using var key = RSA.Create(2048);
        using var dir = new TempDirectory();
        WriteSet(dir.Path, key, sourceFixtureVersion: string.Empty);

        AssertRefused(dir.Path, key);
    }

    // AC-004: a signed fixture set whose sourceFixtureVersion is unknown (non-empty) is refused, even though its signature verifies.
    [Fact]
    public void UnknownNonEmptySourceFixtureVersionIsRefused()
    {
        using var key = RSA.Create(2048);
        using var dir = new TempDirectory();
        WriteSet(dir.Path, key, sourceFixtureVersion: "2026.09.9");

        AssertRefused(dir.Path, key);
    }

    // AC-005, NFR-002: a refused load (bad signature, or a publication failure after validation) leaves every published collection unchanged.
    [Fact]
    public void ValidationOrPublicationFailureLeavesPublishedStateUnchanged()
    {
        using var key = RSA.Create(2048);
        var spki = key.ExportSubjectPublicKeyInfo();
        using var validDir = new TempDirectory();
        using var badDir = new TempDirectory();
        WriteSet(validDir.Path, key);
        WriteSet(badDir.Path, key);
        var signaturePath = Path.Combine(badDir.Path, "technical.manifest.sig");
        var signature = File.ReadAllBytes(signaturePath);
        signature[0] ^= 0xFF;
        File.WriteAllBytes(signaturePath, signature);

        var seed = new PublishedFixtureState(
            [new StoredFixturePack("2026.09.1", "aa")],
            [new StoredSourceRecord("technical", "2026.09.1", 0, "arr-core")],
            [new StoredBasisPin("technical", "2026.09.1", "aa")],
            [new StoredAuditEvent(1, "source-fixtures.published", "2026.09.1")],
            [new StoredCaseDatum("arr-core", "unrelated case data")]);
        var store = new SourceFixtureStore(seed);
        var before = Snapshot(store.State);

        Assert.Throws<SourceFixtureRefusedException>(() => SourceFixtureLoader.LoadAndPublish(badDir.Path, spki, store));
        AssertSnapshot(before, store.State);

        Assert.Throws<InvalidOperationException>(() =>
            SourceFixtureLoader.LoadAndPublish(validDir.Path, spki, store, _ => throw new InvalidOperationException("publication failed")));
        AssertSnapshot(before, store.State);
    }

    // AC-005, NFR-002: the same stored version with the same digest is a no-op; with different content it is refused and the store is unchanged.
    [Fact]
    public void ReusedVersionWithDifferentContentIsRefused()
    {
        using var key = RSA.Create(2048);
        var spki = key.ExportSubjectPublicKeyInfo();
        using var validDir = new TempDirectory();
        using var changedDir = new TempDirectory();
        WriteSet(validDir.Path, key);
        WriteSet(changedDir.Path, key, recordCaseIds: ["arr-core", "case-a", "case-b", "arr-core"]);

        var store = new SourceFixtureStore(PublishedFixtureState.Empty);
        Assert.Equal(SourceFixturePublication.Published, SourceFixtureLoader.LoadAndPublish(validDir.Path, spki, store));
        var before = Snapshot(store.State);

        Assert.Equal(SourceFixturePublication.AlreadyPublished, SourceFixtureLoader.LoadAndPublish(validDir.Path, spki, store));
        AssertSnapshot(before, store.State);

        Assert.Throws<SourceFixtureRefusedException>(() => SourceFixtureLoader.LoadAndPublish(changedDir.Path, spki, store));
        AssertSnapshot(before, store.State);
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

    private static void AssertRefused(string directory, RSA trustKey) =>
        Assert.Throws<SourceFixtureRefusedException>(() => SourceFixtureLoader.Load(directory, trustKey.ExportSubjectPublicKeyInfo()));

    private static void WriteSet(
        string directory,
        RSA signingKey,
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
            var fixture = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                sourceId = source,
                records = caseIds.Select(caseId => new { caseId }),
            }));
            var manifest = ManifestBytes(fixture, schemaVersion, scenarioDate, digest, declaredCount, sourceFixtureVersion);

            File.WriteAllBytes(Path.Combine(directory, $"{source}.json"), fixture);
            File.WriteAllBytes(Path.Combine(directory, $"{source}.manifest.json"), manifest);
            File.WriteAllBytes(
                Path.Combine(directory, $"{source}.manifest.sig"),
                signingKey.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
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
