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
    string? Regime = null);

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
    string? SpecialistResult = null);

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
        var specialistClaims = claims.Select(claim => ResolveSpecialistFlag(claim, specialistFlags)).ToArray();
        var validated = claims
            .Select(claim => ClaimValidator.Validate(
                new InvestigationClaim(claim.Value, claim.Witnesses), claim.Kind, documents, claim.Value))
            .ToArray();
        var assessed = claims
            .Select((claim, index) => new AssessedClaim(
                claim.Kind.ToString(),
                claim.Value,
                claim.Component,
                claim.SerialNumber,
                (claim.Witnesses ?? []).Where(w => w is not null).Select(w => new AssessedWitness(w.DocumentId, w.Page)).ToArray(),
                claim.RelatedRequirementId,
                specialistClaims[index].Result))
            .ToArray();
        assessed = assessed.Select((claim, index) =>
        {
            if (!sourceModeFinding.WitnessesByClaim.TryGetValue(index, out var sourceWitnesses))
                return claim;
            return claim with { Witnesses = claim.Witnesses.Concat(sourceWitnesses).ToArray() };
        }).ToArray();

        var specialistRoute = RouteSpecialistFlags(specialistClaims);
        var (outcome, reason) = isCurrent
            ? sourceModeFinding.Reason is { } sourceReason
                ? (Review, sourceReason)
                : specialistRoute ?? Route(current!, validated)
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

    private static (string Outcome, string Reason) Route(AssessmentBasis basis, IReadOnlyList<ValidatedClaim> validated)
    {
        if (!basis.Coverage.IsComplete)
        {
            return (Review, "processing or inventory coverage incomplete");
        }

        if (basis.IdentityAmbiguous)
        {
            return (Review, "ambiguous identity");
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

        return validated[0].Kind switch
        {
            ClaimKind.Baseline => (Baseline, "supported baseline"),
            ClaimKind.MissingRequirement => (Request, "single supported missing requirement on current basis"),
            _ => (Review, "unsupported claim kind"),
        };
    }

    private sealed record SpecialistClaimResolution(string? Result, bool Matched, bool RelatedToUnmatchedRegime = false);

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
}
