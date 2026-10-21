using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Fixtures;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Projection;

namespace AirlineDemo.Api.Tests;

public sealed class CasePackSceneProjectionTests
{
    private const string CaseId = "cp-case-a";
    private const string Owner = "owner-a";

    [Fact]
    public void AC009_ProjectionKeepsOneCaseAndRevision()
    {
        var pack = Pack();
        var store = new SnapshotStore(State(CaseId, 7));
        var scene = CasePackSceneProjection.Build(store, new CaseScope(CaseId), pack, "presenter");

        Assert.Equal(1, store.ReadCalls);
        Assert.NotNull(scene);
        Assert.Equal((CaseId, 7L, 4, 1, 1, 2),
            (scene.Case.CaseId, scene.Case.CaseRevision, scene.Objects.Count,
             scene.Objects.Count(item => item.Kind == "aircraft"),
             scene.Objects.Count(item => item.Kind == "engine"),
             scene.Objects.Count(item => item.Kind == "component")));
        Assert.All(scene.Objects, item => Assert.Equal(CaseId, item.CaseId));
        Assert.All(scene.History, item => Assert.False(string.IsNullOrWhiteSpace(item.RunId)));

        var crossCaseAssessment = State(CaseId, 7) with
        {
            Assessments = [new CaseAssessment("a", "cp-case-b", "basis-v1", 1, "Review", "reason", [], null, null)]
        };
        Assert.Null(CasePackSceneProjection.Build(pack, crossCaseAssessment, "presenter"));

        var crossCaseEvent = State(CaseId, 7) with
        {
            JourneyEvents = [Event("AI agent", "cp-case-b",
                [new CaseJourneyWitness("witness-b-event", "case-b/foreign.txt", 1, 1, "basis-v1", 1)])]
        };
        Assert.Null(CasePackSceneProjection.Build(pack, crossCaseEvent, "presenter"));

        var source = Pack();
        var first = source.Cases[0] with
        {
            Components = [new PublishedAircraftCasePackComponent("bad", ["case-b/foreign.txt"])]
        };
        var crossCaseDocument = new AircraftCasePackSnapshot(source.Revision, source.Manifest, source.Files,
            [first, .. source.Cases.Skip(1)]);
        Assert.Null(CasePackSceneProjection.Build(crossCaseDocument, State(CaseId, 7), "presenter"));

        var crossCaseWitness = State(CaseId, 7) with
        {
            JourneyEvents = [Event("AI agent", CaseId,
                [new CaseJourneyWitness("witness-b", "case-b/foreign.txt", 1, 1, "basis-v1", 1)])]
        };
        Assert.Null(CasePackSceneProjection.Build(pack, crossCaseWitness, "presenter"));
    }

    [Theory]
    [InlineData("AI agent", "AI agent")]
    [InlineData("Rules-based", "Rules-based")]
    [InlineData("Simulated", "Simulated")]
    [InlineData("Design-only", "Design-only")]
    [InlineData("Unknown", "Unknown")]
    [InlineData(null, "Unknown")]
    [InlineData("unrecognized", "Unknown")]
    public void AC010_LabelsAndEvidenceMappingsUseExactRules(string? raw, string expected)
    {
        var state = State(CaseId, 3) with
        { JourneyEvents = raw is null ? [] : [Event(raw)] };
        var scene = CasePackSceneProjection.Build(Pack(), state, "presenter");

        Assert.NotNull(scene);
        Assert.Equal(expected, scene.Case.CapabilityLabel);
        Assert.All(scene.DocumentRefs, reference => Assert.True(
            scene.Objects.SelectMany(item => item.EvidenceIds).Contains(reference.EvidenceRefId)));
        Assert.All(scene.Objects.Where(item => item.Kind == "component"),
            item => Assert.NotEmpty(item.EvidenceIds));

        var source = Pack();
        var first = source.Cases[0] with
        {
            Components = [new PublishedAircraftCasePackComponent("bad", ["case-b/foreign.txt"])]
        };
        var bad = new AircraftCasePackSnapshot(source.Revision, source.Manifest, source.Files,
            [first, .. source.Cases.Skip(1)]);
        Assert.Null(CasePackSceneProjection.Build(bad, state, "presenter"));
    }

