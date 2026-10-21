using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Dashboard;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Projection;

public sealed record CaseStates(string? Assessment, string? Review, string? Request, string? Finance, string? Legal, string? Acceptance);
public sealed record ActionSummary(string ActionId, string Text);
public sealed record CaseSummary(string CaseId, string Name, string AircraftCategory, long CaseRevision, string CurrentRunId,
    CaseStates Status, string? Owner, ActionSummary? NextAction, string? ActivityId, string CapabilityLabel, bool Synthetic);
public sealed record SceneObject(string ObjectId, string Kind, string CaseId, string DisplayName, CaseStates Status,
    string? Owner, ActionSummary? NextAction, string? ActivityId, string CapabilityLabel, IReadOnlyList<string> EvidenceIds);
public sealed record EvidenceReference(string EvidenceRefId, string DocumentId, int DocumentVersion, string BasisId, int BasisVersion,
    string DisplayLabel, string ProcessingState, bool Retryable, bool UsableAsEvidence);
public sealed record JourneyEvent(string EventId, string RunId, long Sequence, string EventType, string? BasisId, int? BasisVersion,
    string ActorRole, string Summary, string CapabilityLabel, IReadOnlyList<string> WitnessIds);
public sealed record EvidenceWitness(string WitnessId, string EvidenceRefId, string DocumentId, int DocumentVersion, int Page,
    string BasisId, int BasisVersion, string DisplayLabel);
public sealed record SceneEnvelope(int ContractVersion, string PackVersion, string ScenarioDate, CaseSummary Case,
    IReadOnlyList<string> AllowedActions, bool CanResetBlockedVariant, IReadOnlyList<SceneObject> Objects,
    IReadOnlyList<JourneyEvent> History, bool OlderHistoryAvailable, IReadOnlyList<EvidenceReference> DocumentRefs,
    IReadOnlyList<EvidenceWitness> Evidence);
public static class CasePackSceneProjection
{
    public const int ContractVersion = 1;
    public const string ScenarioDate = "2026-10-01";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static SceneEnvelope? Build(ICaseStateStore store, CaseScope scope, AircraftCasePackSnapshot pack,
        string? role = null, EvidencePackageStore? packages = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(pack);
        var state = store.Read(scope); // exactly one snapshot
        return state.CaseId == scope.CaseId ? Build(pack, state, role, packages) : null;
    }
    public static SceneEnvelope? Build(AircraftCasePackSnapshot pack, CaseState state, string? role = null,
        EvidencePackageStore? packages = null) => BuildState(pack, state, role, packages);
    public static SceneEnvelope? Build(CaseState state, AircraftCasePackSnapshot pack, string? role = null,
        EvidencePackageStore? packages = null) => BuildState(pack, state, role, packages);
    public static SceneEnvelope? Project(ICaseStateStore store, CaseScope scope, AircraftCasePackSnapshot pack,
        string? role = null, EvidencePackageStore? packages = null) => Build(store, scope, pack, role, packages);
    public static SceneEnvelope? Build(CaseScope scope, ICaseStateStore store, AircraftCasePackSnapshot pack,
        string? role = null, EvidencePackageStore? packages = null) => Build(store, scope, pack, role, packages);
    public static SceneEnvelope? Project(CaseScope scope, ICaseStateStore store, AircraftCasePackSnapshot pack,
        string? role = null, EvidencePackageStore? packages = null) => Build(store, scope, pack, role, packages);
    public static SceneEnvelope? Project(AircraftCasePackSnapshot pack, CaseState state,
        string? role = null, EvidencePackageStore? packages = null) => BuildState(pack, state, role, packages);
    public static SceneEnvelope? Project(CaseState state, AircraftCasePackSnapshot pack,
        string? role = null, EvidencePackageStore? packages = null) => BuildState(pack, state, role, packages);
    public static SceneEnvelope? ProjectScene(ICaseStateStore store, CaseScope scope, AircraftCasePackSnapshot pack,
        string? role = null, EvidencePackageStore? packages = null) => Build(store, scope, pack, role, packages);
    public static SceneEnvelope? ProjectScene(CaseState state, AircraftCasePackSnapshot pack,
        string? role = null, EvidencePackageStore? packages = null) => BuildState(pack, state, role, packages);

