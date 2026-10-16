using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Tests;

public sealed class SourceFixtureLoaderTests : IDisposable
{
    private const string Version = "2026.10.1";
    private const string ScenarioDate = "2026-10-16";
    private static readonly string[] Cases = ["arr-core", "case-a", "case-b", "case-c"];

    // Test-generated key pair supplied through the loader's constructor seam.
    private static readonly RSA TrustKey = RSA.Create(2048);
    private static readonly RSA OtherKey = RSA.Create(2048);
    private static readonly string TrustPem = TrustKey.ExportSubjectPublicKeyInfoPem();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sfl-" + Guid.NewGuid().ToString("N"));
    private int _sequence;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ValidManifestLoadsAfterSignatureAndManifestValidation()
    {
        var dir = NewDirectory();
        var files = SourceFiles();
        WriteSet(dir, Manifest(files), files, TrustKey);

        var store = new SourcePublicationStore();
        var result = Loader().Load(dir, store);

        Assert.Equal(SourceFixtureLoadStatus.Published, result.Status);
        Assert.Empty(result.Reasons);
        var snapshot = store.Snapshot();
        Assert.Equal(SourceFixtureContract.Sources.Count * Cases.Length, snapshot.Records.Count);
        Assert.Equal(Cases.Length, snapshot.BasisPins.Count);
        Assert.All(snapshot.BasisPins, pin => Assert.Equal(Version, pin.SourceFixtureVersion));
        Assert.Single(snapshot.AuditEvents);
    }

    [Fact]
    public void InvalidSignatureSchemaDateDigestOrCountIsRefused()
    {
        Assert.Equal(
            SourceFixtureLoadStatus.Refused,
            LoadMutated(files => File.WriteAllText(
                Path.Combine(files, "manifest.sig"),
                Convert.ToBase64String(new byte[256]))).Status);

        AssertRefusedWithManifest(m => m["schemaVersion"] = 2);
        AssertRefusedWithManifest(m => m["scenarioDate"] = "2026-10-17");
        AssertRefusedWithManifest(m => Entry(m, "technical")["sha256"] = new string('0', 64));
        AssertRefusedWithManifest(m => Entry(m, "technical")["recordCounts"]!["case-a"] = 2);
    }

