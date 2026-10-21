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

    private sealed class SnapshotStore(CaseState state) : ICaseStateStore
    {
        public int ReadCalls { get; private set; }
        public CaseScope AuthorizeCase(string caseId, string owner) => new(caseId);
        public string? OwnerOf(string caseId) => caseId == state.CaseId ? Owner : null;
        public CaseScope CreateCase(string caseId, string owner) => throw new NotSupportedException();
        public CaseState Read(CaseScope scope) { ReadCalls++; return state; }
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
