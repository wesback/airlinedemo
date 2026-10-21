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

    private sealed record ApplicationVisibleState(AircraftCasePackSnapshot? Pack);

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
        private ApplicationVisibleState _visibleState;
        private long _revision;

        public RecordingCaseStore() => _visibleState = new ApplicationVisibleState(null);

        public int Commits { get; private set; }
        public int Reads { get; private set; }
        public bool ChangeRevisionAtNextCommit { get; set; }
        public bool PublishInterveningSnapshotOnRace { get; set; }
        public AircraftCasePackSnapshot? InterveningSnapshot { get; private set; }
        public ApplicationVisibleState? InterveningApplicationState { get; private set; }
        public long Revision => _revision;
        public AircraftCasePackSnapshot? VisibleSnapshot => _visibleState.Pack;
        public AircraftCasePackSnapshot? Read() { Reads++; return _visibleState.Pack; }
        public ApplicationVisibleState ReadApplicationState() => _visibleState;
        public bool TryCommit(long expectedRevision, AircraftCasePackSnapshot snapshot)
        {
            Commits++;
            if (ChangeRevisionAtNextCommit)
            {
                ChangeRevisionAtNextCommit = false;
                _revision++;
                if (PublishInterveningSnapshotOnRace && _visibleState.Pack is not null)
                    _visibleState = _visibleState with
                    {
                        Pack = new AircraftCasePackSnapshot(
                            _revision, _visibleState.Pack.Manifest, _visibleState.Pack.Files, _visibleState.Pack.Cases)
                    };
                InterveningSnapshot = _visibleState.Pack;
                InterveningApplicationState = _visibleState;
            }
            if (_revision != expectedRevision) return false;
            _visibleState = _visibleState with { Pack = snapshot };
            _revision = snapshot.Revision;
            return true;
        }
    }
    private sealed class RecordingCaseStager : IAircraftCasePackCaseStager
    {
        private readonly AircraftCasePackCaseStager _inner = new();
        public int Calls { get; private set; }
        public PublishedAircraftCasePackCase Stage(AircraftCasePackCase record, IReadOnlyDictionary<string, EvidenceBlobVersion> documentBlobs)
        {
            Calls++;
            return _inner.Stage(record, documentBlobs);
        }
        public PublishedAircraftCasePackCase Stage(
            AircraftCasePackCase record,
            IReadOnlyDictionary<string, AircraftCasePackDocumentReference> documentReferences)
        {
            Calls++;
            return _inner.Stage(record, documentReferences);
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
    private sealed class FailingDocumentReferenceStager : IAircraftCasePackDocumentReferenceStager
    {
        private readonly AircraftCasePackDocumentReferenceStager _inner = new();
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public IReadOnlyDictionary<string, AircraftCasePackDocumentReference> Stage(
            AircraftCasePackCase record,
            IReadOnlyDictionary<string, EvidenceBlobVersion> documentBlobs)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("injected document-reference staging failure");
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
    public async Task AC001_ValidatesBeforeAnyStoreWrites()
    {
        var fixture = LoadFixture();
        var rows = new (string Name, Action<JsonObject> Change, string Expected, string? MissingFile)[]
        {
            ("schema version 2", json => json["schemaVersion"] = 2, "schemaVersion", null),
            ("two cases", json => json["cases"]!.AsArray().RemoveAt(2), "cases", null),
            ("four cases", json => json["cases"]!.AsArray().Add(json["cases"]![0]!.DeepClone()), "cases", null),
            ("seven case documents", json => json["cases"]![0]!["documents"]!.AsArray().RemoveAt(7), "cases[0].documents", null),
            ("thirteen case documents", json =>
            {
                for (var i = 0; i < 5; i++) json["cases"]![0]!["documents"]!.AsArray().Add(json["cases"]![0]!["documents"]![0]!.DeepClone());
            }, "cases[0].documents", null),
            ("23 total documents", json => json["cases"]![0]!["documents"]!.AsArray().RemoveAt(0), "cases.documents", null),
            ("37 total documents", json =>
            {
                for (var i = 0; i < 13; i++) json["cases"]![0]!["documents"]!.AsArray().Add(json["cases"]![0]!["documents"]![0]!.DeepClone());
            }, "cases.documents", null),
            ("stale digest", json => json["sha256"] = new string('0', 64), "sha256", null),
            ("false case marker", json => json["cases"]![0]!["synthetic"] = false, "cases[0].synthetic", null),
            ("false document marker", json => json["cases"]![0]!["documents"]![0]!["synthetic"] = false, "cases[0].documents[0].synthetic", null),
            ("empty case generator", json => json["cases"]![0]!["generatorReference"] = "", "cases[0].generatorReference", null),
            ("blank case generator", json => json["cases"]![0]!["generatorReference"] = "   ", "cases[0].generatorReference", null),
            ("empty document generator", json => json["cases"]![0]!["documents"]![0]!["generatorReference"] = "", "cases[0].documents[0].generatorReference", null),
            ("blank document generator", json => json["cases"]![0]!["documents"]![0]!["generatorReference"] = "   ", "cases[0].documents[0].generatorReference", null),
            ("empty review reference", json => json["productOwnerReviewReference"] = "", "productOwnerReviewReference", null),
            ("blank review reference", json => json["productOwnerReviewReference"] = "   ", "productOwnerReviewReference", null),
            ("missing listed fixture", json => { }, "case-a/aircraft-summary.txt", "case-a/aircraft-summary.txt")
        };

        foreach (var row in rows)
        {
            var json = JsonNode.Parse(fixture.Json)!.AsObject();
            row.Change(json);
            var root = CreateFixtureDirectory(fixture, json.ToJsonString(), row.MissingFile);
            try
            {
                var evidence = new RecordingEvidenceStore();
                var publication = new RecordingCaseStore();
                var caseStager = new RecordingCaseStager();
                var referenceStager = new FailingDocumentReferenceStager();
                var publisher = new AircraftCasePackPublisher(evidence, publication, caseStager, referenceStager);

                var exception = await Assert.ThrowsAsync<AircraftCasePackValidationException>(() =>
                    publisher.PublishAsync(new AircraftCasePackLoader(root), 0));

                Assert.Contains(exception.Errors, error => error.StartsWith(row.Expected + ":", StringComparison.Ordinal));
                Assert.Equal(0, evidence.Calls);
                Assert.Equal(0, caseStager.Calls);
                Assert.Equal(0, referenceStager.Calls);
                Assert.Equal(0, publication.Reads);
                Assert.Equal(0, publication.Commits);
                Assert.Null(publication.VisibleSnapshot);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AC002_InvalidFixedManifestIsRejectedBeforeAnyWrite()
    {
        var fixture = LoadFixture();
        var evidence = new RecordingEvidenceStore();
        var cases = new RecordingCaseStore();
        var publisher = new AircraftCasePackPublisher(evidence, cases, new AircraftCasePackCaseStager());
        var prior = await publisher.PublishAsync(new AircraftCasePackLoader(FixtureRoot()), 0);
        var priorCases = prior.Cases.ToArray();
        var rows = new (string Name, Action<JsonObject> Change, string Error)[]
        {
            ("two cases", json => json["cases"]!.AsArray().RemoveAt(2), "cases"),
            ("duplicate case ID", json => json["cases"]![2]!["caseId"] = "cp-case-b", "cases[2].caseId"),
            ("unknown case ID", json => json["cases"]![2]!["caseId"] = "cp-case-d", "cases[2].caseId"),
            ("different scenario date", json => json["scenarioDate"] = "2026-10-02", "scenarioDate"),
            ("impossible scenario date", json => json["scenarioDate"] = "2026-02-30", "scenarioDate")
        };

        foreach (var row in rows)
        {
            var json = JsonNode.Parse(fixture.Json)!.AsObject();
            row.Change(json);
            RefreshHash(json, fixture.Files);
            var writesBefore = evidence.Writes.Count;
            var callsBefore = evidence.Calls;
            var commitsBefore = cases.Commits;

            var exception = await Assert.ThrowsAsync<AircraftCasePackValidationException>(() =>
                publisher.PublishAsync(
                    () => new AircraftCasePackLoader(FixtureRoot()).Load(json.ToJsonString(), fixture.Files),
                    1));

            Assert.Contains(exception.Errors, error => error.StartsWith(row.Error, StringComparison.Ordinal));
            Assert.Same(prior, cases.Read());
            Assert.Equal(prior.PackVersion, cases.Read()!.PackVersion);
            Assert.Equal(prior.Sha256, cases.Read()!.Sha256);
            Assert.Equal(prior.Revision, cases.Read()!.Revision);
            Assert.Equal(priorCases, cases.Read()!.Cases);
            Assert.Equal(writesBefore, evidence.Writes.Count);
            Assert.Equal(callsBefore, evidence.Calls);
            Assert.Equal(commitsBefore, cases.Commits);
        }
    }

    [Fact]
    public async Task AC018_InvalidIdentityDoesNotWriteOrPublish()
    {
        var fixture = LoadFixture();
        var evidence = new RecordingEvidenceStore();
        var cases = new RecordingCaseStore();
        var publisher = new AircraftCasePackPublisher(evidence, cases, new AircraftCasePackCaseStager());
        var prior = await publisher.PublishAsync(new AircraftCasePackLoader(FixtureRoot()), 0);
        var writesBefore = evidence.Writes.Count;
        var callsBefore = evidence.Calls;
        var commitsBefore = cases.Commits;
        var json = JsonNode.Parse(fixture.Json)!.AsObject();
        json["cases"]![0]!["aircraftRegistration"]!["synthetic"] = false;
        RefreshHash(json, fixture.Files);

        var exception = await Assert.ThrowsAsync<AircraftCasePackValidationException>(() =>
            publisher.PublishAsync(
                () => new AircraftCasePackLoader(FixtureRoot()).Load(json.ToJsonString(), fixture.Files), 1));

        Assert.Contains("cases[0].aircraftRegistration.synthetic: must be true", exception.Errors);
        Assert.Equal(writesBefore, evidence.Writes.Count);
        Assert.Equal(callsBefore, evidence.Calls);
        Assert.Equal(commitsBefore, cases.Commits);
        Assert.Same(prior, cases.Read());
        Assert.Equal(prior.PackVersion, cases.Read()!.PackVersion);
        Assert.Equal(prior.Sha256, cases.Read()!.Sha256);
        Assert.Equal(prior.Revision, cases.Read()!.Revision);
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
            var changed = CreateChangedFixture(fixture, "1.0.3");

            if (failure == "evidence") evidence.FailAfterWriteCall = evidence.Calls + 1;
            else stager.FailCaseId = "cp-case-a";
            var publishError = failure == "evidence" ? typeof(IOException) : typeof(InvalidOperationException);
            await Assert.ThrowsAsync(publishError, () =>
                publisher.PublishAsync(new AircraftCasePackLoader(changed.Root), 1));

            Assert.Same(prior, cases.Read());
            Assert.Equal(1, cases.Commits);
            Assert.Equal("1.0.2", cases.Read()!.PackVersion);
            Assert.Equal(unrelatedBefore, unrelated.Read(unrelatedScope));
            var visibleBlobNames = cases.Read()!.Cases.SelectMany(item => item.DocumentReferences)
                .Select(item => item.BlobName).ToHashSet(StringComparer.Ordinal);
            Assert.NotEmpty(evidence.Writes.Skip(priorBlobCount));
            Assert.All(evidence.Writes.Skip(priorBlobCount), blob => Assert.DoesNotContain(blob.Name, visibleBlobNames));

            evidence.FailAfterWriteCall = null;
            stager.FailCaseId = null;
            var retry = await publisher.PublishAsync(new AircraftCasePackLoader(changed.Root), 1);
            Assert.Equal("1.0.3", retry.PackVersion);
            Assert.Equal(2, cases.Commits);
            Assert.Equal(3, retry.Cases.Count);
            foreach (var path in new[] { unrelatedPath, unrelatedPath + ".lock", unrelatedPath + ".tmp" }) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(changed.Root, recursive: true);
        }
    }

    [Fact]
    public async Task M05_AC002_FailuresPreserveVisibleSnapshotAndHideOrphans()
    {
        foreach (var failure in new[] { "blob", "case", "document-reference", "revision" })
        {
            var fixture = LoadFixture();
            var evidence = new RecordingEvidenceStore();
            var caseStager = new FailingCaseStager();
            var referenceStager = new FailingDocumentReferenceStager();
            var changed = CreateChangedFixture(fixture, "1.0.2");
            var statePath = Path.Combine(Path.GetTempPath(), $"aircraft-pack-state-{Guid.NewGuid():N}.json");
            var unrelatedStore = new CaseStateStore(statePath);
            var unrelatedScope = unrelatedStore.CreateCase("unrelated-case", "presenter");
            unrelatedStore.Commit(unrelatedScope, 0, new CaseCommit(
                CaseStateStore.CreateBasisVersion(1, "unrelated basis", ""), null, "presenter", "seed", "unrelated"));
            var publication = new RecordingCaseStore();
            var publisher = new AircraftCasePackPublisher(evidence, publication, caseStager, referenceStager);

            try
            {
                var prior = await publisher.PublishAsync(new AircraftCasePackLoader(FixtureRoot()), 0);
                var priorBlobCount = evidence.Writes.Count;
                var unrelatedBeforeAttempt = unrelatedStore.Read(unrelatedScope);
                var auditBeforeAttempt = unrelatedStore.ReadAudit(unrelatedScope);
                var beforeAttempt = publication.ReadApplicationState();
                switch (failure)
                {
                    case "blob": evidence.FailAfterWriteCall = evidence.Calls + 1; break;
                    case "case": caseStager.FailCaseId = "cp-case-a"; break;
                    case "document-reference": referenceStager.Fail = true; break;
                    case "revision":
                        publication.ChangeRevisionAtNextCommit = true;
                        publication.PublishInterveningSnapshotOnRace = true;
                        break;
                }
                var expectedException = failure switch
                {
                    "blob" => typeof(IOException),
                    "revision" => typeof(AircraftCasePackRevisionConflictException),
                    _ => typeof(InvalidOperationException)
                };
                await Assert.ThrowsAsync(expectedException, () =>
                    publisher.PublishAsync(new AircraftCasePackLoader(changed.Root), 1));

                var expectedState = failure == "revision"
                    ? publication.InterveningApplicationState
                    : beforeAttempt;
                Assert.NotNull(expectedState);
                var afterAttempt = publication.ReadApplicationState();
                var visible = expectedState!.Pack;
                Assert.NotNull(visible);
                Assert.Same(expectedState.Pack, afterAttempt.Pack);
                Assert.Same(visible, publication.Read());
                Assert.Equal(failure == "revision" ? 2 : 1, visible!.Revision);
                Assert.Equal("1.0.1", visible.PackVersion);
                Assert.Equal(prior.Cases.Count, visible.Cases.Count);
                for (var i = 0; i < prior.Cases.Count; i++)
                {
                    Assert.Same(prior.Cases[i], visible.Cases[i]);
                    Assert.Equal(prior.Cases[i].DocumentReferences.Count, visible.Cases[i].DocumentReferences.Count);
                    for (var j = 0; j < prior.Cases[i].DocumentReferences.Count; j++)
                        Assert.Same(prior.Cases[i].DocumentReferences[j], visible.Cases[i].DocumentReferences[j]);
                }
                AssertSameApplicationState(expectedState, afterAttempt);
                AssertSameCaseState(
                    unrelatedBeforeAttempt,
                    unrelatedStore.Read(unrelatedScope),
                    auditBeforeAttempt,
                    unrelatedStore.ReadAudit(unrelatedScope));
                Assert.NotEmpty(evidence.Writes.Skip(priorBlobCount));
                var visibleBlobNames = visible.Cases.SelectMany(item => item.DocumentReferences)
                    .Select(item => item.BlobName).ToHashSet(StringComparer.Ordinal);
                foreach (var orphan in evidence.Writes.Skip(priorBlobCount))
                {
                    Assert.DoesNotContain(orphan.Name, visibleBlobNames);
                    Assert.Null(await evidence.ReadAsync(orphan.Name, orphan.Sha256));
                }
                Assert.Equal(failure == "revision" ? 2 : 1, publication.Revision);
            }
            finally
            {
                Directory.Delete(changed.Root, recursive: true);
                foreach (var path in new[] { statePath, statePath + ".lock", statePath + ".tmp" })
                    if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private static void AssertSameApplicationState(ApplicationVisibleState expected, ApplicationVisibleState actual)
    {
        Assert.Same(expected.Pack, actual.Pack);
    }

    private static void AssertSameCaseState(
        CaseState expected,
        CaseState actual,
        IReadOnlyList<AuditEvent> expectedAudit,
        IReadOnlyList<AuditEvent> actualAudit)
    {
        Assert.Same(expected, actual);
        Assert.Equal(expected.CaseId, actual.CaseId);
        Assert.Equal(expected.Owner, actual.Owner);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Basis, actual.Basis);
        Assert.Equal(expected.ReceiptIds, actual.ReceiptIds);
        Assert.Equal(expected.ReceiptFingerprints, actual.ReceiptFingerprints);
        Assert.Same(expectedAudit, actualAudit);
        Assert.Equal(expectedAudit, actualAudit);
        Assert.Equal(expectedAudit.Count, actualAudit.Count);
        for (var i = 0; i < expectedAudit.Count; i++)
            Assert.Same(expectedAudit[i], actualAudit[i]);
    }

    [Fact]
    public async Task RetryIsIdempotentAndChangedSameVersionIsRejected()
    {
        var fixture = LoadFixture(); var evidence = new RecordingEvidenceStore(); var cases = new RecordingCaseStore();
        var publisher = new AircraftCasePackPublisher(evidence, cases, new AircraftCasePackCaseStager());
        var initial = await publisher.PublishAsync(new AircraftCasePackLoader(FixtureRoot()), 0);
        Assert.Equal("1.0.2", initial.PackVersion);
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

        var changed = CreateChangedFixture(fixture, "1.0.2");
        Assert.Equal("1.0.2", changed.Manifest.PackVersion);
        Assert.NotEqual(initial.Sha256, changed.Manifest.Sha256);
        var exception = await Assert.ThrowsAsync<AircraftCasePackValidationException>(() =>
            publisher.PublishAsync(new AircraftCasePackLoader(changed.Root), 1));
        Assert.Equal(["packVersion: must change when canonical content changes"], exception.Errors);
        Assert.Same(initial, cases.Read());
        Assert.Equal(writeCount, evidence.Writes.Count);
        Assert.Equal(1, cases.Commits);
        Directory.Delete(changed.Root, recursive: true);
    }

    [Fact]
    public async Task AC018_RetriesAreIdempotentAndVersionChangesAreRejected()
    {
        var fixture = LoadFixture();
        var evidence = new RecordingEvidenceStore();
        var publication = new RecordingCaseStore();
        var caseStager = new RecordingCaseStager();
        var referenceStager = new FailingDocumentReferenceStager();
        var publisher = new AircraftCasePackPublisher(evidence, publication, caseStager, referenceStager);
        var initial = await publisher.PublishAsync(new AircraftCasePackLoader(FixtureRoot()), 0);
        var initialCalls = evidence.Calls;
        var initialWrites = evidence.Writes.Count;
        var initialCaseStages = caseStager.Calls;
        var initialReferenceStages = referenceStager.Calls;

        var replay = await publisher.PublishAsync(new AircraftCasePackLoader(FixtureRoot()), 1);
        Assert.Same(initial, replay);
        Assert.Equal(initialCalls, evidence.Calls);
        Assert.Equal(initialWrites, evidence.Writes.Count);
        Assert.Equal(initialCaseStages, caseStager.Calls);
        Assert.Equal(initialReferenceStages, referenceStager.Calls);
        Assert.Equal(1, publication.Commits);

        var sameVersionChanged = CreateChangedFixture(fixture, "1.0.1");
        try
        {
            var conflict = await Assert.ThrowsAsync<AircraftCasePackValidationException>(() =>
                publisher.PublishAsync(new AircraftCasePackLoader(sameVersionChanged.Root), 1));
            Assert.Equal(["packVersion: must change when canonical content changes"], conflict.Errors);
            Assert.Same(initial, publication.Read());
            Assert.Equal(initialCalls, evidence.Calls);
            Assert.Equal(initialWrites, evidence.Writes.Count);
            Assert.Equal(initialCaseStages, caseStager.Calls);
            Assert.Equal(initialReferenceStages, referenceStager.Calls);
            Assert.Equal(1, publication.Commits);
        }
        finally
        {
            Directory.Delete(sameVersionChanged.Root, recursive: true);
        }

        var newVersionChanged = CreateChangedFixture(fixture, "1.0.2");
        try
        {
            var updated = await publisher.PublishAsync(new AircraftCasePackLoader(newVersionChanged.Root), 1);
            Assert.NotSame(initial, updated);
            Assert.Equal(2, updated.Revision);
            Assert.Equal("1.0.2", updated.PackVersion);
            Assert.Equal(newVersionChanged.Manifest.Sha256, updated.Sha256);
            Assert.Equal(3, updated.Cases.Count);
            Assert.Equal(24, updated.Cases.Sum(item => item.DocumentReferences.Count));
            Assert.Equal(initialCalls + 24, evidence.Calls);
            Assert.Equal(initialCaseStages + 3, caseStager.Calls);
            Assert.Equal(initialReferenceStages + 3, referenceStager.Calls);
            Assert.Equal(2, publication.Commits);
        }
        finally
        {
            Directory.Delete(newVersionChanged.Root, recursive: true);
        }
    }
    [Fact]
    public async Task PublicationRevisionRaceDoesNotExposeCandidate()
    {
        var fixture = LoadFixture(); var evidence = new RecordingEvidenceStore(); var cases = new RecordingCaseStore();
        var publisher = new AircraftCasePackPublisher(evidence, cases, new AircraftCasePackCaseStager());
        var prior = await publisher.PublishAsync(new AircraftCasePackLoader(FixtureRoot()), 0);
        var priorBlobCount = evidence.Writes.Count;
        var changed = CreateChangedFixture(fixture, "1.0.3");
        Assert.Equal("1.0.3", changed.Manifest.PackVersion);
        Assert.NotEqual(prior.Sha256, changed.Manifest.Sha256);
        cases.ChangeRevisionAtNextCommit = true;

        await Assert.ThrowsAsync<AircraftCasePackRevisionConflictException>(() =>
            publisher.PublishAsync(new AircraftCasePackLoader(changed.Root), 1));
        Assert.Same(prior, cases.Read());
        Assert.Equal("1.0.2", cases.Read()!.PackVersion);
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
        var newVersion = sameVersion with { PackVersion = "1.0.3", Sha256 = "" };
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
        var changedPath = fixture.Manifest.Cases[0].Documents![0].Path;
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
    private static string CreateFixtureDirectory(Fixture fixture, string manifestJson, string? omitPath = null)
    {
        var root = Directory.CreateTempSubdirectory("aircraft-case-pack-validation-").FullName;
        File.WriteAllText(Path.Combine(root, "manifest.json"), manifestJson);
        foreach (var path in fixture.Manifest.Files.Where(path => !StringComparer.Ordinal.Equals(path, omitPath)))
        {
            var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, fixture.Files[path]);
        }
        return root;
    }
    private static Dictionary<string, byte[]> CopyFiles(IReadOnlyDictionary<string, byte[]> files) =>
        files.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.Clone(), StringComparer.Ordinal);
    private static void RefreshHash(JsonObject json, IReadOnlyDictionary<string, byte[]> files)
    {
        var manifest = JsonSerializer.Deserialize<AircraftCasePackManifest>(json.ToJsonString(), WebJson)!;
        json["sha256"] = AircraftCasePackManifest.ComputeSha256(manifest, files);
    }
    private static string FixtureRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "AirlineDemo.Api", "Fixtures", "aircraft-case-pack"));
}