    public static SceneEnvelope? BuildState(AircraftCasePackSnapshot pack, CaseState state,
        string? role = null, EvidencePackageStore? packages = null)
    {
        var published = pack?.Cases.SingleOrDefault(item => item.CaseId == state?.CaseId);
        if (pack is null || state is null || state.Revision < 0 || published is null || published.AircraftCount != 1 ||
            published.EngineCount != 1 || published.Components.Count != 2 || !OwnedBySameCase(state)) return null;
        var casePathPrefix = state.CaseId switch { "cp-case-a" => "case-a/", "cp-case-b" => "case-b/", "cp-case-c" => "case-c/", _ => null };
        if (casePathPrefix is null || published.Documents.Any(document => !pack.Files.ContainsKey(document.Path)
            || !document.Path.StartsWith(casePathPrefix, StringComparison.Ordinal))) return null;
        if (published.Components.Any(component => component.EvidenceDocumentPaths.Any(path =>
            !path.StartsWith(casePathPrefix, StringComparison.Ordinal)))) return null;

        if (!TrySerial(pack, state.CaseId, "aircraft", null, out var aircraftId) ||
            !TrySerial(pack, state.CaseId, "engine", null, out var engineId)) return null;
        var category = state.CaseId switch { "cp-case-a" => "narrow-body", "cp-case-b" => "regional", "cp-case-c" => "wide-body", _ => null };
        var registration = pack.Manifest.Cases.SingleOrDefault(item => item.CaseId == state.CaseId)?.AircraftRegistration?.Value;
        if (category is null || string.IsNullOrWhiteSpace(registration)) return null;
        if (state.Basis.Count == 0) return null;
        var basis = state.Basis.MaxBy(item => item.Version);
        if (basis is null || basis.Version < 1) return null;

        var label = Label(state);
        var workItems = (state.WorkItems ?? []).Where(item => item.CaseId == state.CaseId).ToArray();
        var nextItem = label == CapabilityLabels.Unknown || role is null
            ? null : workItems.Where(item => item.State == "open" && (role == null || item.Owner == role)).MaxBy(item => item.ItemId);
        var owner = label == CapabilityLabels.Unknown ? null : nextItem?.Owner;
        var next = nextItem is null ? null : new ActionSummary(nextItem.ItemId,
            CapabilityProjection.LabelForCapability(nextItem.CapabilityId));
        var audit = state.Audit.OrderBy(item => item.Sequence).LastOrDefault();
        var activityId = label == CapabilityLabels.Unknown ? null : audit?.Sequence.ToString(CultureInfo.InvariantCulture);
        var states = CaseStatus(state, basis);

        var refs = new Dictionary<string, EvidenceReference>(StringComparer.Ordinal);
        var witnesses = new Dictionary<string, EvidenceWitness>(StringComparer.Ordinal);

        var allEvents = (state.JourneyEvents ?? []).Where(item => item.CaseId == state.CaseId)
            .OrderBy(item => item.Sequence).ToArray();
        if ((state.JourneyEvents ?? []).Any(item => item.CaseId != state.CaseId)) return null;
        var currentRun = allEvents.LastOrDefault()?.RunId ?? state.Traces?.LastOrDefault(item => item.CaseId == state.CaseId)?.RunId
            ?? state.BlockedVariantResetReceipts?.LastOrDefault(item => item.Receipt.CaseId == state.CaseId)?.Receipt.NewRunId ?? "";
        var currentEvents = currentRun.Length == 0 ? [] : allEvents.Where(item => item.RunId == currentRun).ToArray();
        var window = currentEvents.TakeLast(20).ToArray();
        var history = new List<JourneyEvent>(window.Length);
        foreach (var source in window)
        {
            var ids = new List<string>();
            foreach (var sourceWitness in source.Witnesses ?? [])
            {
                if (sourceWitness is null || sourceWitness.Page < 1 || string.IsNullOrWhiteSpace(sourceWitness.DocumentId) ||
                    !ResolveDocument(published, sourceWitness.DocumentId, out var document)) return null;
                var status = PackageStatus(packages, state.CaseId, document.Path, sourceWitness.DocumentVersion);
                if (!status.Usable || sourceWitness.BasisVersion < 1 || sourceWitness.DocumentVersion < 1)
                    return null;
                var reference = AddRef(refs, state.CaseId, sourceWitness.DocumentId,
                    sourceWitness.DocumentVersion, sourceWitness.BasisId, sourceWitness.BasisVersion,
                    document.Path, status);
                var witness = new EvidenceWitness(sourceWitness.WitnessId, reference.EvidenceRefId,
                    sourceWitness.DocumentId, sourceWitness.DocumentVersion, sourceWitness.Page,
                    sourceWitness.BasisId, sourceWitness.BasisVersion, document.Path);
                if (!witnesses.TryAdd(sourceWitness.WitnessId, witness)) return null;
                ids.Add(sourceWitness.WitnessId);
            }
            history.Add(new JourneyEvent(source.EventId, source.RunId, source.Sequence, source.EventType,
                source.BasisId, source.BasisVersion, source.ActorRole, source.Summary,
                CapabilityLabels.Normalize(source.CapabilityLabel), ids));
        }

        var objects = new List<SceneObject>(4)
        {
            new(aircraftId, "aircraft", state.CaseId, registration, states, owner, next, activityId, label, []),
            new(engineId, "engine", state.CaseId, engineId, states, owner, next, activityId, label, [])
        };
        foreach (var component in published.Components.OrderBy(item => item.ComponentId, StringComparer.Ordinal))
        {
            if (!TrySerial(pack, state.CaseId, "component", component.ComponentId, out var componentId)) return null;
            var evidenceIds = new List<string>();
            foreach (var path in component.EvidenceDocumentPaths)
            {
                var document = published.Documents.SingleOrDefault(item => item.Path == path);
                if (document is null) return null;
                var status = PackageStatus(packages, state.CaseId, path, document.Evidence.Version);
                var reference = AddRef(refs, state.CaseId, document.Evidence.Path, document.Evidence.Version,
                    "basis-v" + basis.Version, basis.Version, document.Path, status);
                evidenceIds.Add(reference.EvidenceRefId);
            }
            objects.Add(new SceneObject(componentId, "component", state.CaseId, component.ComponentId,
                states, owner, next, activityId, label, evidenceIds.Distinct(StringComparer.Ordinal).ToArray()));
        }
        if (objects.Count != 4 || objects.Select(item => item.ObjectId).Distinct(StringComparer.Ordinal).Count() != 4) return null;

        var canReset = label != CapabilityLabels.Unknown && role == "presenter" && state.CaseId == "cp-case-c";
        var allowed = canReset ? new[] { "resetBlockedVariant" } : Array.Empty<string>();
        var name = state.CaseId switch { "cp-case-a" => "Case A", "cp-case-b" => "Case B", "cp-case-c" => "Case C", _ => state.CaseId };
        var summary = new CaseSummary(state.CaseId, name, category, state.Revision, currentRun, states,
            owner, next, activityId, label, true);
        var scene = new SceneEnvelope(ContractVersion, pack.PackVersion, ScenarioDate, summary, allowed, canReset,
            objects, history, allEvents.Any(item => item.RunId != currentRun) || currentEvents.Length > window.Length,
            refs.Values.OrderBy(item => item.EvidenceRefId, StringComparer.Ordinal).ToArray(),
            witnesses.Values.OrderBy(item => item.WitnessId, StringComparer.Ordinal).ToArray());
        return Validate(scene) ? scene : null;
    }

