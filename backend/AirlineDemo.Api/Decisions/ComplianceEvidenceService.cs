using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Decisions;

public sealed record EvidenceReference(string SourceId, string? RecordId, int? RecordVersion, string Access);
public sealed record EvidenceDecision(long Sequence, string AuditEventId, string ActorId, string Role,
    DateTimeOffset At, string Decision);
public sealed record RegimeRequirement(string RequirementId, int? RequirementVersion, string? ResponsibleParty,
    string Status, EvidenceReference? Applicability, IReadOnlyList<EvidenceReference> Evidence,
    EvidenceDecision? Decision);
public sealed record RegimeEvidence(string Regime, IReadOnlyList<RegimeRequirement> Requirements);
public sealed record EvidenceResponsibilities(IReadOnlyList<string> Platform, IReadOnlyList<string> Customer, string Limitation);
public sealed record ComplianceEvidence(int ContractVersion, string CaseId, long CaseRevision, int? BasisVersion,
    bool CurrentBasis, string ApplicabilityAccess, IReadOnlyList<RegimeEvidence> Regimes,
    IReadOnlyList<RegimeRequirement> Untagged, EvidenceDecision? BasisDecision,
    IReadOnlyList<EvidenceDecision> Decisions, EvidenceResponsibilities Responsibilities);

