using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Fixtures;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Tests;

public sealed class AircraftCasePackPublicationTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed class RecordingEvidenceStore : IEvidenceBlobStore
    {
        public readonly List<EvidenceBlobVersion> Writes = [];
        public int Calls { get; private set; }
        public int? FailAfterWriteCall { get; set; }

        public Task<EvidenceBlobVersion> StoreAsync(string documentId, byte[] content, CancellationToken cancellationToken = default)
        {
            Calls++;
            var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            var prior = Writes.Where(write => write.Name.StartsWith(documentId + "/", StringComparison.Ordinal)).ToArray();
            var match = prior.FirstOrDefault(write => write.Sha256 == digest);
            if (match is not null) return Task.FromResult(match);
            var version = prior.Select(write => write.Version).DefaultIfEmpty(0).Max() + 1;
            var blob = new EvidenceBlobVersion($"{documentId}/v{version}-{digest}", digest, version);
            Writes.Add(blob);
            if (Calls == FailAfterWriteCall) throw new IOException("injected evidence write failure");
            return Task.FromResult(blob);
        }
        public Task<byte[]?> ReadAsync(string name, string expectedSha256, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
    }
    private sealed class RecordingCaseStore : IAircraftCasePackSnapshotStore
    {
        private AircraftCasePackSnapshot? _snapshot;
        private long _revision;
        public int Commits { get; private set; }
        public bool ChangeRevisionAtNextCommit { get; set; }
        public AircraftCasePackSnapshot? Read() => _snapshot;
        public bool TryCommit(long expectedRevision, AircraftCasePackSnapshot snapshot)
        {
            Commits++;
            if (ChangeRevisionAtNextCommit) { ChangeRevisionAtNextCommit = false; _revision++; }
            if (_revision != expectedRevision) return false;
            _snapshot = snapshot; _revision = snapshot.Revision; return true;
        }
    }
    private sealed class FailingCaseStager : IAircraftCasePackCaseStager
    {
        private readonly AircraftCasePackCaseStager _inner = new();
        public string? FailCaseId { get; set; }
        public PublishedAircraftCasePackCase Stage(AircraftCasePackCase record, IReadOnlyDictionary<string, EvidenceBlobVersion> documentBlobs)
        {
            if (record.CaseId == FailCaseId) throw new InvalidOperationException("injected case staging failure");
            return _inner.Stage(record, documentBlobs);
        }
    }
    [Fact]
    public async Task InvalidManifestIsRejectedBeforeAnyStoreWrite()
    {
        var fixture = LoadFixture();
        var rows = new (string Name, Action<JsonObject> Change, string Error)[]
        {
            ("schema version 2", json => json["schemaVersion"] = 2, "schemaVersion: must be integer 1"),
            ("two cases", json => json["cases"]!.AsArray().RemoveAt(2), "cases: must contain exactly 3 cases"),
            ("four cases", json => json["cases"]!.AsArray().Add(json["cases"]![0]!.DeepClone()), "cases: must contain exactly 3 cases"),
            ("seven documents", json => json["cases"]![0]!["documents"]!.AsArray().RemoveAt(7), "cases[0].documents: must contain 8 through 12 documents"),
            ("thirteen documents", json => { for (var i = 0; i < 5; i++) json["cases"]![0]!["documents"]!.AsArray().Add(json["cases"]![0]!["documents"]![0]!.DeepClone()); }, "cases[0].documents: must contain 8 through 12 documents"),
            ("total 23 documents", json => json["cases"]![0]!["documents"]!.AsArray().RemoveAt(0), "cases.documents: total must be 24 through 36"),
            ("total 37 documents", json => { for (var i = 0; i < 13; i++) json["cases"]![0]!["documents"]!.AsArray().Add(json["cases"]![0]!["documents"]![0]!.DeepClone()); }, "cases.documents: total must be 24 through 36"),
            ("stale hash", json => json["sha256"] = new string('0', 64), "sha256: does not match manifest and file contents"),
            ("false case synthetic marker", json => json["cases"]![0]!["synthetic"] = false, "cases[0].synthetic: must be true"),
            ("false document synthetic marker", json => json["cases"]![0]!["documents"]![0]!["synthetic"] = false, "cases[0].documents[0].synthetic: must be true"),
            ("blank case generator reference", json => json["cases"]![0]!["generatorReference"] = " ", "cases[0].generatorReference: must be non-empty"),
            ("blank document generator reference", json => json["cases"]![0]!["documents"]![0]!["generatorReference"] = "", "cases[0].documents[0].generatorReference: must be non-empty"),
            ("blank product-owner review reference", json => json["productOwnerReviewReference"] = "", "productOwnerReviewReference: must be non-empty"),
            ("document path absent from files", json => json["cases"]![0]!["documents"]![0]!["path"] = "case-a/not-listed.txt", "cases[0].documents[0].path: must be listed in files")
        };
        foreach (var row in rows)
        {
            var json = JsonNode.Parse(fixture.Json)!.AsObject(); row.Change(json);
            var evidence = new RecordingEvidenceStore(); var cases = new RecordingCaseStore();
            var publisher = new AircraftCasePackPublisher(evidence, cases, new AircraftCasePackCaseStager());
            var exception = await Assert.ThrowsAsync<AircraftCasePackValidationException>(() =>
                publisher.PublishAsync(() => new AircraftCasePackLoader(FixtureRoot()).Load(json.ToJsonString(), fixture.Files), 0));
            Assert.Contains(row.Error, exception.Errors);
            Assert.Equal(0, evidence.Calls);
            Assert.Equal(0, cases.Commits);
            Assert.Null(cases.Read());
        }
    }

    [Fact]
    public async Task FailedPublicationDoesNotExposePartialPack()
    {
        foreach (var failure in new[] { "evidence", "staging" })
        {
            var fixture = LoadFixture(); var evidence = new RecordingEvidenceStore(); var cases = new RecordingCaseStore();
            var stager = new FailingCaseStager();
            var publisher = new AircraftCasePackPublisher(evidence, cases, stager);
            var prior = await publisher.PublishAsync(new AircraftCasePackLoader(FixtureRoot()), 0);
            var priorBlobCount = evidence.Writes.Count;
            var unrelatedPath = Path.Combine(Path.GetTempPath(), $"aircraft-pack-unrelated-{Guid.NewGuid():N}.json");
            var unrelated = new CaseStateStore(unrelatedPath); var unrelatedScope = unrelated.CreateCase("unrelated-case", "presenter");
            unrelated.Commit(unrelatedScope, 0, new CaseCommit(
                CaseStateStore.CreateBasisVersion(1, "unrelated basis", ""), null, "presenter", "seed", "unrelated"));
            var unrelatedBefore = unrelated.Read(unrelatedScope);
            var changed = CreateChangedFixture(fixture, "1.0.2");

            if (failure == "evidence") evidence.FailAfterWriteCall = evidence.Calls + 1;
            else stager.FailCaseId = "cp-case-a";
            var publishError = failure == "evidence" ? typeof(IOException) : typeof(InvalidOperationException);
            await Assert.ThrowsAsync(publishError, () =>
                publisher.PublishAsync(new AircraftCasePackLoader(changed.Root), 1));

            Assert.Same(prior, cases.Read());
            Assert.Equal(1, cases.Commits);
            Assert.Equal("1.0.1", cases.Read()!.PackVersion);
            Assert.Equal(unrelatedBefore, unrelated.Read(unrelatedScope));
            var visibleBlobNames = cases.Read()!.Cases.SelectMany(item => item.DocumentReferences)
                .Select(item => item.BlobName).ToHashSet(StringComparer.Ordinal);
            Assert.NotEmpty(evidence.Writes.Skip(priorBlobCount));
            Assert.All(evidence.Writes.Skip(priorBlobCount), blob => Assert.DoesNotContain(blob.Name, visibleBlobNames));

            evidence.FailAfterWriteCall = null;
            stager.FailCaseId = null;
            var retry = await publisher.PublishAsync(new AircraftCasePackLoader(changed.Root), 1);
            Assert.Equal("1.0.2", retry.PackVersion);
            Assert.Equal(2, cases.Commits);
            Assert.Equal(3, retry.Cases.Count);
            foreach (var path in new[] { unrelatedPath, unrelatedPath + ".lock", unrelatedPath + ".tmp" }) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(changed.Root, recursive: true);
        }
    }

    [Fact]
    public async Task RetryIsIdempotentAndChangedSameVersionIsRejected()
    {
        var fixture = LoadFixture(); var evidence = new RecordingEvidenceStore(); var cases = new RecordingCaseStore();
        var publisher = new AircraftCasePackPublisher(evidence, cases, new AircraftCasePackCaseStager());
        var initial = await publisher.PublishAsync(new AircraftCasePackLoader(FixtureRoot()), 0);
        Assert.Equal("1.0.1", initial.PackVersion);
        Assert.Equal(fixture.Manifest.Sha256, initial.Sha256);
        Assert.Equal(3, initial.Cases.Count);
        Assert.Equal(24, initial.Cases.Sum(item => item.DocumentReferences.Count));
        Assert.Throws<NotSupportedException>(() => ((IList<PublishedAircraftCasePackCase>)initial.Cases).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PublishedAircraftCasePackComponent>)initial.Cases[0].Components).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)initial.Cases[0].Components[0].EvidenceDocumentPaths).Add("forged-path"));
        var writeCount = evidence.Writes.Count;
        var replay = await publisher.PublishAsync(new AircraftCasePackLoader(FixtureRoot()), 1);
        Assert.Same(initial, replay);
        Assert.Equal(writeCount, evidence.Writes.Count);
        Assert.Equal(1, cases.Commits);

        var changed = CreateChangedFixture(fixture, "1.0.1");
        var exception = await Assert.ThrowsAsync<AircraftCasePackValidationException>(() =>
            publisher.PublishAsync(new AircraftCasePackLoader(changed.Root), 1));
        Assert.Equal(["packVersion: must change when canonical content changes"], exception.Errors);
        Assert.Same(initial, cases.Read());
        Assert.Equal(writeCount, evidence.Writes.Count);
        Assert.Equal(1, cases.Commits);
        Directory.Delete(changed.Root, recursive: true);
    }
    [Fact]
    public async Task PublicationRevisionRaceDoesNotExposeCandidate()
    {
        var fixture = LoadFixture(); var evidence = new RecordingEvidenceStore(); var cases = new RecordingCaseStore();
        var publisher = new AircraftCasePackPublisher(evidence, cases, new AircraftCasePackCaseStager());
        var prior = await publisher.PublishAsync(new AircraftCasePackLoader(FixtureRoot()), 0);
        var priorBlobCount = evidence.Writes.Count;
        var changed = CreateChangedFixture(fixture, "1.0.2");
        cases.ChangeRevisionAtNextCommit = true;

        await Assert.ThrowsAsync<AircraftCasePackRevisionConflictException>(() =>
            publisher.PublishAsync(new AircraftCasePackLoader(changed.Root), 1));
        Assert.Same(prior, cases.Read());
        Assert.Equal("1.0.1", cases.Read()!.PackVersion);
        Assert.Equal(2, cases.Commits);
        var visibleBlobNames = cases.Read()!.Cases.SelectMany(item => item.DocumentReferences)
            .Select(item => item.BlobName).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(evidence.Writes.Skip(priorBlobCount));
        Assert.All(evidence.Writes.Skip(priorBlobCount), blob => Assert.DoesNotContain(blob.Name, visibleBlobNames));
        Directory.Delete(changed.Root, recursive: true);
    }
    [Fact]
    public void VersionComparisonRejectsChangedSameVersionAndEvaluationIds()
    {
        var fixture = LoadFixture();
        Assert.Empty(AircraftCasePackPublisher.CompareVersions(fixture.Manifest, fixture.Manifest, fixture.Files, fixture.Files));

        var changedFiles = CopyFiles(fixture.Files);
        changedFiles[fixture.Manifest.Files[0]] = Encoding.UTF8.GetBytes("changed synthetic evidence");
        var sameVersion = fixture.Manifest with { Sha256 = AircraftCasePackManifest.ComputeSha256(fixture.Manifest, changedFiles) };
        Assert.Equal(["packVersion: must change when canonical content changes"],
            AircraftCasePackPublisher.CompareVersions(fixture.Manifest, sameVersion, fixture.Files, changedFiles));
        var newVersion = sameVersion with { PackVersion = "1.0.2", Sha256 = "" };
        newVersion = newVersion with { Sha256 = AircraftCasePackManifest.ComputeSha256(newVersion, changedFiles) };
        Assert.Empty(AircraftCasePackPublisher.CompareVersions(fixture.Manifest, newVersion, fixture.Files, changedFiles));

        var suiteIds = Enumerable.Range(1, 40).Select(number => $"EVAL-V1-{number:00}").Concat(Enumerable.Range(1, 40).Select(number => $"EVAL-V2-{number:00}"));
        foreach (var id in suiteIds) Assert.Equal([$"{id}: evaluation-suite case identifiers are not part of the pack"], AircraftCasePackPublisher.ValidateCaseIdentifiers([id]));
        foreach (var id in new[] { "cp-case-a", "cp-case-b", "cp-case-c" }) Assert.Empty(AircraftCasePackPublisher.ValidateCaseIdentifiers([id]));
    }

    private sealed record Fixture(string Json, AircraftCasePackManifest Manifest, Dictionary<string, byte[]> Files);
    private static Fixture LoadFixture()
    {
        var root = FixtureRoot(); var json = File.ReadAllText(Path.Combine(root, "manifest.json"));
        var manifest = JsonSerializer.Deserialize<AircraftCasePackManifest>(json, WebJson)!;
        var files = manifest.Files.ToDictionary(path => path, path => File.ReadAllBytes(
            Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))), StringComparer.Ordinal);
        return new Fixture(json, manifest, files);
    }
    private static (string Root, AircraftCasePackManifest Manifest, Dictionary<string, byte[]> Files) CreateChangedFixture(
        Fixture fixture,
        string version)
    {
        var root = Directory.CreateTempSubdirectory("aircraft-case-pack-publish-").FullName;
        var files = CopyFiles(fixture.Files);
        var changedPath = fixture.Manifest.Files[0];
        files[changedPath] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files[changedPath]) + "\nchanged");
        var manifest = fixture.Manifest with { PackVersion = version, Sha256 = "" };
        manifest = manifest with { Sha256 = AircraftCasePackManifest.ComputeSha256(manifest, files) };
        foreach (var path in manifest.Files)
        {
            var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, files[path]);
        }
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest, WebJson));
        return (root, manifest, files);
    }
    private static Dictionary<string, byte[]> CopyFiles(IReadOnlyDictionary<string, byte[]> files) =>
        files.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone(), StringComparer.Ordinal);
    private static string FixtureRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "AirlineDemo.Api", "Fixtures", "aircraft-case-pack"));
}
