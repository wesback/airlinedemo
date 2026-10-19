using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;

namespace AirlineDemo.Api.Decisions;

public sealed record DossierDocument(string DocumentId, string DocumentVersion);
public sealed record DossierSourcePin(string SourceId, string RecordId, int RecordVersion);
public sealed record DossierRequirement(string Id, int Version, string Regime, string ResponsibleParty);
public sealed record DossierBasis(string BasisId, int BasisVersion, IReadOnlyList<DossierDocument> Documents,
    IReadOnlyList<DossierSourcePin> SourcePins, IReadOnlyList<DossierRequirement> Requirements);
public sealed record DossierDecision(string ActorId, string Role, DateTimeOffset Time, string Reason, string Outcome);
public sealed record DecisionDossier(int ContractVersion, string CaseId, long DecisionId, DateTimeOffset ExportedAt,
    DossierBasis Basis, string PolicyVersion, string? TraceId, DossierDecision Decision,
    IReadOnlyList<string> AuditEventIds, string ContentHash, bool Synthetic, IReadOnlyList<string> Limitations);
public enum DossierResultKind { Success, NotFound, Incomplete }
public sealed record DossierResult(DossierResultKind Kind, DecisionDossier? Dossier = null);

public interface IDecisionDossierService
{
    DossierResult Export(CaseScope scope, string caseId, long decisionId, string role);
}

public sealed class DecisionDossierService(ICaseStateStore store, TimeProvider timeProvider) : IDecisionDossierService
{
    private const string SourceBasisFormat = "source-record-pins-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] Limitations =
    [
        "Not a certification.",
        "Not an approval.",
        "Not a legal conclusion.",
        "The SHA-256 contentHash is a checksum only and does not prove publisher identity or origin.",
    ];

