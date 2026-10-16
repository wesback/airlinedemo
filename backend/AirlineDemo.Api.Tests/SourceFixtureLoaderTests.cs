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

    private static readonly string[] Cases = ["case-001", "case-a", "case-b", "case-c"];

    private sealed class Dir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory().FullName;

        public void Dispose() => Directory.Delete(Path, true);
    }

    private static byte[] FixtureBytes(string source, string? owningCase = null)
    {
        var records = Cases.Select(c => new Dictionary<string, object>
        {
            ["recordId"] = $"{source}-{c}",
            ["recordVersion"] = 1,
            ["asOf"] = "2026-10-01",
            ["caseId"] = c,
            ["kind"] = "k",
            ["synthetic"] = true,
            ["payload"] = new Dictionary<string, string> { ["a"] = "b" },
        }).ToList();
        if (owningCase is not null)
        {
            records[0]["owningCaseId"] = owningCase;
        }

        return JsonSerializer.SerializeToUtf8Bytes(new { sourceId = source, records });
    }

    private static byte[] ManifestBytes(string source, byte[] fixture, string schema = "1", string date = "2026-10-01", string? digest = null, int count = 4)
    {
        var counts = SourceFixtureManifest.RequiredSources.ToDictionary(x => x, x => x == source ? count : 0);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = schema,
            sourceFixtureVersion = "2026.10.1",
            scenarioDate = date,
            sha256 = digest ?? Convert.ToHexString(SHA256.HashData(fixture)).ToLowerInvariant(),
            recordCounts = counts,
        });
    }

    private static void Write(Dir dir, string source, byte[] fixture, byte[] manifest, RSA key)
    {
        File.WriteAllBytes(System.IO.Path.Combine(dir.Path, $"{source}.json"), fixture);
        File.WriteAllBytes(System.IO.Path.Combine(dir.Path, $"{source}.manifest.json"), manifest);
        File.WriteAllText(
            System.IO.Path.Combine(dir.Path, $"{source}.manifest.sig"),
            Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    }

    private static Dir Build(RSA key, Action<string, Dir, RSA>? tweak = null)
    {
        var dir = new Dir();
        foreach (var source in SourceFixtureManifest.RequiredSources)
        {
            var fixture = FixtureBytes(source);
            Write(dir, source, fixture, ManifestBytes(source, fixture), key);
            tweak?.Invoke(source, dir, key);
        }

        return dir;
    }

    private static SourceFixtureLoader LoaderFor(RSA key) => new(key.ExportSubjectPublicKeyInfo());

    // AC-004
    [Fact]
    public void ValidManifestLoadsAfterSignatureAndManifestValidation()
    {
        using var key = RSA.Create(3072);
        using var dir = Build(key);

        var set = LoaderFor(key).Load(dir.Path);

        Assert.Equal("2026.10.1", set.SourceFixtureVersion);
        Assert.Equal(20, set.Records.Count);
        foreach (var source in SourceFixtureManifest.RequiredSources)
        {
            Assert.Equal(Cases, set.Records.Where(r => r.SourceId == source).Select(r => r.CaseId).ToArray());
        }
    }

    // AC-005
    [Theory]
    [InlineData("signature")]
    [InlineData("schema")]
    [InlineData("date")]
    [InlineData("digest")]
    [InlineData("count")]
    public void InvalidSignatureSchemaDateDigestOrCountIsRefused(string defect)
    {
        using var key = RSA.Create(3072);
        using var dir = Build(key, (source, d, k) =>
        {
            if (source != "finance")
            {
                return;
            }

            var fixture = FixtureBytes(source);
            switch (defect)
            {
                case "signature":
                    File.WriteAllText(System.IO.Path.Combine(d.Path, "finance.manifest.sig"), Convert.ToBase64String(new byte[384]));
                    break;
                case "schema": Write(d, source, fixture, ManifestBytes(source, fixture, schema: "2"), k); break;
                case "date": Write(d, source, fixture, ManifestBytes(source, fixture, date: "2026-10-02"), k); break;
                case "digest": Write(d, source, fixture, ManifestBytes(source, fixture, digest: new string('0', 64)), k); break;
                default: Write(d, source, fixture, ManifestBytes(source, fixture, count: 3), k); break;
            }
        });

        Assert.Throws<SourceFixtureRefusedException>(() => LoaderFor(key).Load(dir.Path));
    }

    // AC-005
    [Theory]
    [InlineData("missing-signature")]
    [InlineData("missing-manifest")]
    [InlineData("missing-key")]
    [InlineData("other-key")]
    [InlineData("tampered-with-digest")]
    public void UntrustedOrMissingSignatureAndTrustKeyAreRefused(string defect)
    {
        using var key = RSA.Create(3072);
        using var other = RSA.Create(3072);
        using var dir = Build(key);
        var loader = LoaderFor(key);
        switch (defect)
        {
            case "missing-signature": File.Delete(System.IO.Path.Combine(dir.Path, "records.manifest.sig")); break;
            case "missing-manifest": File.Delete(System.IO.Path.Combine(dir.Path, "records.manifest.json")); break;
            case "missing-key": loader = new SourceFixtureLoader(null); break;
            case "other-key": loader = LoaderFor(other); break;
            default:
                var changed = Encoding.UTF8.GetBytes("{\"sourceId\":\"contracts\",\"records\":[]}");
                Write(dir, "contracts", changed, ManifestBytes("contracts", changed, count: 0), other);
                break;
        }

        Assert.Throws<SourceFixtureRefusedException>(() => loader.Load(dir.Path));
    }

    // AC-005
    [Fact]
    public void CaseOwnershipMismatchIsRefused()
    {
        using var key = RSA.Create(3072);
        using var dir = Build(key, (source, d, k) =>
        {
            if (source == "technical")
            {
                var fixture = FixtureBytes(source, owningCase: "case-b");
                Write(d, source, fixture, ManifestBytes(source, fixture), k);
            }
        });

        var ex = Assert.Throws<SourceFixtureRefusedException>(() => LoaderFor(key).Load(dir.Path));
        Assert.Equal("Source 'technical' record 'technical-case-001' caseId does not match its owning case.", ex.Message);
    }

    // AC-005, NFR-002
    [Fact]
    public void RejectedLoadLeavesPublishedStateUnchanged()
    {
        using var key = RSA.Create(3072);
        using var dir = Build(key);
        var records = new[] { new PublishedSourceRecord("technical", "t-1", 1, "case-001") };
        var pins = new[] { new SourceBasisPin("technical", "2026.9.0", "abc") };
        var audit = new[] { new SourceAuditEvent("source_fixture_published", "2026.9.0", "abc") };
        var publication = new SourceFixturePublication();
        publication.Seed(records, pins, audit);
        publication.BeforeCommit = () => throw new InvalidOperationException("injected");

        Assert.Throws<InvalidOperationException>(() => publication.Publish(LoaderFor(key).Load(dir.Path)));

        Assert.Equal(records, publication.Records);
        Assert.Equal(pins, publication.Pins);
        Assert.Equal(audit, publication.AuditEvents);
    }

    // AC-005, NFR-002
    [Fact]
    public void ReusedVersionWithDifferentContentIsRefused()
    {
        using var key = RSA.Create(3072);
        using var dir = Build(key);
        var publication = new SourceFixturePublication();
        var loader = LoaderFor(key);
        Assert.True(publication.Publish(loader.Load(dir.Path)));
        var records = publication.Records;
        var pins = publication.Pins;
        var audit = publication.AuditEvents;

        Assert.False(publication.Publish(loader.Load(dir.Path)));
        Assert.Equal(records, publication.Records);
        Assert.Equal(audit, publication.AuditEvents);

        var changed = FixtureBytes("finance").Concat(" "u8.ToArray()).ToArray();
        Write(dir, "finance", changed, ManifestBytes("finance", changed), key);
        Assert.Throws<SourceFixtureRefusedException>(() => publication.Publish(loader.Load(dir.Path)));

        Assert.Equal(records, publication.Records);
        Assert.Equal(pins, publication.Pins);
        Assert.Equal(audit, publication.AuditEvents);
    }
}