    public static bool Validate(SceneEnvelope? scene) => scene is not null && Validate(JsonSerializer.SerializeToElement(scene, WebJson));
    public static bool Validate(JsonElement scene) => ValidateJson(scene);
    public static bool Validate(string json)
    { try { using var document = JsonDocument.Parse(json); return ValidateJson(document.RootElement); } catch (JsonException) { return false; } }
    public static bool ValidateScene(SceneEnvelope? scene) => Validate(scene);
    public static bool ValidateScene(JsonElement scene) => ValidateJson(scene);
    public static bool IsValid(SceneEnvelope? scene) => Validate(scene);
    public static bool TryBuild(ICaseStateStore store, CaseScope scope, AircraftCasePackSnapshot pack,
        out SceneEnvelope? scene, string? role = null, EvidencePackageStore? packages = null)
    { scene = Build(store, scope, pack, role, packages); return scene is not null; }

    private static bool ValidateJson(JsonElement root)
    {
        if (!Object(root, ["contractVersion", "packVersion", "scenarioDate", "case", "allowedActions", "canResetBlockedVariant", "objects", "history", "olderHistoryAvailable", "documentRefs", "evidence"])) return false;
        if (!Integer(root, "contractVersion") || root.GetProperty("contractVersion").GetInt32() != 1 || !String(root, "packVersion") ||
            !String(root, "scenarioDate") || root.GetProperty("scenarioDate").GetString() != ScenarioDate ||
            !Boolean(root, "canResetBlockedVariant") || !Boolean(root, "olderHistoryAvailable")) return false;
        var allowed = root.GetProperty("allowedActions"); if (allowed.ValueKind != JsonValueKind.Array) return false;
        foreach (var action in allowed.EnumerateArray()) if (action.ValueKind != JsonValueKind.String || action.GetString() != "resetBlockedVariant") return false;
        if (!ValidateCaseSummary(root.GetProperty("case"))) return false;

        var refs = root.GetProperty("documentRefs"); var evidence = root.GetProperty("evidence"); var history = root.GetProperty("history"); var objects = root.GetProperty("objects");
        if (refs.ValueKind != JsonValueKind.Array || evidence.ValueKind != JsonValueKind.Array || history.ValueKind != JsonValueKind.Array || objects.ValueKind != JsonValueKind.Array) return false;
        var refIds = new HashSet<string>(StringComparer.Ordinal); foreach (var item in refs.EnumerateArray()) if (!Ref(item, refIds)) return false;
        var witnessIds = new HashSet<string>(StringComparer.Ordinal); foreach (var item in evidence.EnumerateArray()) if (!Witness(item, refIds, witnessIds, refs)) return false;
        var objectIds = new HashSet<string>(StringComparer.Ordinal); var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in objects.EnumerateArray())
        {
            if (!ObjectType(item)) return false; var id = item.GetProperty("objectId").GetString(); if (id is null || !objectIds.Add(id)) return false;
            if (item.GetProperty("caseId").GetString() != root.GetProperty("case").GetProperty("caseId").GetString()) return false;
            var kind = item.GetProperty("kind").GetString()!; counts[kind] = counts.GetValueOrDefault(kind) + 1;
            foreach (var evidenceId in item.GetProperty("evidenceIds").EnumerateArray())
                if (!evidenceId.ValueKind.Equals(JsonValueKind.String) || !refIds.Contains(evidenceId.GetString()!)) return false;
        }
        if (counts.GetValueOrDefault("aircraft") != 1 || counts.GetValueOrDefault("engine") != 1 || counts.GetValueOrDefault("component") != 2 || counts.Count != 3) return false;
        var eventWitnessIds = new HashSet<string>(StringComparer.Ordinal); long sequence = 0;
        foreach (var item in history.EnumerateArray())
        {
            if (!Journey(item)) return false; var current = item.GetProperty("sequence").GetInt64(); if (current <= 0 || current <= sequence) return false;
            sequence = current;
            foreach (var witnessId in item.GetProperty("witnessIds").EnumerateArray())
            {
                var id = witnessId.GetString(); if (id is null || !witnessIds.Contains(id) || !eventWitnessIds.Add(id)) return false;
            }
        }
        return eventWitnessIds.SetEquals(witnessIds);
    }

    private static bool ValidateCaseSummary(JsonElement item)
    {
        if (!Object(item, ["caseId", "name", "aircraftCategory", "caseRevision", "currentRunId", "status", "owner", "nextAction", "activityId", "capabilityLabel", "synthetic"])) return false;
        foreach (var name in new[] { "caseId", "name", "aircraftCategory", "currentRunId", "capabilityLabel" }) if (!String(item, name)) return false;
        if (!Integer(item, "caseRevision") || item.GetProperty("caseRevision").GetInt64() < 0 || !StringOrNull(item, "owner") || !StringOrNull(item, "activityId") || !Boolean(item, "synthetic") || !item.GetProperty("synthetic").GetBoolean()) return false;
        if (item.GetProperty("aircraftCategory").GetString() is not ("narrow-body" or "regional" or "wide-body") || !LabelValid(item.GetProperty("capabilityLabel").GetString())) return false;
        return States(item.GetProperty("status")) && ActionOrNull(item.GetProperty("nextAction"));
    }
    private static bool ObjectType(JsonElement item)
    {
        if (!Object(item, ["objectId", "kind", "caseId", "displayName", "status", "owner", "nextAction", "activityId", "capabilityLabel", "evidenceIds"])) return false;
        foreach (var name in new[] { "objectId", "kind", "caseId", "displayName", "capabilityLabel" }) if (!String(item, name)) return false;
        if (item.GetProperty("kind").GetString() is not ("aircraft" or "engine" or "component") || !StringOrNull(item, "owner") || !StringOrNull(item, "activityId") || !LabelValid(item.GetProperty("capabilityLabel").GetString())) return false;
        var ids = item.GetProperty("evidenceIds"); if (ids.ValueKind != JsonValueKind.Array) return false;
        foreach (var id in ids.EnumerateArray()) if (id.ValueKind != JsonValueKind.String) return false;
        return States(item.GetProperty("status")) && ActionOrNull(item.GetProperty("nextAction"));
    }
    private static bool Ref(JsonElement item, HashSet<string> ids)
    {
        if (!Object(item, ["evidenceRefId", "documentId", "documentVersion", "basisId", "basisVersion", "displayLabel", "processingState", "retryable", "usableAsEvidence"])) return false;
        foreach (var name in new[] { "evidenceRefId", "documentId", "basisId", "displayLabel", "processingState" }) if (!String(item, name)) return false;
        if (!Integer(item, "documentVersion") || !Integer(item, "basisVersion") || item.GetProperty("documentVersion").GetInt32() < 1 || item.GetProperty("basisVersion").GetInt32() < 1 || !Boolean(item, "retryable") || !Boolean(item, "usableAsEvidence")) return false;
        var state = item.GetProperty("processingState").GetString();
        if (state is not ("accepted" or "rejected-retryable" or "rejected-permanent" or "unprocessed") ||
            (state == "accepted" ? (!item.GetProperty("usableAsEvidence").GetBoolean() || item.GetProperty("retryable").GetBoolean())
                : item.GetProperty("usableAsEvidence").GetBoolean())) return false;
        return ids.Add(item.GetProperty("evidenceRefId").GetString()!);
    }
    private static bool Witness(JsonElement item, HashSet<string> refs, HashSet<string> ids, JsonElement refArray)
    {
        if (!Object(item, ["witnessId", "evidenceRefId", "documentId", "documentVersion", "page", "basisId", "basisVersion", "displayLabel"])) return false;
        foreach (var name in new[] { "witnessId", "evidenceRefId", "documentId", "basisId", "displayLabel" }) if (!String(item, name)) return false;
        if (!Integer(item, "documentVersion") || !Integer(item, "basisVersion") || !Integer(item, "page") || item.GetProperty("documentVersion").GetInt32() < 1 || item.GetProperty("basisVersion").GetInt32() < 1 || item.GetProperty("page").GetInt32() < 1) return false;
        var refId = item.GetProperty("evidenceRefId").GetString(); if (refId is null || !refs.Contains(refId)) return false;
        var match = refArray.EnumerateArray().Any(reference => reference.GetProperty("evidenceRefId").GetString() == refId &&
            reference.GetProperty("documentId").GetString() == item.GetProperty("documentId").GetString() && reference.GetProperty("documentVersion").GetInt32() == item.GetProperty("documentVersion").GetInt32() &&
            reference.GetProperty("basisId").GetString() == item.GetProperty("basisId").GetString() && reference.GetProperty("basisVersion").GetInt32() == item.GetProperty("basisVersion").GetInt32() && reference.GetProperty("usableAsEvidence").GetBoolean());
        return match && ids.Add(item.GetProperty("witnessId").GetString()!);
    }
    private static bool Journey(JsonElement item)
    {
        if (!Object(item, ["eventId", "runId", "sequence", "eventType", "basisId", "basisVersion", "actorRole", "summary", "capabilityLabel", "witnessIds"])) return false;
        foreach (var name in new[] { "eventId", "runId", "eventType", "actorRole", "summary", "capabilityLabel" }) if (!String(item, name)) return false;
        if (!Integer(item, "sequence") || item.GetProperty("sequence").GetInt64() <= 0 || !StringOrNull(item, "basisId") || !LabelValid(item.GetProperty("capabilityLabel").GetString())) return false;
        var basisIdKind = item.GetProperty("basisId").ValueKind;
        var basisVersionKind = item.GetProperty("basisVersion").ValueKind;
        if (basisIdKind == JsonValueKind.Null ? basisVersionKind != JsonValueKind.Null : basisVersionKind != JsonValueKind.Number) return false;
        if (basisVersionKind == JsonValueKind.Number && (!item.GetProperty("basisVersion").TryGetInt32(out var basisVersion) || basisVersion < 1)) return false;
        var ids = item.GetProperty("witnessIds"); if (ids.ValueKind != JsonValueKind.Array) return false;
        foreach (var id in ids.EnumerateArray()) if (id.ValueKind != JsonValueKind.String) return false;
        return true;
    }
    private static bool States(JsonElement item) => Object(item, ["assessment", "review", "request", "finance", "legal", "acceptance"]) &&
        item.EnumerateObject().All(property => property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null);
    private static bool ActionOrNull(JsonElement item) => item.ValueKind == JsonValueKind.Null || (Object(item, ["actionId", "text"])
        && String(item, "actionId") && String(item, "text") && !string.IsNullOrWhiteSpace(item.GetProperty("actionId").GetString()));
    private static bool Object(JsonElement item, IReadOnlyList<string> keys)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        var actual = item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        var expected = keys.Order(StringComparer.Ordinal).ToArray(); return actual.SequenceEqual(expected, StringComparer.Ordinal);
    }
    private static bool String(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;
    private static bool StringOrNull(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Null;
    private static bool Integer(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _);
    private static bool Boolean(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool LabelValid(string? label) => label is "AI agent" or "Rules-based" or "Simulated" or "Design-only" or "Unknown";

    private static bool OwnedBySameCase(CaseState state) =>
        (state.Assessments ?? []).All(item => item.CaseId == state.CaseId) && (state.RequestReceipts ?? []).All(item => item.CaseId == state.CaseId) &&
        (state.WorkItems ?? []).All(item => item.CaseId == state.CaseId) && (state.Traces ?? []).All(item => item.CaseId == state.CaseId) &&
        (state.JourneyEvents ?? []).All(item => item.CaseId == state.CaseId) &&
        (state.BlockedVariantResetReceipts ?? []).All(item => item.Receipt.CaseId == state.CaseId);
    private static string Label(CaseState state)
    {
        var label = (state.JourneyEvents ?? []).Where(item => item.CaseId == state.CaseId).OrderBy(item => item.Sequence).LastOrDefault()?.CapabilityLabel;
        return CapabilityLabels.Normalize(label);
    }
    private static CaseStates CaseStatus(CaseState state, BasisVersion basis)
    {
        var assessment = (state.Assessments ?? []).Where(item => item.CaseId == state.CaseId && item.BasisVersion == basis.Version).LastOrDefault()?.Outcome;
        var review = (state.WorkItems ?? []).Where(item => item.CaseId == state.CaseId && item.CapabilityId == AssessmentExecutionService.ReviewCapability).LastOrDefault()?.State;
        var request = (state.RequestReceipts ?? []).Any(item => item.CaseId == state.CaseId && item.BasisVersion == basis.Version) ? "delivered" : null;
        var finance = Decision(state, "reserve.decided", "approve", "reject");
        var legal = Decision(state, "obligation.legal-decided", "signed-off", "rejected");
        var acceptance = TechnicalReviewPolicy.Decisions(state).LastOrDefault()?.Decision;
        return new CaseStates(assessment, review, request, finance, legal, acceptance);
    }
    private static string? Decision(CaseState state, string action, params string[] tokens)
    {
        var audit = state.Audit.LastOrDefault(item => item.Action == action); if (audit is null) return null;
        try { using var json = JsonDocument.Parse(audit.Detail); var root = json.RootElement; if ((root.TryGetProperty("decision", out var value) || root.TryGetProperty("Decision", out value)) && value.ValueKind == JsonValueKind.String) return tokens.Contains(value.GetString(), StringComparer.Ordinal) ? value.GetString() : null; }
        catch (JsonException) { }
        return null;
    }
    private readonly record struct ReferenceStatus(string State, bool Retryable, bool Usable);
    private static ReferenceStatus PackageStatus(EvidencePackageStore? packages, string caseId, string path, int expectedVersion = 1)
    {
        if (packages is null) return new("accepted", false, true);
        var package = packages.ListForCase(caseId).LastOrDefault();
        var document = package?.Documents?.LastOrDefault(item => item.FileName == path || item.FileName == Path.GetFileName(path) || path.EndsWith('/' + item.FileName, StringComparison.Ordinal));
        return document?.Status switch { "accepted" => document.Version == expectedVersion ? new("accepted", false, true) : new("unprocessed", true, false), "rejected-retryable" => new("rejected-retryable", true, false), "rejected-permanent" => new("rejected-permanent", false, false), _ => new("unprocessed", true, false) };
    }
    private static EvidenceReference AddRef(Dictionary<string, EvidenceReference> refs, string caseId, string documentId,
        int documentVersion, string basisId, int basisVersion, string displayLabel, ReferenceStatus status)
    {
        var identity = string.Join('\0', caseId, documentId, documentVersion, basisId, basisVersion);
        var id = "cp-ref-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..16];
        if (!refs.TryGetValue(id, out var reference))
        {
            reference = new EvidenceReference(id, documentId, documentVersion, basisId, basisVersion, displayLabel,
                status.State, status.Retryable, status.Usable); refs.Add(id, reference);
        }
        else if (reference.ProcessingState != status.State || reference.UsableAsEvidence != status.Usable || reference.Retryable != status.Retryable)
            throw new InvalidDataException("Document reference tuple has inconsistent state.");
        return reference;
    }
    private static bool ResolveDocument(PublishedAircraftCasePackCase published, string documentId,
        out PublishedAircraftCasePackDocument document)
    {
        document = published.Documents.FirstOrDefault(item => item.Evidence.Path == documentId || item.Evidence.BlobName == documentId || item.Path == documentId)!;
        return document is not null && !string.IsNullOrWhiteSpace(document.Evidence.Path);
    }
    private static bool TrySerial(AircraftCasePackSnapshot pack, string caseId, string kind, string? componentId, out string serial)
    {
        serial = ""; var record = pack.Manifest.Cases.SingleOrDefault(item => item.CaseId == caseId);
        var match = record?.SerialNumbers?.SingleOrDefault(item => item.Kind == kind && (kind != "component" || item.ComponentId == componentId));
        serial = match?.Value ?? ""; return !string.IsNullOrWhiteSpace(serial);
    }
}