/// <summary>A metadata-only projection of one immutable decision basis; it never reads live source systems.</summary>
public static class ComplianceEvidenceService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Pin(string SourceId, string RecordId, int Version, string Kind, bool Stale, JsonElement Fields);
    private sealed record Tag(Pin Pin, string Id, string? Regime, string? Party, bool Valid);
    private sealed record Flag(Pin Pin, SpecialistReviewFlag Value);
    public static readonly EvidenceResponsibilities Responsibilities = new(
        ["Access control", "Audit trail", "Pinned evidence"],
        ["Determine applicable obligations", "Review evidence and record decisions", "Obtain specialist advice"],
        "Evidence only. Not a certification, approval or legal conclusion. The customer remains responsible for its decisions.");

    public static ComplianceEvidence Build(CaseState state, string role, long? decisionId = null)
    {
        var decisions = state.Audit.Where(a => a.Action == TechnicalReviewPolicy.DecisionAction)
            .Select(a => (Audit: a, Detail: JsonSerializer.Deserialize<DecisionDetail>(a.Detail)
                ?? throw new InvalidDataException())).ToArray();
        if (decisions.Select(d => d.Audit.Sequence).Distinct().Count() != decisions.Length)
            throw new InvalidDataException();
        foreach (var (audit, detail) in decisions)
            if (detail.Role != CaseEndpoints.ReviewerRole || detail.PolicyVersion != TechnicalReviewPolicy.Version
                || string.IsNullOrWhiteSpace(detail.AuditEventId) || string.IsNullOrWhiteSpace(audit.Actor)
                || audit.At == default || detail.Decision is not ("accept" or "reject")
                || detail.Outcome != (detail.Decision == "accept" ? "approved" : "rejected"))
                throw new InvalidDataException();
        var selected = decisionId.HasValue
            ? decisions.SingleOrDefault(d => d.Audit.Sequence == decisionId)
            : decisions.OrderBy(d => d.Audit.Sequence).LastOrDefault();
        if (decisionId.HasValue && selected.Audit is null) throw new InvalidDataException();
        var version = selected.Detail?.BasisVersion ?? state.Basis.LastOrDefault()?.Version;
        BasisVersion? basis = null;
        if (version.HasValue)
        {
            basis = state.Basis.SingleOrDefault(b => b.Version == version) ?? throw new InvalidDataException();
            if (!DecisionDossierService.ValidBasisDigest(state.Basis, basis)
                || selected.Detail is { } detail && (detail.BasisId != $"basis-v{basis.Version}"
                    || detail.BasisDigest != basis.BasisDigest))
                throw new InvalidDataException();
        }
        var pins = basis is null ? [] : ReadPins(state.CaseId, basis.Content);
        var canReadTags = MetadataAllowed(role, "technical");
        var tags = canReadTags ? pins.Where(p => p.SourceId == "technical" && p.Kind == "applicability")
            .Select(p =>
            {
                var fields = Strings(p.Fields);
                var valid = ApplicabilityPayloadValidator.Validate(fields).Count == 0;
                var regime = fields.GetValueOrDefault("regime");
                return new Tag(p, fields.GetValueOrDefault("requirementId") ?? p.RecordId,
                    valid && ApplicabilityPayloadValidator.Regimes.Contains(regime, StringComparer.Ordinal) ? regime : null,
                    valid ? fields.GetValueOrDefault("responsibleParty") : null, valid);
            }).ToArray() : [];
        var flags = pins.Where(p => p.SourceId == "contracts" && p.Kind == "specialist_flag")
            .Select(p => new Flag(p, SpecialistReviewFlag.Parse(Strings(p.Fields)))).ToArray();
        var decision = selected.Audit is null ? null : ToDecision(selected.Audit, selected.Detail!);
        var auditDetails = state.Audit.Where(a => a.Sequence < (decision?.Sequence ?? long.MaxValue)
                && a.Action == "assessment" && a.Actor == AssessmentExecutionService.Actor)
            .Select(a => a.Detail).ToHashSet(StringComparer.Ordinal);
        var assessments = (state.Assessments ?? []).Where(a => a.CaseId == state.CaseId
            && a.BasisVersion == version && a.BasisId == $"basis-v{version}"
            && auditDetails.Contains($"assessment {a.AssessmentId} recorded with outcome {a.Outcome} on basis {a.BasisId} v{a.BasisVersion}")).ToArray();
        var witnesses = pins.Where(p => p.Kind != "applicability")
            .ToDictionary(p => $"source:{p.SourceId}:{p.RecordId}:v{p.Version}", StringComparer.Ordinal);
        var documents = pins.Where(p => p.SourceId == "records" && !string.IsNullOrWhiteSpace(Text(p.Fields, "documentVersion"))
                && Text(p.Fields, "containsPersonalData") is "true" or "false")
            .GroupBy(p => Text(p.Fields, "documentId")).Where(g => g.Key is not null && g.Count() == 1)
            .ToDictionary(g => g.Key!, g => g.Single(), StringComparer.Ordinal);
        Pin? Resolve(AssessedWitness witness) => witness.Page == 0
            ? witnesses.GetValueOrDefault(witness.DocumentId)
            : witness.Page > 0 ? documents.GetValueOrDefault(witness.DocumentId) : null;
        var tagCounts = tags.GroupBy(t => (t.Id, t.Regime)).ToDictionary(g => g.Key, g => g.Count());
        var flagGroups = flags.GroupBy(f => (f.Value.RelatedRequirementId, RegimeFor(f.Value)))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var claims = assessments.SelectMany(a => a.Claims.Select(c => (Assessment: a, Claim: c)))
            .GroupBy(x => (x.Claim.RequirementId, x.Claim.EvidenceRegime))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var supplements = new Dictionary<string, List<Pin>>(StringComparer.Ordinal)
            { ["GDPR"] = [], ["Cape Town"] = [], ["IFRS 16"] = [] };
        foreach (var pin in pins)
        {
            if (pin.SourceId == "records" && Text(pin.Fields, "containsPersonalData") == "true")
                supplements["GDPR"].Add(pin);
            if (pin.SourceId == "contracts" && pin.Kind == "cape_town_interest")
                supplements["Cape Town"].Add(pin);
            if (pin.SourceId == "finance" && pin.Kind is "rent" or "maintenance_reserve" or "security_deposit" or "reserve_reimbursement")
                supplements["IFRS 16"].Add(pin);
        }
        var requirements = tags.Select(tag =>
        {
            var evidence = new List<Pin>();
            var matchingFlags = flagGroups.GetValueOrDefault((tag.Id, tag.Regime)) ?? [];
            evidence.AddRange(matchingFlags.Select(f => f.Pin));
            if (matchingFlags.Length > 0 && tag.Regime is not null && supplements.TryGetValue(tag.Regime, out var extra))
                evidence.AddRange(extra);
            var matching = claims.GetValueOrDefault((tag.Id, tag.Regime)) ?? [];
            var supported = matching.Length > 0;
            foreach (var (assessment, claim) in matching)
            {
                var resolved = claim.Witnesses.Select(Resolve).ToArray();
                evidence.AddRange(resolved.OfType<Pin>());
                supported &= assessment.Outcome == "Baseline" && claim.Kind == "Baseline"
                    && claim.RequirementStatus is null && claim.SpecialistResult is null
                    && resolved.Length > 0 && resolved.All(p => p is { Stale: false });
            }
            var completeTags = tag.Valid && tag.Regime is not null && tag.Party is not (null or "" or "unassigned")
                && tagCounts[(tag.Id, tag.Regime)] == 1 && !tag.Pin.Stale;
            var status = !completeTags || evidence.Count == 0 || evidence.Any(p => p.Stale || !MetadataAllowed(role, p.SourceId)) ? "needs_review"
                : matchingFlags.Length > 0
                    ? matchingFlags.Length == 1 && matchingFlags[0].Value.Result == "not_assessed" ? "not_assessed" : "needs_review"
                    : supported && decision?.Decision == "accept" ? "met_by_evidence" : "needs_review";
            return (tag.Regime, Requirement: new RegimeRequirement(tag.Id, tag.Pin.Version, tag.Party, status,
                Reference(tag.Pin, role), evidence.Distinct().Select(p => Reference(p, role)).ToArray(),
                matching.Length > 0 ? decision : null));
        }).ToArray();
        var known = tags.Select(t => (t.Id, t.Regime)).ToHashSet();
        var untagged = requirements.Where(r => r.Regime is null).Select(r => r.Requirement)
            .Concat(flags.Where(f => !known.Contains((f.Value.RelatedRequirementId, RegimeFor(f.Value))) && MetadataAllowed(role, "contracts"))
                .Select(f => new RegimeRequirement(f.Value.RelatedRequirementId, null, null, "needs_review", null,
                    [Reference(f.Pin, role)], null)))
            .Concat(canReadTags ? claims.Where(c => c.Key.RequirementId is not null && !known.Contains(c.Key))
                .Select(c => new RegimeRequirement(c.Key.RequirementId!, null, null, "needs_review", null,
                    c.Value.SelectMany(x => x.Claim.Witnesses).Select(Resolve).OfType<Pin>().Distinct()
                        .Select(p => Reference(p, role)).ToArray(), decision)) : []).ToArray();
        return new(1, state.CaseId, state.Revision, version, version == state.Basis.LastOrDefault()?.Version,
            canReadTags ? "permitted" : "not_permitted",
            requirements.Where(r => r.Regime is not null).GroupBy(r => r.Regime!, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new RegimeEvidence(g.Key, g.Select(r => r.Requirement).OrderBy(r => r.RequirementId, StringComparer.Ordinal).ToArray())).ToArray(),
            untagged, decision, decisions.Select(d => ToDecision(d.Audit, d.Detail)).ToArray(), Responsibilities);
    }

    private static EvidenceDecision ToDecision(AuditEvent audit, DecisionDetail detail) =>
        new(audit.Sequence, detail.AuditEventId, audit.Actor, detail.Role, audit.At, detail.Decision);

    private static bool MetadataAllowed(string role, string source) =>
        role == "presenter" || SourceReadEndpoints.Allowed(role, source);

    private static EvidenceReference Reference(Pin pin, string role)
    {
        var permitted = SourceReadEndpoints.Allowed(role, pin.SourceId);
        var metadata = MetadataAllowed(role, pin.SourceId);
        return new(pin.SourceId, metadata ? pin.RecordId : null, metadata ? pin.Version : null,
            permitted ? "permitted" : "not_permitted");
    }

    private static string? RegimeFor(SpecialistReviewFlag flag) => flag.Topic switch
    {
        "sanctions" => $"{flag.Regime} sanctions",
        "cape_town" => "Cape Town",
        "gdpr" => "GDPR",
        "ifrs16" => "IFRS 16",
        _ => null,
    };

    private static Pin[] ReadPins(string caseId, string content)
    {
        if (!content.TrimStart().StartsWith('{')) return [];
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (Text(root, "format") != "source-record-pins-v1") return [];
        var sourcePins = root.GetProperty("pins").Deserialize<SourceBasisPin[]>(Json) ?? throw new InvalidDataException();
        var pins = new List<Pin>();
        var identities = new HashSet<(string, string)>();
        foreach (var pin in sourcePins)
        {
            if (pin is null || !SourceModes.Sources.Contains(pin.SourceId, StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(pin.RecordId) || pin.RecordVersion < 1
                || pin.ContentDigest != Digest(pin.Content) || !identities.Add((pin.SourceId, pin.RecordId)))
                throw new InvalidDataException();
            using var record = JsonDocument.Parse(pin.Content);
            var r = record.RootElement;
            if (Text(r, "caseId") != caseId || Text(r, "sourceId") != pin.SourceId
                || Text(r, "recordId") != pin.RecordId || r.GetProperty("recordVersion").GetInt32() != pin.RecordVersion
                || r.GetProperty("synthetic").ValueKind != JsonValueKind.True)
                throw new InvalidDataException();
            var payload = r.GetProperty("payload");
            var fields = payload.TryGetProperty("fields", out var nested) ? nested : payload;
            pins.Add(new(pin.SourceId, pin.RecordId, pin.RecordVersion,
                Text(r, "kind") ?? throw new InvalidDataException(),
                !r.TryGetProperty("stale", out var stale) || stale.ValueKind != JsonValueKind.False, fields.Clone()));
        }
        return pins.ToArray();
    }

    private static Dictionary<string, string> Strings(JsonElement fields) =>
        fields.EnumerateObject().ToDictionary(p => p.Name,
            p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : throw new InvalidDataException(),
            StringComparer.Ordinal);

    private static string? Text(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    private static string Digest(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
