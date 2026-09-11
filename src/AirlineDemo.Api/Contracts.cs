using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirlineDemo.Api;

public sealed record CaseContext(
    string RunId,
    string CaseId,
    string AirlineId,
    string AircraftId,
    string LeaseId);

public sealed record DocumentMetadata(
    string DocumentId,
    int Version,
    string SourceSystem,
    string SourceRecordId,
    string FileName,
    string MediaType,
    string Sha256,
    DateTimeOffset IssuedOn);

public sealed record SubmissionPackage(
    string SchemaVersion,
    string PackageId,
    string RunId,
    string CaseId,
    string AirlineId,
    string AircraftId,
    string LeaseId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset ScenarioEffectiveAt,
    IReadOnlyList<DocumentMetadata> Manifest);

public sealed record EventEnvelope(
    string SchemaVersion,
    string EventId,
    string Type,
    string RunId,
    string CaseId,
    string AirlineId,
    string AircraftId,
    string LeaseId,
    DateTimeOffset OccurredAt,
    DateTimeOffset ScenarioEffectiveAt,
    string CorrelationId,
    JsonElement Payload);

public sealed record PackageSubmissionRequest(EventEnvelope Event, SubmissionPackage Package);

public sealed record SafeError(string SafeCode, string CorrelationId, string? Message = null);

public sealed record OperationAccepted(
    string OperationId,
    string CaseId,
    string ReceiptId);

public sealed record RunResetRequest(
    string? RunId,
    string? Confirmation);

public sealed record ResetAccepted(
    string RunId,
    string ReceiptId,
    int AffectedRecordCount);

public sealed record ProcessingAttemptStatus(
    string AttemptId,
    int AttemptNumber,
    string Status,
    SafeError? Error = null);

public sealed record OperationStatus(
    string OperationId,
    string CaseId,
    string RunId,
    string AirlineId,
    string AircraftId,
    string LeaseId,
    string Status,
    SafeError? Error = null,
    IReadOnlyList<ProcessingAttemptStatus>? Attempts = null);

public sealed record PackageProcessingStatus(
    string PackageId,
    string OperationId,
    string Status,
    SafeError? Error = null);

public sealed record CaseSummary(
    string CaseId,
    string RunId,
    string AirlineId,
    string AircraftId,
    string LeaseId,
    long CaseRevision,
    string Status,
    IReadOnlyList<PackageProcessingStatus> PackageProcessing,
    InvestigationOutcome? Investigation = null);

public sealed record EvidencePreview(
    string DocumentId,
    int Version,
    int Page,
    string MediaType,
    string TextExcerpt,
    EvidenceBounds? Bounds = null);

public sealed record EvidenceBounds(double X, double Y, double Width, double Height);

public sealed record ApprovedRequirementVersion(
    string RequirementId,
    int Version,
    string? ComponentId = null);

public sealed record EvidenceDocumentVersion(
    string DocumentId,
    int Version,
    string Sha256);

public sealed record ScopedExtractionRecord(
    CaseContext Context,
    string DocumentId,
    int Version,
    string Sha256,
    string ParserVersion,
    string ExtractorVersion,
    IReadOnlyList<int> PageInventory,
    string ProcessingState,
    string? AttemptId = null,
    int AttemptNumber = 0,
    SafeError? Error = null);

public sealed record EvidenceBasis(
    string BasisId,
    CaseContext Context,
    IReadOnlyList<EvidenceDocumentVersion> DocumentInventory,
    IReadOnlyList<ApprovedRequirementVersion> ApprovedRequirementVersions,
    long CaseRevision,
    DateTimeOffset CreatedAt,
    string ParserVersion,
    string ExtractorVersion);

public sealed record EvidenceRef(
    string DocumentId,
    int Version,
    int Page,
    EvidenceBounds? Bounds = null,
    string? Quote = null);

public sealed record Finding(
    string FindingId,
    string ComponentId,
    string RequirementId,
    string BasisId,
    string Assessment,
    string ReasonCode,
    string Explanation,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

public sealed record InvestigationResult(IReadOnlyList<Finding> Findings);

public sealed record InvestigationDocumentContent(
    CaseContext Context,
    string DocumentId,
    int Version,
    int Page,
    string Text);

public sealed record InvestigationRequest(
    EvidenceBasis EvidenceBasis,
    IReadOnlyList<ApprovedRequirementVersion> ApprovedRequirements,
    IReadOnlyList<InvestigationDocumentContent> Documents);

public sealed record InvestigationOutcome(
    string BasisId,
    CaseContext Context,
    string Status,
    IReadOnlyList<Finding> Findings,
    SafeError? Error,
    string CorrelationId);

public sealed record InvestigationLimits(
    int MaxCalls = 3,
    int MaxContextCharacters = 100_000,
    int MaxPages = 100,
    int MaxRetries = 2,
    TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(30);
}

public sealed class InvestigationCallException(string message) : Exception(message);

public sealed record PolicyDecision(
    string DecisionId,
    string FindingId,
    string BasisId,
    string Outcome);

public sealed record EvidenceRequest(
    string RequestId,
    string RequestKey,
    string FindingId,
    string BasisId,
    string Status);

public sealed record CallerScope(
    string RunId,
    string AirlineId,
    string AircraftId,
    string LeaseId);

internal sealed record PersistedCase(
    CaseContext Context,
    long CaseRevision,
    string Status,
    List<string> PackageIds,
    List<string> OperationIds);

internal sealed record PersistedPackage(
    SubmissionPackage Package,
    CaseContext Context,
    string OperationId,
    string ProcessingStatus,
    SafeError? Error);

internal sealed record PersistedOperation(
    OperationStatus Status,
    string ReceiptHash);

internal sealed record PersistedDocument(
    DocumentMetadata Metadata,
    CaseContext Context,
    string StorageLocator);

internal sealed record PersistedReceipt(
    string ReceiptId,
    string RunId,
    string EventId,
    string CanonicalHash,
    string OperationId,
    string CaseId,
    string OperationType = "load",
    string SelectedManifestSha256 = "",
    DateTimeOffset Timestamp = default,
    int AffectedRecordCount = 0);

internal sealed class PersistedState
{
    public Dictionary<string, PersistedCase> Cases { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, PersistedPackage> Packages { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, PersistedOperation> Operations { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, PersistedDocument> Documents { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, ScopedExtractionRecord> ExtractionRecords { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, ScopedExtractionRecord> ExtractionAttempts { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, EvidenceBasis> EvidenceBases { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, InvestigationOutcome> Investigations { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, Finding> Findings { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, PolicyDecision> PolicyDecisions { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, EvidenceRequest> EvidenceRequests { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, PersistedReceipt> Receipts { get; init; } = new(StringComparer.Ordinal);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, PersistedReceipt>? AuditReceipts { get; set; }
}