    public DossierResult Export(CaseScope scope, string caseId, long decisionId, string role)
    {
        var snapshot = store.Read(scope);
        if (!string.Equals(snapshot.CaseId, caseId, StringComparison.Ordinal))
            return new(DossierResultKind.NotFound);

        var events = snapshot.Audit.Where(item => item.Sequence == decisionId).ToArray();
        if (events.Length != 1 || events[0].Action != TechnicalReviewPolicy.DecisionAction)
            return new(DossierResultKind.NotFound);

        DecisionDetail? detail;
        try { detail = JsonSerializer.Deserialize<DecisionDetail>(events[0].Detail); }
        catch (JsonException) { return new(DossierResultKind.Incomplete); }
        if (detail is null || string.IsNullOrWhiteSpace(detail.Role)) return new(DossierResultKind.Incomplete);
        if (!string.Equals(detail.Role, role, StringComparison.Ordinal)) return new(DossierResultKind.NotFound);
        if (string.IsNullOrWhiteSpace(detail.AuditEventId) || string.IsNullOrWhiteSpace(detail.BasisId)
            || string.IsNullOrWhiteSpace(detail.PolicyVersion) || string.IsNullOrWhiteSpace(detail.BasisDigest)
            || string.IsNullOrWhiteSpace(detail.Reason) || string.IsNullOrWhiteSpace(events[0].Actor)
            || events[0].At == default || detail.BasisVersion < 1
            || detail.Decision is not ("accept" or "reject")
            || detail.Outcome is not ("approved" or "rejected")
            || (detail.Decision == "accept") != (detail.Outcome == "approved"))
            return new(DossierResultKind.Incomplete);

        var foundBasis = TryGetBasis(snapshot.Basis, detail.BasisVersion, out var basis);
        var sameDigest = foundBasis && string.Equals(basis.BasisDigest, detail.BasisDigest, StringComparison.Ordinal);
        var sameId = foundBasis && string.Equals(detail.BasisId, "basis-v" + basis.Version, StringComparison.Ordinal);
        var validDigest = foundBasis && ValidBasisDigest(snapshot.Basis, basis);
        if (!foundBasis || !sameDigest || !sameId || !validDigest)
            return new(DossierResultKind.Incomplete);
        if (!TryReadPins(caseId, basis.Content, out var pins, out var documents, out var requirements)
            || documents.Count == 0 || requirements.Count == 0)
            return new(DossierResultKind.Incomplete);

        var basisView = new DossierBasis(detail.BasisId, basis.Version, documents, pins, requirements);
        var decision = new DossierDecision(events[0].Actor, detail.Role, events[0].At, detail.Reason, detail.Outcome);
        var hashView = new DossierHashView(1, caseId, decisionId, basisView, detail.PolicyVersion,
            string.IsNullOrWhiteSpace(detail.TraceId) ? null : detail.TraceId, decision, [detail.AuditEventId], true, Limitations);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(hashView, JsonOptions))).ToLowerInvariant();
        return new(DossierResultKind.Success, new DecisionDossier(1, caseId, decisionId, timeProvider.GetUtcNow(),
            basisView, detail.PolicyVersion, hashView.TraceId, decision, [detail.AuditEventId], hash, true, Limitations));
    }
    private static bool TryGetBasis(IReadOnlyList<BasisVersion> versions, int version, out BasisVersion basis)
    {
        basis = null!;
        if (version > versions.Count || version < 1 || versions[version - 1].Version != version) return false;
        basis = versions[version - 1];
        return true;
    }
    private static bool ValidBasisDigest(IReadOnlyList<BasisVersion> versions, BasisVersion basis)
    {
        var contentDigest = Digest(basis.Content).ToUpperInvariant();
        var previous = basis.Version == 1 ? string.Empty : versions[basis.Version - 2].BasisDigest;
        return string.Equals(contentDigest, basis.ContentDigest, StringComparison.Ordinal)
            && string.Equals(Digest(previous + "|" + contentDigest).ToUpperInvariant(), basis.BasisDigest, StringComparison.Ordinal);
    }
    private static bool TryReadPins(string caseId, string content, out IReadOnlyList<DossierSourcePin> pins,
        out IReadOnlyList<DossierDocument> documents, out IReadOnlyList<DossierRequirement> requirements)
    {
        var pinList = new List<DossierSourcePin>();
        var documentList = new List<DossierDocument>();
        var requirementList = new List<DossierRequirement>();
        pins = pinList.AsReadOnly();
        documents = documentList.AsReadOnly();
        requirements = requirementList.AsReadOnly();
        try
        {
            using var basis = JsonDocument.Parse(content);
            if (basis.RootElement.ValueKind != JsonValueKind.Object
                || !basis.RootElement.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String
                || format.GetString() != SourceBasisFormat
                || !basis.RootElement.TryGetProperty("pins", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            foreach (var pin in values.EnumerateArray())
            {
                if (pin.ValueKind != JsonValueKind.Object || !Text(pin, "sourceId", out var sourceId) || !Text(pin, "recordId", out var recordId)
                    || !Number(pin, "recordVersion", out var version) || version < 1
                    || !Text(pin, "content", out var recordContent) || !Text(pin, "contentDigest", out var recordDigest)
                    || !string.Equals(Digest(recordContent), recordDigest, StringComparison.Ordinal))
                {
                    return false;
                }
                if (!TryRecord(caseId, sourceId, recordId, version, recordContent, out var document, out var requirement))
                {
                    return false;
                }
                pinList.Add(new DossierSourcePin(sourceId, recordId, version));
                if (document is not null) documentList.Add(document);
                if (requirement is not null) requirementList.Add(requirement);
            }
        }
        catch (JsonException) { return false; }
        pins = pinList.OrderBy(pin => pin.SourceId, StringComparer.Ordinal).ThenBy(pin => pin.RecordId, StringComparer.Ordinal)
            .ThenBy(pin => pin.RecordVersion).ToArray();
        documents = documentList.Distinct().OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .ThenBy(item => item.DocumentVersion, StringComparer.Ordinal).ToArray();
        requirements = requirementList.Distinct().OrderBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Version).ToArray();
        return pinList.Count > 0;
    }
    private static bool TryRecord(string caseId, string sourceId, string recordId, int version, string content,
        out DossierDocument? document, out DossierRequirement? requirement)
    {
        document = null;
        requirement = null;
        using var record = JsonDocument.Parse(content);
        var root = record.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !Text(root, "caseId", out var recordCase) || recordCase != caseId
            || !Text(root, "sourceId", out var recordSource) || recordSource != sourceId
            || !Text(root, "recordId", out var recordIdentity) || recordIdentity != recordId
            || !Number(root, "recordVersion", out var recordVersion) || recordVersion != version
            || !root.TryGetProperty("synthetic", out var synthetic) || synthetic.ValueKind != JsonValueKind.True
            || !Text(root, "kind", out var kind) || !root.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object)
            return false;

        if (sourceId == "records")
        {
            if (!Text(payload, "documentId", out var id) || !Text(payload, "documentVersion", out var docVersion)
                || !root.TryGetProperty("payload", out payload)
                || !payload.TryGetProperty("containsPersonalData", out var personalData)
                || personalData.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            document = new DossierDocument(id, docVersion);
        }
        if (sourceId == "technical" && kind == "applicability")
        {
            if (!payload.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object
                || !Text(fields, "requirementId", out var id) || !Text(fields, "regime", out var regime)
                || !Text(fields, "responsibleParty", out var party))
                return false;
            requirement = new DossierRequirement(id, version, regime, party);
        }
        return true;
    }
    private static bool Text(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString()!);
    }
    private static bool Number(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }
    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record DossierHashView(int ContractVersion, string CaseId, long DecisionId, DossierBasis Basis,
        string PolicyVersion, string? TraceId, DossierDecision Decision, IReadOnlyList<string> AuditEventIds,
        bool Synthetic, IReadOnlyList<string> Limitations);
}