    [Fact]
    public void AC026_SceneSchemaAcceptsAndRejectsExactFieldCases()
    {
        var scene = CasePackSceneProjection.Build(Pack(), State(CaseId, 11), "presenter");
        Assert.NotNull(scene);
        Assert.True(CasePackSceneProjection.Validate(scene));
        var valid = JsonSerializer.SerializeToElement(scene, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(CasePackSceneProjection.Validate(valid));

        foreach (var label in new[] { "AI agent", "Rules-based", "Simulated", "Design-only", "Unknown" })
        {
            var row = Clone(valid); row["case"]!["capabilityLabel"] = label;
            Assert.True(CasePackSceneProjection.Validate(row.ToJsonString()));
        }
        var missing = Clone(valid); missing["case"]!.AsObject().Remove("caseRevision");
        var extra = Clone(valid); extra["extra"] = true;
        var wrongType = Clone(valid); wrongType["contractVersion"] = "1";
        var invalidLabel = Clone(valid); invalidLabel["case"]!["capabilityLabel"] = "other";
        var stringPermission = Clone(valid); stringPermission["canResetBlockedVariant"] = "false";
        var duplicate = Clone(valid);
        duplicate["objects"]![1]!["objectId"] = JsonValue.Create(duplicate["objects"]![0]!["objectId"]!.GetValue<string>());
        var unresolvedWitness = Clone(valid);
        unresolvedWitness["evidence"]!.AsArray().Clear();
        unresolvedWitness["history"]!.AsArray().Add(new JsonObject
        {
            ["eventId"] = "missing-witness-event", ["runId"] = "run-1", ["sequence"] = 1,
            ["eventType"] = "evidence-reviewed", ["basisId"] = "basis-v1", ["basisVersion"] = 1,
            ["actorRole"] = "presenter", ["summary"] = "unresolved witness", ["capabilityLabel"] = "Unknown",
            ["witnessIds"] = new JsonArray("missing-witness")
        });

        Assert.False(CasePackSceneProjection.Validate(missing.ToJsonString()));
        Assert.False(CasePackSceneProjection.Validate(extra.ToJsonString()));
        Assert.False(CasePackSceneProjection.Validate(wrongType.ToJsonString()));
        Assert.False(CasePackSceneProjection.Validate(invalidLabel.ToJsonString()));
        Assert.False(CasePackSceneProjection.Validate(stringPermission.ToJsonString()));
        Assert.False(CasePackSceneProjection.Validate(duplicate.ToJsonString()));
        Assert.False(CasePackSceneProjection.Validate(unresolvedWitness.ToJsonString()));
    }

    [Fact]
    public void AC027_CurrentRunWindowPreservesOlderHistoryAndWitnesses()
    {
        var events = new List<CaseJourneyEvent>();
        for (var index = 1; index <= 100; index++)
            events.Add(new CaseJourneyEvent("old-" + index, "old-run-" + index, index, CaseId,
                "evidence-reviewed", "basis-v1", 1, "presenter", "Older run", "AI agent",
                [new CaseJourneyWitness("old-witness-" + index, "case-a/aircraft-summary.txt", 1, 1, "basis-v1", 1)]));
        for (var index = 1; index <= 25; index++)
            events.Add(new CaseJourneyEvent("current-" + index, "current-run", 100 + index, CaseId,
                "evidence-reviewed", "basis-v1", 1, "presenter", "Current run event", "AI agent",
                [new CaseJourneyWitness("current-witness-" + index, "case-a/aircraft-summary.txt", 1, 1, "basis-v1", 1)]));
        var state = State(CaseId, 25) with { JourneyEvents = events };
        var reader = new FakeArrHistoryReader(CaseId, "current-run", "basis-v1", events.TakeLast(25).ToArray(), 100);
        var result = CasePackSceneProjection.BuildResult(Pack(), state, "presenter", history: reader);

        Assert.True(result.Succeeded);
        var scene = result.Scene;
        Assert.NotNull(scene);
        Assert.Equal([.. Enumerable.Range(106, 20).Select(index => (long)index)],
            scene.History.Select(item => item.Sequence).ToArray());
        Assert.Equal(20, scene.History.Count);
        Assert.True(scene.OlderHistoryAvailable);
        Assert.Equal(20, scene.Evidence.Count);
        Assert.All(scene.Evidence, witness => Assert.Equal("case-a/aircraft-summary.txt", witness.DocumentId));
        Assert.Equal(125, state.JourneyEvents!.Count);
        Assert.Equal(100, reader.RetainedOlderRuns);
        Assert.True(reader.Selectors.SequenceEqual([new ArrSelector(CaseId, "current-run", "basis-v1")],
            EqualityComparer<ArrSelector>.Default));

        var oversized = State(CaseId, 26) with { JourneyEvents = [new CaseJourneyEvent("oversized", "current-run", 126, CaseId,
            "evidence-reviewed", "basis-v1", 1, "presenter", new string('x', 70_000), "AI agent", [])] };
        var limit = CasePackSceneProjection.BuildResult(Pack(), oversized, "presenter");
        Assert.True(limit.PayloadLimit);
        Assert.Null(limit.Scene);
        Assert.Equal(CasePackSceneProjection.PayloadLimitCode, limit.ErrorCode);
    }

    [Fact]
    public void AC028_EvidenceWitnessesBindExactCaseRunDocumentBasisAndPageTuples()
    {
        var pack = Pack();
        var validBasis = State(CaseId, 30) with
        {
            Basis = [new BasisVersion(1, "old", "old-digest", "old-basis", DateTimeOffset.UtcNow),
                new BasisVersion(2, "current", "current-digest", "current-basis", DateTimeOffset.UtcNow)],
            JourneyEvents = [new CaseJourneyEvent("current-event", "current-run", 1, CaseId,
                "evidence-reviewed", "basis-v2", 2, "presenter", "Current evidence", "AI agent",
                [new CaseJourneyWitness("historical-witness", "case-a/aircraft-summary.txt", 1, 1, "basis-v1", 1)])]
        };
        var valid = CasePackSceneProjection.Build(pack, validBasis, "presenter");
        Assert.NotNull(valid);
        var witness = Assert.Single(valid.Evidence);
        Assert.Equal((1, 1, 1), (witness.DocumentVersion, witness.BasisVersion, witness.Page));
        Assert.Equal((1, 1), (witness.DocumentVersion, witness.BasisVersion));
        Assert.Contains(valid.DocumentRefs, item => item.BasisId == "basis-v2" && item.DocumentVersion == 1 && item.UsableAsEvidence);
        Assert.Contains(valid.DocumentRefs, item => item.BasisId == "basis-v1" && item.DocumentVersion == 1 && item.UsableAsEvidence);

        var source = Pack();
        var newerCase = source.Cases[0] with
        {
            Documents = source.Cases[0].Documents.Select(document => document with
                { Evidence = document.Evidence with { Version = 2 } }).ToArray(),
            DocumentReferences = source.Cases[0].DocumentReferences.Select(reference => reference with
                { Version = 2 }).ToArray()
        };
        var newerPack = new AircraftCasePackSnapshot(source.Revision, source.Manifest, source.Files,
            [newerCase, .. source.Cases.Skip(1)]);
        var independentTuple = validBasis with
        {
            JourneyEvents = [new CaseJourneyEvent("independent-event", "current-run", 2, CaseId,
                "evidence-reviewed", "basis-v2", 2, "presenter", "Independent versions", "AI agent",
                [new CaseJourneyWitness("independent-witness", "case-a/aircraft-summary.txt", 1, 1, "basis-v1", 1)])]
        };
        var independent = CasePackSceneProjection.Build(newerPack, independentTuple, "presenter");
        Assert.NotNull(independent);
        var independentWitness = Assert.Single(independent.Evidence, item => item.WitnessId == "independent-witness");
        Assert.Equal((1, 1), (independentWitness.DocumentVersion, independentWitness.BasisVersion));
        Assert.Contains(independent.DocumentRefs, item => item.DocumentVersion == 2 && item.BasisVersion == 2 && item.UsableAsEvidence);
        Assert.Contains(independent.DocumentRefs, item => item.DocumentVersion == 1 && item.BasisVersion == 1 && item.UsableAsEvidence);

        foreach (var mutation in new[] { "case", "document", "document-version", "basis-id", "basis-version", "run", "page" })
        {
            var badWitness = mutation switch
            {
                "case" => new CaseJourneyWitness("bad", "case-a/aircraft-summary.txt", 1, 1, "basis-v1", 1),
                "document" => new CaseJourneyWitness("bad", "case-b/foreign.txt", 1, 1, "basis-v1", 1),
                "document-version" => new CaseJourneyWitness("bad", "case-a/aircraft-summary.txt", 999, 1, "basis-v1", 1),
                "basis-id" => new CaseJourneyWitness("bad", "case-a/aircraft-summary.txt", 1, 1, "wrong-basis", 1),
                "basis-version" => new CaseJourneyWitness("bad", "case-a/aircraft-summary.txt", 1, 1, "basis-v1", 999),
                "run" => new CaseJourneyWitness("bad", "case-a/aircraft-summary.txt", 1, 1, "basis-v1", 1),
                "page" => new CaseJourneyWitness("bad", "case-a/aircraft-summary.txt", 1, 999, "basis-v1", 1),
                _ => throw new InvalidOperationException()
            };
            var badEvent = mutation switch
            {
                "case" => new CaseJourneyEvent("bad-event", "current-run", 2, "cp-case-b", "evidence-reviewed", "basis-v1", 1, "presenter", "bad", "AI agent", [badWitness]),
                "run" => new CaseJourneyEvent("bad-event", "wrong-run", 2, CaseId, "evidence-reviewed", "basis-v1", 1, "presenter", "bad", "AI agent", [badWitness]),
                _ => new CaseJourneyEvent("bad-event", "current-run", 2, CaseId, "evidence-reviewed", "basis-v1", 1, "presenter", "bad", "AI agent", [badWitness])
            };
            if (mutation == "run")
            {
                var validEvent = new CaseJourneyEvent("valid-event", "current-run", 3, CaseId, "evidence-reviewed", "basis-v1", 1, "presenter", "valid", "AI agent", []);
                var badState = State(CaseId, 31) with { JourneyEvents = [badEvent, validEvent] };
                var scene = CasePackSceneProjection.Build(pack, badState, "presenter");
                Assert.NotNull(scene);
                Assert.DoesNotContain(scene.History, item => item.RunId == "wrong-run");
                Assert.DoesNotContain(scene.Evidence, item => item.WitnessId == "bad");
            }
            else
            {
                var badState = State(CaseId, 31) with { JourneyEvents = [badEvent] };
                Assert.Null(CasePackSceneProjection.Build(pack, badState, "presenter"));
            }
        }

        foreach (var status in new[] { "blocked", "failed", "partial" })
        {
            var documentStatus = status == "failed" ? "rejected-permanent"
                : status == "blocked" ? "rejected-retryable" : "rejected-permanent";
            var packages = PackageStore(status, documentStatus);
            var statusState = State(CaseId, 33) with
            {
                JourneyEvents = [new CaseJourneyEvent(status + "-event", "current-run", 1, CaseId,
                    "evidence-reviewed", "basis-v1", 1, "presenter", status + " evidence", "AI agent",
                    [new CaseJourneyWitness(status + "-witness", "case-a/aircraft-summary.txt", 1, 1, "basis-v1", 1)])]
            };
            var statusScene = CasePackSceneProjection.Build(pack, statusState, "presenter", packages);
            Assert.NotNull(statusScene);
            Assert.DoesNotContain(statusScene.Evidence, item => item.WitnessId == status + "-witness");
            Assert.DoesNotContain(statusScene.History.Single().WitnessIds, item => item == status + "-witness");
            Assert.All(statusScene.DocumentRefs.Where(item => item.DocumentId == "case-a/aircraft-summary.txt"),
                item => Assert.False(item.UsableAsEvidence));
        }
    }

    [Fact]
    public void AC027_ArrHistoryReaderUsesExactSelectorsAndRetainsOlderRuns()
    {
        var events = new List<CaseJourneyEvent>();
        for (var index = 1; index <= 100; index++)
            events.Add(new CaseJourneyEvent("old-" + index, "old-run-" + index, index, CaseId,
                "evidence-reviewed", "basis-v1", 1, "presenter", "Older run", "AI agent", []));
        for (var index = 1; index <= 25; index++)
            events.Add(new CaseJourneyEvent("current-" + index, "current-run", 100 + index, CaseId,
                "evidence-reviewed", "basis-v1", 1, "presenter", "Current run", "AI agent", []));
        var store = new SnapshotStore(State(CaseId, 25) with { JourneyEvents = events });
        var reader = new ArrCaseHistorySource(store);

        var selection = reader.Read(CaseId, "current-run", "basis-v1");
        Assert.NotNull(selection);
        Assert.Equal((CaseId, "current-run", "basis-v1", 25, true),
            (selection.CaseId, selection.RunId, selection.BasisId, selection.Events.Count, selection.OlderHistoryAvailable));
        Assert.Equal(25, selection.Events.Count(item => item.RunId == "current-run"));
        Assert.Null(reader.Read("cp-case-b", "current-run", "basis-v1"));
        Assert.Null(reader.Read(CaseId, "wrong-run", "basis-v1"));
        Assert.Null(reader.Read(CaseId, "current-run", "wrong-basis"));

        var deniedStore = new SnapshotStore(State(CaseId, 25), denyRead: true);
        Assert.Null(new ArrCaseHistorySource(deniedStore).Read(CaseId, "current-run", "basis-v1"));
    }

    private static EvidencePackageStore PackageStore(string packageState, string documentStatus)
    {
        var packages = new EvidencePackageStore();
        var document = new EvidenceDocumentResult("aircraft-summary.txt", documentStatus,
            documentStatus == "accepted" ? "accepted" : "no usable evidence", documentStatus == "accepted" ? 1 : null, null);
        packages.Submit("pkg-" + packageState, CaseId,
            () => new EvidencePackageOutcome(packageState, [document]));
        return packages;
    }

    [Theory]
    [InlineData("blocked", "accepted")]
    [InlineData("failed", "rejected-permanent")]
    [InlineData("failed", "rejected-retryable")]
    [InlineData("partial", "rejected-permanent")]
    public void AC028_BlockedFailedOrPartialReferenceHasNoWitness(string packageState, string documentStatus)
    {
        var packages = PackageStore(packageState, documentStatus);
        var state = State(CaseId, 32) with
        {
            JourneyEvents = [new CaseJourneyEvent("status-event", "current-run", 1, CaseId,
                "evidence-reviewed", "basis-v1", 1, "presenter", "Status evidence", "AI agent",
                [new CaseJourneyWitness("status-witness", "case-a/aircraft-summary.txt", 1, 1, "basis-v1", 1)])]
        };

        var scene = CasePackSceneProjection.Build(Pack(), state, "presenter", packages);
        Assert.NotNull(scene);
        var references = scene.DocumentRefs.Where(item => item.DocumentId == "case-a/aircraft-summary.txt").ToArray();
        Assert.NotEmpty(references);
        Assert.All(references, reference => Assert.False(reference.UsableAsEvidence));
        Assert.DoesNotContain(scene.Evidence, item => item.WitnessId == "status-witness");
        Assert.DoesNotContain(scene.History.Single().WitnessIds, item => item == "status-witness");
    }

    private sealed record ArrSelector(string CaseId, string RunId, string BasisId);
    private sealed class FakeArrHistoryReader : IArrCaseHistorySource
    {
        private readonly string _caseId; private readonly string _runId; private readonly string _basisId;
        private readonly CaseJourneyEvent[] _events;
        public int RetainedOlderRuns { get; }
        public List<ArrSelector> Selectors { get; } = [];
        public FakeArrHistoryReader(string caseId, string runId, string basisId, CaseJourneyEvent[] events, int retainedOlderRuns)
        { _caseId = caseId; _runId = runId; _basisId = basisId; _events = events; RetainedOlderRuns = retainedOlderRuns; }
        public ArrCaseHistorySelection? Read(string caseId, string runId, string basisId)
        {
            Selectors.Add(new ArrSelector(caseId, runId, basisId));
            if (caseId != _caseId || runId != _runId || basisId != _basisId) return null;
            return new ArrCaseHistorySelection(caseId, runId, basisId, _events, true);
        }
    }

    private static AircraftCasePackSnapshot Pack()
    {
        var root = CasePackFixture.Root();
        var json = File.ReadAllText(Path.Combine(root, "manifest.json"));
        using var manifestJson = JsonDocument.Parse(json);
        var manifestFiles = manifestJson.RootElement.GetProperty("files").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        var files = manifestFiles.ToDictionary(path => path,
            path => File.ReadAllBytes(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))), StringComparer.Ordinal);
        var loaded = new AircraftCasePackLoader(root).Load(json, files);
        var published = loaded.ValidatedManifest.Cases.Select(record =>
        {
            var blobs = record.Documents!.ToDictionary(document => document.Path,
                document => new EvidenceBlobVersion("blob-" + document.Path.Replace('/', '-'), "sha", 1), StringComparer.Ordinal);
            return new AircraftCasePackCaseStager().Stage(record, blobs);
        }).ToArray();
        return new AircraftCasePackSnapshot(1, loaded.ValidatedManifest, files, published);
    }

    private static CaseState State(string caseId, long revision) => new(caseId, Owner, revision,
        [new BasisVersion(1, "current basis", "digest", "basis-digest", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero))],
        [], [], new Dictionary<string, string>(), [], [], [], [], [], []);
    private static CaseJourneyEvent Event(string label) => Event(label, CaseId, []);
    private static CaseJourneyEvent Event(string label, string eventCaseId, IReadOnlyList<CaseJourneyWitness> witnesses) =>
        new("event-1", "run-1", 1, eventCaseId, "evidence-reviewed", "basis-v1", 1,
            "presenter", "Evidence reviewed", label, witnesses);
    private static JsonObject Clone(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();

    private sealed class SnapshotStore(CaseState state, bool denyRead = false) : ICaseStateStore
    {
        public int ReadCalls { get; private set; }
        public CaseScope AuthorizeCase(string caseId, string owner) => new(caseId);
        public string? OwnerOf(string caseId) => caseId == state.CaseId ? Owner : null;
        public CaseScope CreateCase(string caseId, string owner) => throw new NotSupportedException();
        public CaseState Read(CaseScope scope) { ReadCalls++; return denyRead ? throw new CaseAccessDeniedException() : state; }
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => throw new NotSupportedException();
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => throw new NotSupportedException();
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit) => throw new NotSupportedException();
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => throw new NotSupportedException();
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) => throw new NotSupportedException();
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) => throw new NotSupportedException();
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => throw new NotSupportedException();
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) => throw new NotSupportedException();
    }
}
