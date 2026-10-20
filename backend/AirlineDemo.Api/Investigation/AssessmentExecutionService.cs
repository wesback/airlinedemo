using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Investigation;

/// <summary>One typed model claim: the kind, typed value, identity and cited witnesses. No prompt or response text.</summary>
public sealed record AssessmentClaim(
    ClaimKind Kind,
    string Value,
    string Component,
    string SerialNumber,
    IReadOnlyList<DocumentWitness> Witnesses,
    string? RelatedRequirementId = null,
    string? Regime = null,
    bool ReliesOnCapeTownEvidence = false,
    string? RequirementId = null,
    string? EvidenceRegime = null);

/// <summary>The accepted evidence basis an assessment runs against, with its completeness and identity state.</summary>
public sealed record AssessmentBasis(
    string BasisId,
    int BasisVersion,
    string ScenarioTime,
    ProcessingCoverage Coverage,
    bool IdentityAmbiguous,
    IReadOnlyDictionary<string, BasisDocument> Documents,
    IReadOnlyList<SourceBasisPin>? SourcePins = null);

public sealed record AssessedWitness(string DocumentId, int Page);

public sealed record AssessedClaim(
    string Kind,
    string Value,
    string Component,
    string SerialNumber,
    IReadOnlyList<AssessedWitness> Witnesses,
    string? RelatedRequirementId = null,
    string? SpecialistResult = null,
    IReadOnlyList<CapeTownInterestEvidence>? CapeTownEvidence = null,
    string? RequirementId = null,
    string? EvidenceRegime = null,
    string? RequirementStatus = null,
    RequirementApplicability? Applicability = null);

public sealed record RequirementApplicability(string? Regime, string? ResponsibleParty);

/// <summary>
/// The stored result of one assessment. It holds typed values and source identifiers only: no witness quote,
/// document text or prompt.
/// </summary>
public sealed record AssessmentRecord(
    string CaseId,
    string BasisId,
    int BasisVersion,
    string Outcome,
    string Reason,
    IReadOnlyList<AssessedClaim> Claims,
    string? RequestId,
    string? WorkItemId,
    string? AssessmentId = null);

