namespace AirlineDemo.Api;

public interface IInvestigationModel
{
    Task<InvestigationResult> InvestigateAsync(
        InvestigationRequest request,
        CancellationToken cancellationToken);
}

public sealed class BoundedInvestigationGateway
{
    private static readonly HashSet<string> ClosedAssessments = new(StringComparer.Ordinal)
    {
        "satisfied",
        "missing",
        "ambiguous",
        "conflicting",
        "blocked"
    };

    private readonly IInvestigationModel model;
    private readonly InvestigationLimits limits;

    public BoundedInvestigationGateway(
        IInvestigationModel model,
        InvestigationLimits? limits = null)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        this.limits = limits ?? new InvestigationLimits();
        ValidateLimits(this.limits);
    }

    public InvestigationRequest CreateRequest(
        EvidenceBasis evidenceBasis,
        IReadOnlyList<InvestigationDocumentContent> candidateDocuments)
    {
        ArgumentNullException.ThrowIfNull(evidenceBasis);
        ArgumentNullException.ThrowIfNull(candidateDocuments);

        var inventory = evidenceBasis.DocumentInventory
            .ToDictionary(
                document => DocumentKey(document.DocumentId, document.Version),
                StringComparer.Ordinal);
        var documents = candidateDocuments
            .Where(document => ScopeMatches(document.Context, evidenceBasis.Context))
            .Where(document => inventory.ContainsKey(
                DocumentKey(document.DocumentId, document.Version)))
            .Where(document => document.Page > 0)
            .GroupBy(document => DocumentPageKey(
                document.DocumentId, document.Version, document.Page), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        return new InvestigationRequest(
            evidenceBasis,
            evidenceBasis.ApprovedRequirementVersions.ToArray(),
            documents);
    }

    public async Task<InvestigationOutcome> InvestigateAsync(
        EvidenceBasis evidenceBasis,
        IReadOnlyList<InvestigationDocumentContent> candidateDocuments,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("A correlation ID is required.", nameof(correlationId));
        }

        var request = CreateRequest(evidenceBasis, candidateDocuments);
        var validationRequest = new InvestigationRequest(
            request.EvidenceBasis with
            {
                DocumentInventory = request.EvidenceBasis.DocumentInventory.ToArray(),
                ApprovedRequirementVersions =
                    request.EvidenceBasis.ApprovedRequirementVersions.ToArray()
            },
            request.ApprovedRequirements.ToArray(),
            request.Documents.ToArray());
        var pageCount = request.Documents.Count;
        var contextCharacters = request.Documents.Sum(document => document.Text?.Length ?? 0);
        if (pageCount > limits.MaxPages)
        {
            return Blocked(
                evidenceBasis,
                "INVESTIGATION_PAGE_LIMIT",
                correlationId);
        }

        if (contextCharacters > limits.MaxContextCharacters)
        {
            return Blocked(
                evidenceBasis,
                "INVESTIGATION_CONTEXT_LIMIT",
                correlationId);
        }

        var maximumAttempts = Math.Min(limits.MaxCalls, limits.MaxRetries + 1);
        if (maximumAttempts < 1)
        {
            return Blocked(
                evidenceBasis,
                "INVESTIGATION_CALL_LIMIT",
                correlationId);
        }

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(limits.EffectiveTimeout);
            try
            {
                var result = await model.InvestigateAsync(request, timeout.Token);
                var validationError = Validate(validationRequest, result);
                if (validationError is not null)
                {
                    return Blocked(
                        evidenceBasis,
                        "INVESTIGATION_OUTPUT_INVALID",
                        correlationId);
                }

                return new InvestigationOutcome(
                    evidenceBasis.BasisId,
                    evidenceBasis.Context,
                    "complete",
                    result.Findings.ToArray(),
                    null,
                    correlationId);
            }
            catch (OperationCanceledException)
                when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return Blocked(
                    evidenceBasis,
                    "INVESTIGATION_TIMEOUT",
                    correlationId);
            }
            catch (TimeoutException)
            {
                return Blocked(
                    evidenceBasis,
                    "INVESTIGATION_TIMEOUT",
                    correlationId);
            }
            catch (InvestigationCallException)
            {
                if (attempt == maximumAttempts)
                {
                    return Blocked(
                        evidenceBasis,
                        "INVESTIGATION_RETRY_LIMIT",
                        correlationId);
                }
            }
        }

        return Blocked(evidenceBasis, "INVESTIGATION_RETRY_LIMIT", correlationId);
    }

    public static string? Validate(
        InvestigationRequest request,
        InvestigationResult? result)
    {
        if (result?.Findings is null)
        {
            return "The investigation result must contain findings.";
        }

        var inventory = request.EvidenceBasis.DocumentInventory
            .Select(document => DocumentKey(document.DocumentId, document.Version))
            .ToHashSet(StringComparer.Ordinal);
        var pages = request.Documents
            .Where(document => ScopeMatches(
                document.Context, request.EvidenceBasis.Context))
            .Select(document => DocumentPageKey(
                document.DocumentId, document.Version, document.Page))
            .ToHashSet(StringComparer.Ordinal);
        var requirementKeys = request.ApprovedRequirements
            .Select(requirement => RequirementKey(
                requirement.RequirementId, requirement.Version))
            .ToHashSet(StringComparer.Ordinal);
        var findingIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var finding in result.Findings)
        {
            if (finding is null ||
                string.IsNullOrWhiteSpace(finding.FindingId) ||
                !findingIds.Add(finding.FindingId) ||
                string.IsNullOrWhiteSpace(finding.ComponentId) ||
                string.IsNullOrWhiteSpace(finding.RequirementId) ||
                !requirementKeys.Contains(
                    RequirementKey(finding.RequirementId, ResolveRequirementVersion(
                        request, finding.RequirementId))) ||
                !StringComparer.Ordinal.Equals(
                    finding.BasisId, request.EvidenceBasis.BasisId) ||
                !ClosedAssessments.Contains(finding.Assessment) ||
                string.IsNullOrWhiteSpace(finding.ReasonCode) ||
                string.IsNullOrWhiteSpace(finding.Explanation) ||
                finding.EvidenceRefs is null)
            {
                return "The investigation result contains an unsupported finding.";
            }

            foreach (var evidenceRef in finding.EvidenceRefs)
            {
                if (evidenceRef is null ||
                    evidenceRef.Page < 1 ||
                    !inventory.Contains(DocumentKey(
                        evidenceRef.DocumentId, evidenceRef.Version)) ||
                    !pages.Contains(DocumentPageKey(
                        evidenceRef.DocumentId,
                        evidenceRef.Version,
                        evidenceRef.Page)))
                {
                    return "The investigation result contains an invalid evidence citation.";
                }
            }
        }

        return null;
    }

    private static int ResolveRequirementVersion(
        InvestigationRequest request,
        string requirementId)
    {
        var versions = request.ApprovedRequirements
            .Where(requirement => requirement.RequirementId == requirementId)
            .Select(requirement => requirement.Version)
            .ToArray();
        return versions.Length == 1 ? versions[0] : 0;
    }

    private static InvestigationOutcome Blocked(
        EvidenceBasis evidenceBasis,
        string safeCode,
        string correlationId) =>
        new(
            evidenceBasis.BasisId,
            evidenceBasis.Context,
            "blocked",
            [],
            new SafeError(safeCode, correlationId),
            correlationId);

    private static void ValidateLimits(InvestigationLimits limits)
    {
        if (limits.MaxCalls < 0 ||
            limits.MaxContextCharacters < 0 ||
            limits.MaxPages < 0 ||
            limits.MaxRetries < 0 ||
            limits.EffectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
    }

    private static string DocumentKey(string documentId, int version) =>
        $"{documentId}:v{version}";

    private static string DocumentPageKey(string documentId, int version, int page) =>
        $"{DocumentKey(documentId, version)}:page-{page}";

    private static string RequirementKey(string requirementId, int version) =>
        $"{requirementId}:v{version}";

    private static bool ScopeMatches(CaseContext left, CaseContext right) =>
        left.RunId == right.RunId &&
        left.CaseId == right.CaseId &&
        left.AirlineId == right.AirlineId &&
        left.AircraftId == right.AircraftId &&
        left.LeaseId == right.LeaseId;
}