    [Fact]
    public void UntrustedOrMissingSignatureAndTrustKeyAreRefused()
    {
        var missingSignature = NewDirectory();
        var files = SourceFiles();
        WriteSet(missingSignature, Manifest(files), files, signer: null);
        AssertRefused(Loader().Load(missingSignature, new SourcePublicationStore()), "manifest.sig is missing");

        var missingTrustKey = NewDirectory();
        WriteSet(missingTrustKey, Manifest(files), files, TrustKey);
        var noKey = new SourceFixtureLoader(null, SourceFixtureContract.ScenarioDate);
        AssertRefused(noKey.Load(missingTrustKey, new SourcePublicationStore()), "no trust key");

        var otherKey = NewDirectory();
        WriteSet(otherKey, Manifest(files), files, OtherKey);
        AssertRefused(Loader().Load(otherKey, new SourcePublicationStore()), "signature");

        var tampered = NewDirectory();
        WriteSet(tampered, Manifest(files), files, TrustKey);
        var changed = new Dictionary<string, byte[]>(files)
        {
            ["technical"] = Encoding.UTF8.GetBytes("[]"),
        };
        var manifest = Manifest(changed);
        File.WriteAllBytes(Path.Combine(tampered, "technical.json"), changed["technical"]);
        File.WriteAllBytes(Path.Combine(tampered, "manifest.json"), Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        var store = new SourcePublicationStore();
        AssertRefused(Loader().Load(tampered, store), "signature");
        Assert.Empty(store.Snapshot().Records);
    }

    [Fact]
    public void CaseOwnershipMismatchIsRefused()
    {
        var dir = NewDirectory();
        var files = SourceFiles();
        var technical = JsonNode.Parse(files["technical"])!.AsArray();
        technical[0]!["caseId"] = "case-z";
        files["technical"] = Encoding.UTF8.GetBytes(technical.ToJsonString());
        WriteSet(dir, Manifest(files), files, TrustKey);

        var store = new SourcePublicationStore();
        var result = Loader().Load(dir, store);

        AssertRefused(result, "case-z");
        Assert.Empty(store.Snapshot().Records);
    }

    [Fact]
    public void RejectedLoadLeavesPublishedStateUnchanged()
    {
        var store = new SourcePublicationStore();
        var pre = NewDirectory();
        var preFiles = SourceFiles();
        WriteSet(pre, Manifest(preFiles, "2026.09.1"), preFiles, TrustKey);
        Assert.Equal(SourceFixtureLoadStatus.Published, Loader().Load(pre, store).Status);
        var before = store.Snapshot();

        var next = NewDirectory();
        var nextFiles = SourceFiles();
        WriteSet(next, Manifest(nextFiles), nextFiles, TrustKey);
        store.BeforeCommit = () => throw new IOException("injected publication failure");

        AssertRefused(Loader().Load(next, store), "publication failed");
        var after = store.Snapshot();
        Assert.Equal(before.Records, after.Records);
        Assert.Equal(before.BasisPins, after.BasisPins);
        Assert.Equal(before.AuditEvents, after.AuditEvents);
    }

    [Fact]
    public void ReusedVersionWithDifferentContentIsRefused()
    {
        var store = new SourcePublicationStore();
        var first = NewDirectory();
        var files = SourceFiles();
        WriteSet(first, Manifest(files), files, TrustKey);
        Assert.Equal(SourceFixtureLoadStatus.Published, Loader().Load(first, store).Status);
        var before = store.Snapshot();

        Assert.Equal(SourceFixtureLoadStatus.AlreadyPublished, Loader().Load(first, store).Status);
        Assert.Equal(before.Records, store.Snapshot().Records);

        var changedDir = NewDirectory();
        var changed = SourceFiles();
        changed["technical"] = Encoding.UTF8.GetBytes(
            JsonNode.Parse(changed["technical"])!.AsArray().ToJsonString().Replace("\"fixture\"", "\"changed\""));
        WriteSet(changedDir, Manifest(changed), changed, TrustKey);

        AssertRefused(Loader().Load(changedDir, store), "already published with different content");
        var after = store.Snapshot();
        Assert.Equal(before.Records, after.Records);
        Assert.Equal(before.BasisPins, after.BasisPins);
        Assert.Equal(before.AuditEvents, after.AuditEvents);
    }

    private SourceFixtureLoader Loader() => new(TrustPem, SourceFixtureContract.ScenarioDate);

    private string NewDirectory() => Path.Combine(_root, (++_sequence).ToString());

    private SourceFixtureLoadResult LoadMutated(Action<string> mutateDirectory)
    {
        var dir = NewDirectory();
        var files = SourceFiles();
        WriteSet(dir, Manifest(files), files, TrustKey);
        mutateDirectory(dir);
        return Loader().Load(dir, new SourcePublicationStore());
    }

    private void AssertRefusedWithManifest(Action<JsonObject> mutate)
    {
        var dir = NewDirectory();
        var files = SourceFiles();
        var manifest = Manifest(files);
        mutate(manifest);
        WriteSet(dir, manifest, files, TrustKey);

        var store = new SourcePublicationStore();
        AssertRefused(Loader().Load(dir, store), "");
        Assert.Empty(store.Snapshot().Records);
    }

    private static void AssertRefused(SourceFixtureLoadResult result, string reasonFragment)
    {
        Assert.Equal(SourceFixtureLoadStatus.Refused, result.Status);
        Assert.NotEmpty(result.Reasons);
        Assert.Contains(result.Reasons, reason => reason.Contains(reasonFragment, StringComparison.Ordinal));
    }

    private static Dictionary<string, byte[]> SourceFiles() =>
        SourceFixtureContract.Sources.ToDictionary(source => source, source => RecordsFor(source));

    private static byte[] RecordsFor(string source)
    {
        var records = new JsonArray();
        foreach (var caseId in Cases)
        {
            records.Add(new JsonObject
            {
                ["sourceId"] = source,
                ["recordId"] = $"{source}-{caseId}-1",
                ["recordVersion"] = 1,
                ["asOf"] = ScenarioDate,
                ["caseId"] = caseId,
                ["kind"] = "fixture",
                ["stale"] = false,
                ["payload"] = new JsonObject { ["source"] = source },
                ["synthetic"] = true,
            });
        }

        return Encoding.UTF8.GetBytes(records.ToJsonString());
    }

    private static JsonObject Manifest(IReadOnlyDictionary<string, byte[]> files, string version = Version)
    {
        var entries = new JsonArray();
        foreach (var (source, bytes) in files)
        {
            var counts = new JsonObject();
            foreach (var caseId in Cases)
            {
                counts[caseId] = 1;
            }

            entries.Add(new JsonObject
            {
                ["source"] = source,
                ["fileName"] = $"{source}.json",
                ["sha256"] = SourceFixtureLoader.Sha256Hex(bytes),
                ["recordCounts"] = counts,
            });
        }

        return new JsonObject
        {
            ["schemaVersion"] = SourceFixtureContract.SupportedSchemaVersion,
            ["sourceFixtureVersion"] = version,
            ["scenarioDate"] = ScenarioDate,
            ["files"] = entries,
        };
    }

    private static JsonObject Entry(JsonObject manifest, string source) =>
        manifest["files"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(entry => entry["source"]!.GetValue<string>() == source);

    private static void WriteSet(
        string dir,
        JsonObject manifest,
        IReadOnlyDictionary<string, byte[]> files,
        RSA? signer)
    {
        Directory.CreateDirectory(dir);
        foreach (var (source, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(dir, $"{source}.json"), bytes);
        }

        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        File.WriteAllBytes(Path.Combine(dir, "manifest.json"), manifestBytes);
        if (signer is not null)
        {
            var signature = signer.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            File.WriteAllText(Path.Combine(dir, "manifest.sig"), Convert.ToBase64String(signature));
        }
    }
}