/// <summary>
/// Validates typed claims against the current basis and applies the deterministic policy. A single supported
/// missing requirement on complete coverage creates one request through the mock inbox. A supported baseline creates
/// nothing. Every other case creates one open <c>records-investigation</c> item owned by <c>technical-reviewer</c>.
/// </summary>
public sealed class AssessmentExecutionService(
    ICaseStateStore store,
    MockRequestInbox inbox,
    AssessmentStore assessments,
    ISourceSystem<TechnicalPayload>? technical = null,
    ISourceSystem<MaintenancePayload>? maintenance = null,
    ISourceSystem<FinancePayload>? finance = null,
    ISourceSystem<RecordsPayload>? records = null,
    ISourceSystem<ContractsPayload>? contracts = null,
    SourceModeStore? sourceModes = null)
{
    public const string Baseline = "Baseline";
    public const string Request = "Request";
    public const string Review = "Review";
    public const string ReviewCapability = "records-investigation";
    public const string ReviewOwner = "technical-reviewer";
    public const string Actor = "assessment-service";
    private const string ScriptElementClaim = "<script>send_to_recipient()</script>";
    private const string ScriptElementClaimReason = "script element in missing requirement claim";

    private static readonly IReadOnlyDictionary<string, BasisDocument> NoDocuments =
        new Dictionary<string, BasisDocument>(StringComparer.Ordinal);

    public AssessmentRecord Assess(CaseScope scope, AssessmentBasis basis, IReadOnlyList<AssessmentClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(claims);

        var sourceModeFinding = new SourceModeFindingResult(null, new Dictionary<int, IReadOnlyList<AssessedWitness>>());
        var persisted = store.Read(scope);
        if (HasAnySourceSystem)
        {
            EnsureSourceSystemsConfigured();
            var modes = sourceModes?.ModesFor(persisted)
                ?? SourceModes.Sources.ToDictionary(sourceId => sourceId, _ => SourceModes.Available, StringComparer.Ordinal);
            var anyUnavailable = modes.Values.Contains("unavailable", StringComparer.Ordinal);
            if (!anyUnavailable)
            {
                var gathered = EvidenceIntake.GatherSourceBasisAsync(
                    scope.CaseId,
                    SourceModeFindingPolicy.Apply(technical!, "technical", modes["technical"]),
                    SourceModeFindingPolicy.Apply(maintenance!, "maintenance", modes["maintenance"]),
                    SourceModeFindingPolicy.Apply(finance!, "finance", modes["finance"]),
                    SourceModeFindingPolicy.Apply(records!, "records", modes["records"]),
                    SourceModeFindingPolicy.Apply(contracts!, "contracts", modes["contracts"]))
                    .GetAwaiter().GetResult();
                sourceModeFinding = SourceModeFindingPolicy.Assess(claims, modes, gathered.Pins);
                if (modes.Values.All(mode => mode == SourceModes.Available) && persisted.Basis.Count == basis.BasisVersion)
                {
                    (basis, persisted) = RefreshSourceBasis(scope, basis, persisted, gathered);
                }
            }
            else
            {
                if (modes["technical"] == "unavailable")
                {
                    sourceModeFinding = SourceModeFindingPolicy.Assess(claims, modes, []);
                }
                else
                {
                    var technicalRead = SourceModeFindingPolicy.Apply(technical!, "technical", modes["technical"])
                        .ListAsync(scope.CaseId).GetAwaiter().GetResult();
                    var technicalRecords = technicalRead switch
                    {
                        SourceReadSucceeded<IReadOnlyList<SourceRecord<TechnicalPayload>>> succeeded => succeeded.Value,
                        SourceUnavailable<IReadOnlyList<SourceRecord<TechnicalPayload>>> unavailable =>
                            throw new InvalidOperationException($"Source '{unavailable.SourceId}' is unavailable; its records cannot be assessed."),
                        UnknownSource<IReadOnlyList<SourceRecord<TechnicalPayload>>> unknown =>
                            throw new InvalidOperationException($"Source '{unknown.SourceId}' is not registered; its records cannot be assessed."),
                        _ => throw new InvalidOperationException("Technical source read did not return a successful record list."),
                    };
                    sourceModeFinding = SourceModeFindingPolicy.AssessTechnicalRecords(claims, modes, technicalRecords);
                }
            }
        }

        var expectedRevision = persisted.Revision;
        var current = assessments.CurrentBasis(scope.CaseId);
        var isCurrent = current is not null && current.BasisId == basis.BasisId && current.BasisVersion == basis.BasisVersion;
        // Evidence, coverage and identity come only from the stored accepted basis; the supplied basis is matched by identity.
        var documents = isCurrent ? current!.Documents : NoDocuments;
        var specialistFlags = isCurrent ? ReadSpecialistFlags(current!.SourcePins ?? []) : [];
        var capeTownEvidence = isCurrent ? ReadCapeTownInterestEvidence(current!.SourcePins ?? []) : [];
        IReadOnlyDictionary<string, IReadOnlyList<RequirementApplicability>> applicability = isCurrent
            ? ReadRequirementApplicability(current!.SourcePins ?? [])
            : new Dictionary<string, IReadOnlyList<RequirementApplicability>>(StringComparer.Ordinal);
        var applicabilityByClaim = claims.Select(claim => ResolveRequirementApplicability(claim, applicability)).ToArray();
        var capeTownClaims = claims.Select(claim =>
            ResolveCapeTownEvidence(claim, specialistFlags, capeTownEvidence)).ToArray();
        var specialistClaims = claims.Select((claim, index) => capeTownClaims[index].Matched
            ? new SpecialistClaimResolution(capeTownClaims[index].Result, true)
            : ResolveSpecialistFlag(claim, specialistFlags)).ToArray();
        var validated = claims
            .Select(claim =>
            {
                var investigationClaim = new InvestigationClaim(claim.Value, claim.Witnesses);
                if (claim.Kind == ClaimKind.MissingRequirement
                    && claim.Value.Contains(ScriptElementClaim, StringComparison.Ordinal))
                {
                    return new ValidatedClaim(
                        investigationClaim, claim.Kind, ClaimStatus.Downgraded, ScriptElementClaimReason);
                }

                return ClaimValidator.Validate(investigationClaim, claim.Kind, documents, claim.Value);
            })
            .ToArray();
        var assessed = claims
            .Select((claim, index) => new AssessedClaim(
                claim.Kind.ToString(),
                claim.Value,
                claim.Component,
                claim.SerialNumber,
                (claim.Witnesses ?? []).Where(w => w is not null).Select(w => new AssessedWitness(w.DocumentId, w.Page)).ToArray(),
                claim.RelatedRequirementId,
                specialistClaims[index].Result,
                capeTownClaims[index].Matched
                    ? capeTownClaims[index].Evidence.Select(item => item.Evidence).ToArray()
                    : null,
                claim.RequirementId,
                claim.EvidenceRegime,
                applicabilityByClaim[index].Status,
                applicabilityByClaim[index].Applicability))
            .ToArray();
        assessed = assessed.Select((claim, index) =>
        {
            if (capeTownClaims[index].Matched)
            {
                claim = claim with
                {
                    Witnesses = claim.Witnesses.Concat(capeTownClaims[index].Evidence.Select(item =>
                        new AssessedWitness(
                            $"source:{item.Pin.SourceId}:{item.Pin.RecordId}:v{item.Pin.RecordVersion}",
                            0))).ToArray(),
                };
            }
            if (!sourceModeFinding.WitnessesByClaim.TryGetValue(index, out var sourceWitnesses))
                return claim;
            return claim with { Witnesses = claim.Witnesses.Concat(sourceWitnesses).ToArray() };
        }).ToArray();

        var specialistRoute = capeTownClaims.Any(claim => claim.Matched)
            ? (Review, "specialist review required")
            : RouteSpecialistFlags(specialistClaims);
        var (outcome, reason) = isCurrent
            ? sourceModeFinding.Reason is { } sourceReason
                ? (Review, sourceReason)
                : specialistRoute ?? Route(current!, validated, applicabilityByClaim)
            : (Review, "supplied basis is not the current accepted basis");
        string? requestId = null;
        string? workItemId = null;
        var assessmentId = outcome == Request
            ? $"assessment-delivery-{current!.BasisVersion}"
            : $"assessment-{Guid.NewGuid():N}";
        var record = new AssessmentRecord(
            scope.CaseId, basis.BasisId, basis.BasisVersion, outcome, reason, assessed, requestId, workItemId, assessmentId);
        var assessmentCommitted = false;

        if (outcome == Request)
        {
            var active = current!;
            var claim = claims[0];
            var finding = new DeliveryFinding(active.Coverage, validated, active.IdentityAmbiguous, active.BasisId, active.BasisVersion);
            var envelope = new DeliveryEnvelope(
                $"{scope.CaseId}:{active.BasisId}:{active.BasisVersion}:assessment",
                scope.CaseId,
                $"assessment-{active.BasisVersion}",
                active.BasisId,
                active.BasisVersion,
                active.ScenarioTime,
                claim.Component,
                claim.SerialNumber,
                claim.Value);
            DeliveryResult? delivery = null;
            // The basis check and the submission share one lock, so a superseding basis cannot land between them.
            var submitted = assessments.RunIfCurrent(scope.CaseId, active, () =>
            {
                delivery = inbox.Prepare(envelope, finding, active.ScenarioTime);
                if (delivery.Outcome == DeliveryOutcome.Delivered && delivery.Receipt is { } receipt)
                {
                    requestId = receipt.RequestId;
                    record = record with { RequestId = requestId };
                    var committed = store.Commit(scope, expectedRevision, new CaseCommit(
                        null,
                        receipt.EventId,
                        Actor,
                        "assessment-delivered",
                        $"assessment {assessmentId} delivered request {receipt.RequestId} on basis {receipt.BasisId} v{receipt.BasisVersion}",
                        Assessment: ToCaseAssessment(record),
                        RequestReceipt: receipt));
                    if (committed.Replayed)
                    {
                        var existing = store.ListAssessments(scope).FirstOrDefault(item => item.RequestId == receipt.RequestId);
                        if (existing is null)
                            throw new InvalidOperationException("Delivered request has no persisted assessment.");
                        record = ToAssessmentRecord(existing);
                    }
                    else
                    {
                        inbox.RecordPrepared(envelope, receipt);
                        assessmentCommitted = true;
                    }
                }
            });
            if (!submitted)
            {
                outcome = Review;
                reason = "basis superseded during assessment";
            }
            else if (delivery!.Outcome == DeliveryOutcome.Delivered && delivery.Receipt is not null)
            {
                requestId = delivery.Receipt.RequestId;
                if (!assessmentCommitted)
                {
                    var existing = store.ListAssessments(scope)
                        .FirstOrDefault(item => item.RequestId == requestId);
                    if (existing is null)
                    {
                        throw new InvalidOperationException("Delivered request has no persisted assessment.");
                    }

                    record = ToAssessmentRecord(existing);
                    assessments.Append(record);
                    return record;
                }
            }
            else if (delivery.Outcome == DeliveryOutcome.Replayed && delivery.Receipt is not null)
            {
                var existing = store.ListAssessments(scope)
                    .FirstOrDefault(item => item.RequestId == delivery.Receipt.RequestId);
                if (existing is not null)
                {
                    record = ToAssessmentRecord(existing);
                    if (!assessments.HasPersistentHistory) assessments.Append(record);
                    return record;
                }

                outcome = Review;
                reason = "inbox replay has no persisted assessment";
            }
            else
            {
                // The inbox applies the same policy, so this is a safety net: anything it will not deliver goes to review.
                outcome = Review;
                reason = $"inbox declined: {delivery!.Reason}";
            }
        }

        if (outcome == Review || specialistClaims.Any(claim => claim.Matched))
        {
            workItemId = $"assessment-{Guid.NewGuid():N}";
            var workItemCommit = store.CreateWorkItem(
                scope,
                expectedRevision,
                new WorkItemDraft(workItemId, ReviewCapability, "open", ReviewOwner),
                Actor);
            record = record with { Outcome = outcome, Reason = reason, WorkItemId = workItemId };
            expectedRevision = workItemCommit.State.Revision;
        }

        if (!assessmentCommitted)
        {
            record = record with { Outcome = outcome, Reason = reason, RequestId = requestId, WorkItemId = workItemId };
            store.Commit(scope, expectedRevision, new CaseCommit(
                null,
                null,
                Actor,
                "assessment",
                $"assessment {assessmentId} recorded with outcome {outcome} on basis {basis.BasisId} v{basis.BasisVersion}",
                Assessment: ToCaseAssessment(record)));
        }

        if (!assessments.HasPersistentHistory) assessments.Append(record);
        return record;
    }

    private bool HasAnySourceSystem =>
        technical is not null || maintenance is not null || finance is not null || records is not null || contracts is not null;
    private (AssessmentBasis Basis, CaseState State) RefreshSourceBasis(
        CaseScope scope,
        AssessmentBasis basis,
        CaseState state,
        GatheredSourceBasis gathered)
    {
        var latest = state.Basis.LastOrDefault();
        if (latest?.Content != gathered.Content)
        {
            var next = CaseStateStore.CreateBasisVersion(
                checked(state.Basis.Count + 1), gathered.Content, latest?.BasisDigest ?? string.Empty);
            state = store.Commit(scope, state.Revision, new CaseCommit(
                next, null, Actor, "source-basis.gathered",
                $"Pinned {gathered.Pins.Count} source records with content digest {next.ContentDigest}")).State;
            latest = state.Basis.LastOrDefault();
        }
        var refreshed = basis with { BasisVersion = latest!.Version, SourcePins = gathered.Pins };
        assessments.SetCurrentBasis(scope.CaseId, refreshed);
        return (refreshed, state);
    }
    private void EnsureSourceSystemsConfigured()
    {
        if (technical is null || maintenance is null || finance is null || records is null || contracts is null)
            throw new InvalidOperationException("All five source systems must be configured before gathering a source basis.");
    }

    private static CaseAssessment ToCaseAssessment(AssessmentRecord record) =>
        new(
            record.AssessmentId!,
            record.CaseId,
            record.BasisId,
            record.BasisVersion,
            record.Outcome,
            record.Reason,
            record.Claims,
            record.RequestId,
            record.WorkItemId);

    private static AssessmentRecord ToAssessmentRecord(CaseAssessment assessment) =>
        new(
            assessment.CaseId,
            assessment.BasisId,
            assessment.BasisVersion,
            assessment.Outcome,
            assessment.Reason,
            assessment.Claims,
            assessment.RequestId,
            assessment.WorkItemId,
            assessment.AssessmentId);

    private static (string Outcome, string Reason) Route(
        AssessmentBasis basis,
        IReadOnlyList<ValidatedClaim> validated,
        IReadOnlyList<RequirementApplicabilityResolution> applicability)
    {
        if (!basis.Coverage.IsComplete)
        {
            return (Review, "processing or inventory coverage incomplete");
        }

        if (basis.IdentityAmbiguous)
        {
            return (Review, "ambiguous identity");
        }

        if (validated.Any(claim => claim.Reason == ScriptElementClaimReason))
        {
            return (Review, ScriptElementClaimReason);
        }

        if (validated.Any(v => v.Kind == ClaimKind.Conflict))
        {
            return (Review, "conflicting evidence");
        }

        var downgraded = validated.FirstOrDefault(v => v.Status == ClaimStatus.Downgraded);
        if (downgraded is not null)
        {
            return (Review, $"downgraded claim: {downgraded.Reason}");
        }

        if (validated.Count != 1)
        {
            return (Review, validated.Count == 0 ? "no claim" : "ambiguous: multiple claims");
        }

        if (validated[0].Kind == ClaimKind.MissingRequirement)
        {
            var requirement = applicability[0];
            return requirement.CanRequest
                ? (Request, "single supported missing requirement on current basis")
                : (Review, requirement.Reason ?? "requirement applicability needs review");
        }

        return validated[0].Kind == ClaimKind.Baseline
            ? (Baseline, "supported baseline")
            : (Review, "unsupported claim kind");
    }

    private sealed record RequirementApplicabilityResolution(
        bool CanRequest,
        string? Status,
        string? Reason,
        RequirementApplicability? Applicability);

    private static RequirementApplicabilityResolution ResolveRequirementApplicability(
        AssessmentClaim claim,
        IReadOnlyDictionary<string, IReadOnlyList<RequirementApplicability>> applicability)
    {
        if (claim.Kind != ClaimKind.MissingRequirement)
            return new RequirementApplicabilityResolution(false, null, null, null);

        if (string.IsNullOrWhiteSpace(claim.RequirementId)
            || !applicability.TryGetValue(claim.RequirementId, out var matches))
        {
            return NeedsReview("requirement applicability record not found");
        }

        if (matches.Count != 1)
            return NeedsReview("multiple requirement applicability records found");

        var match = matches[0];
        var missing = new List<string>(2);
        if (string.IsNullOrWhiteSpace(match.Regime)) missing.Add("regime");
        if (string.IsNullOrWhiteSpace(match.ResponsibleParty)) missing.Add("responsibleParty");
        if (missing.Count > 0)
            return NeedsReview($"missing applicability tags: {string.Join(", ", missing)}", match);

        if (!string.Equals(claim.EvidenceRegime, match.Regime, StringComparison.Ordinal))
            return NeedsReview("evidenceRegime does not match applicability regime", match);

        return new RequirementApplicabilityResolution(true, null, null, match);

        static RequirementApplicabilityResolution NeedsReview(
            string reason,
            RequirementApplicability? match = null) =>
            new(false, "needs_review", reason, match);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<RequirementApplicability>> ReadRequirementApplicability(
        IReadOnlyList<SourceBasisPin> pins)
    {
        var applicability = new Dictionary<string, List<RequirementApplicability>>(StringComparer.Ordinal);
        foreach (var pin in pins.Where(pin => pin.SourceId == "technical"))
        {
            using var document = System.Text.Json.JsonDocument.Parse(pin.Content);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "applicability")
                continue;
            if (!root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("fields", out var fields)
                || fields.ValueKind != System.Text.Json.JsonValueKind.Object
                || !fields.TryGetProperty("requirementId", out var requirementId)
                || requirementId.ValueKind != System.Text.Json.JsonValueKind.String
                || string.IsNullOrWhiteSpace(requirementId.GetString()))
            {
                throw new InvalidDataException("Pinned applicability payload is invalid.");
            }

            var record = new RequirementApplicability(
                ReadOptionalString(fields, "regime"),
                ReadOptionalString(fields, "responsibleParty"));
            var id = requirementId.GetString()!;
            if (!applicability.TryGetValue(id, out var records))
            {
                records = [];
                applicability.Add(id, records);
            }
            records.Add(record);
        }

        return applicability.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<RequirementApplicability>)pair.Value.AsReadOnly(),
            StringComparer.Ordinal);

        static string? ReadOptionalString(System.Text.Json.JsonElement fields, string name)
        {
            if (!fields.TryGetProperty(name, out var value))
                return null;
            if (value.ValueKind != System.Text.Json.JsonValueKind.String)
                throw new InvalidDataException("Pinned applicability payload is invalid.");
            return value.GetString();
        }
    }

    private sealed record SpecialistClaimResolution(string? Result, bool Matched, bool RelatedToUnmatchedRegime = false);
    private sealed record PinnedCapeTownEvidence(SourceBasisPin Pin, CapeTownInterestEvidence Evidence);
    private sealed record CapeTownClaimResolution(
        string? Result,
        bool Matched,
        IReadOnlyList<PinnedCapeTownEvidence> Evidence);

    private static SpecialistClaimResolution ResolveSpecialistFlag(
        AssessmentClaim claim,
        IReadOnlyList<SpecialistReviewFlag> flags)
    {
        if (string.IsNullOrWhiteSpace(claim.RelatedRequirementId))
            return new SpecialistClaimResolution(null, false);

        var related = flags.Where(flag =>
            string.Equals(flag.RelatedRequirementId, claim.RelatedRequirementId, StringComparison.Ordinal)).ToArray();
        if (related.Length == 0)
            return new SpecialistClaimResolution(null, false);

        var applicable = related.Where(flag =>
            flag.Topic != "sanctions" || string.Equals(flag.Regime, claim.Regime, StringComparison.Ordinal)).ToArray();
        if (applicable.Length == 0)
            return new SpecialistClaimResolution("not_assessed", false, true);

        var result = applicable.Any(flag => flag.Result == "specialist_review_required")
            ? "specialist_review_required"
            : "not_assessed";
        return new SpecialistClaimResolution(result, true);
    }

    private static (string Outcome, string Reason)? RouteSpecialistFlags(
        IReadOnlyList<SpecialistClaimResolution> claims)
    {
        if (claims.Any(claim => claim.Matched && claim.Result == "specialist_review_required"))
            return (Review, "specialist review required");
        if (claims.Any(claim => claim.Matched && claim.Result == "not_assessed"))
            return ("not_assessed", "not_assessed");
        if (claims.Any(claim => claim.RelatedToUnmatchedRegime))
            return ("not_assessed", "not_assessed");
        return null;
    }

    private static IReadOnlyList<SpecialistReviewFlag> ReadSpecialistFlags(
        IReadOnlyList<SourceBasisPin> pins)
    {
        var flags = new List<SpecialistReviewFlag>();
        foreach (var pin in pins.Where(pin => pin.SourceId == "contracts"))
        {
            using var document = System.Text.Json.JsonDocument.Parse(pin.Content);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "specialist_flag")
                continue;
            if (!root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("fields", out var fields)
                || fields.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new InvalidDataException("Pinned specialist_flag payload is invalid.");

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in fields.EnumerateObject())
            {
                if (field.Value.ValueKind != System.Text.Json.JsonValueKind.String)
                    throw new InvalidDataException("Pinned specialist_flag payload is invalid.");
                values.Add(field.Name, field.Value.GetString()!);
            }
            flags.Add(SpecialistReviewFlag.Parse(values));
        }
        return flags;
    }

    private static CapeTownClaimResolution ResolveCapeTownEvidence(
        AssessmentClaim claim,
        IReadOnlyList<SpecialistReviewFlag> flags,
        IReadOnlyList<PinnedCapeTownEvidence> evidence)
    {
        if (evidence.Count == 0)
            return new CapeTownClaimResolution(null, false, []);

        var flag = string.IsNullOrWhiteSpace(claim.RelatedRequirementId)
            ? null
            : flags.FirstOrDefault(item =>
                item.Topic == "cape_town"
                && string.Equals(item.RelatedRequirementId, claim.RelatedRequirementId, StringComparison.Ordinal));
        if (flag is null)
            throw new InvalidDataException(
                "A finding assessed with cape_town_interest requires an exact matching cape_town specialist_flag.");
        return new CapeTownClaimResolution(flag.Result, true, evidence);
    }

    private static IReadOnlyList<PinnedCapeTownEvidence> ReadCapeTownInterestEvidence(
        IReadOnlyList<SourceBasisPin> pins)
    {
        var evidence = new List<PinnedCapeTownEvidence>();
        foreach (var pin in pins.Where(pin => pin.SourceId == "contracts"))
        {
            using var document = System.Text.Json.JsonDocument.Parse(pin.Content);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "cape_town_interest")
                continue;
            if (!root.TryGetProperty("payload", out var payload))
                throw new InvalidDataException("Pinned cape_town_interest payload is invalid.");
            evidence.Add(new PinnedCapeTownEvidence(pin, ContractsPayload.ParsePinnedCapeTownInterest(payload)));
        }
        return evidence.AsReadOnly();
    }
}
