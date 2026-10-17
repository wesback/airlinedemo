using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Requests;

namespace AirlineDemo.Api.Persistence;

public sealed class CaseAccessDeniedException()
    : Exception("Case not found or not in scope.");

public sealed class RevisionConflictException()
    : Exception("Case revision is stale; no change was made.");

public sealed class ReceiptConflictException()
    : Exception("Receipt was already used for a different commit; no change was made.");

public sealed class TraceConflictException()
    : Exception("Investigation trace already exists for this case, run and investigation; no change was made.");

public sealed class BasisIntegrityException(string message) : Exception(message);

/// <summary>Server-issued proof that a case id was authorized; only the store can create one.</summary>
public sealed class CaseScope
{
    internal CaseScope(string caseId) => CaseId = caseId;

    public string CaseId { get; }
}

public sealed record BasisVersion(
    int Version,
    string Content,
    string ContentDigest,
    string BasisDigest,
    DateTimeOffset CreatedAt);

public sealed record AuditEvent(
    long Sequence,
    long Revision,
    string Actor,
    string Action,
    string Detail,
    DateTimeOffset At);

/// <summary>A review work item. <see cref="CreatedAt"/> is assigned by the store, never by the caller.</summary>
public sealed record WorkItem(
    string CaseId,
    string ItemId,
    string CapabilityId,
    string State,
    string Owner,
    DateTimeOffset CreatedAt);

/// <summary>Caller-supplied work item fields; the creation timestamp is deliberately absent.</summary>
public sealed record WorkItemDraft(string ItemId, string CapabilityId, string State, string Owner);

public sealed record CaseState(
    string CaseId,
    string Owner,
    long Revision,
    IReadOnlyList<BasisVersion> Basis,
    IReadOnlyList<string> ReceiptIds,
    IReadOnlyList<AuditEvent> Audit,
    IReadOnlyDictionary<string, string> ReceiptFingerprints,
    IReadOnlyList<WorkItem>? WorkItems = null,
    IReadOnlyList<InvestigationTrace>? Traces = null,
    IReadOnlyList<CaseAssessment>? Assessments = null,
    IReadOnlyList<DeliveryReceipt>? RequestReceipts = null);

public sealed record CaseAssessment(
    string AssessmentId,
    string CaseId,
    string BasisId,
    int BasisVersion,
    string Outcome,
    string Reason,
    IReadOnlyList<AssessedClaim> Claims,
    string? RequestId,
    string? WorkItemId);

internal static class AssessmentPersistenceRules
{
    public static void Validate(CaseAssessment assessment)
    {
        if (string.IsNullOrWhiteSpace(assessment.AssessmentId) || string.IsNullOrWhiteSpace(assessment.CaseId)
            || string.IsNullOrEmpty(assessment.BasisId) || assessment.BasisVersion <= 0
            || string.IsNullOrWhiteSpace(assessment.Outcome) || string.IsNullOrWhiteSpace(assessment.Reason) || assessment.Claims is null)
            throw new ArgumentException("Assessment identity, basis, result and claims are required.", nameof(assessment));

        foreach (var claim in assessment.Claims)
            if (claim is null || string.IsNullOrWhiteSpace(claim.Kind) || claim.Value is null || claim.Component is null
                || claim.SerialNumber is null || claim.Witnesses is null || claim.Witnesses.Any(w => w is null || w.DocumentId is null))
                throw new ArgumentException("Assessment claims and witnesses are invalid.", nameof(assessment));
    }

    public static void Validate(DeliveryReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt.EventId) || string.IsNullOrWhiteSpace(receipt.RequestId)
            || string.IsNullOrWhiteSpace(receipt.CaseId) || string.IsNullOrWhiteSpace(receipt.ReportId)
            || string.IsNullOrWhiteSpace(receipt.BasisId) || receipt.BasisVersion <= 0 || string.IsNullOrWhiteSpace(receipt.TemplateVersion)
            || receipt.Body is null)
            throw new ArgumentException("Request receipt fields are required.", nameof(receipt));
    }
}

/// <summary>Required-field rules shared by both case-state stores.</summary>
internal static class WorkItemRules
{
    public static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Work item {name} is required.", name);
        }
    }

    public static void Validate(WorkItemDraft draft)
    {
        RequireText(draft.ItemId, nameof(draft.ItemId));
        RequireText(draft.CapabilityId, nameof(draft.CapabilityId));
        RequireText(draft.State, nameof(draft.State));
        RequireText(draft.Owner, nameof(draft.Owner));
    }
}

/// <summary>
/// One ordered, minimized investigation step. Categorical codes only: no document, prompt or response text.
/// <see cref="CorrectionCode"/> is <c>None</c> when no correction was made; <see cref="PolicyHandover"/> is <c>None</c> when no handover applied.
/// </summary>
public sealed record InvestigationTraceStep(
    int Sequence,
    string ToolName,
    string DocumentId,
    int PageNumber,
    string CitationCheckOutcome,
    string CorrectionCode,
    string PolicyHandover);

/// <summary>
/// Append-only minimized trace keyed by (CaseId, RunId, InvestigationId). <see cref="ActorToken"/> is opaque and non-identifying.
/// </summary>
public sealed record InvestigationTrace(
    string CaseId,
    string RunId,
    string InvestigationId,
    int BasisVersion,
    string ModelDeploymentName,
    string ApiVersion,
    string Region,
    string ContentFilterSetting,
    string Decision,
    string ActorToken,
    string Outcome,
    IReadOnlyList<InvestigationTraceStep> Steps);

internal static class InvestigationTraceRules
{
    // Marks a step with no document (a tool call or a correction). Such a step has PageNumber 0.
    public const string NoValue = "None";

    public static void Validate(InvestigationTrace trace)
    {
        Require(trace.CaseId, nameof(trace.CaseId));
        Require(trace.RunId, nameof(trace.RunId));
        Require(trace.InvestigationId, nameof(trace.InvestigationId));
        Require(trace.ModelDeploymentName, nameof(trace.ModelDeploymentName));
        Require(trace.ApiVersion, nameof(trace.ApiVersion));
        Require(trace.Region, nameof(trace.Region));
        Require(trace.ContentFilterSetting, nameof(trace.ContentFilterSetting));
        Require(trace.Decision, nameof(trace.Decision));
        Require(trace.ActorToken, nameof(trace.ActorToken));
        Require(trace.Outcome, nameof(trace.Outcome));
        if (trace.Steps is null)
        {
            throw new ArgumentException("Investigation trace steps are required.", nameof(trace));
        }

        for (var i = 0; i < trace.Steps.Count; i++)
        {
            var step = trace.Steps[i];
            if (step.Sequence != i + 1)
            {
                throw new ArgumentException("Investigation trace steps must be numbered 1..n in append order.", nameof(trace));
            }

            Require(step.ToolName, "ToolName");
            Require(step.DocumentId, "DocumentId");
            Require(step.CitationCheckOutcome, "CitationCheckOutcome");
            Require(step.CorrectionCode, "CorrectionCode");
            Require(step.PolicyHandover, "PolicyHandover");
            if (step.DocumentId == NoValue ? step.PageNumber != 0 : step.PageNumber < 1)
            {
                throw new ArgumentException("Investigation trace PageNumber must be positive for a cited document, and 0 when there is none.", nameof(trace));
            }
        }
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Investigation trace {name} is required.", name);
        }
    }
}

public sealed record CaseCommit(
    BasisVersion? NewBasis,
    string? ReceiptId,
    string Actor,
    string Action,
    string Detail,
    DateTimeOffset? At = null,
    CaseAssessment? Assessment = null,
    DeliveryReceipt? RequestReceipt = null);

public sealed record CommitResult(CaseState State, bool Replayed);

internal sealed record CaseStoreDocument(int SchemaVersion, List<CaseState> Cases);

/// <summary>
/// File-backed, scoped, versioned case state. Every mutation goes through <see cref="Commit"/>,
/// which checks the server-issued scope and current revision, validates basis digests, and
/// persists atomically: on any failure neither memory nor the file changes.
/// The audit log is exposed read-only; there is no update or delete operation.
/// </summary>
public sealed class CaseStateStore : ICaseStateStore
{
    private const int SchemaVersion = 1;
    private const int LockAttempts = 500;
    private const int LockRetryMilliseconds = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly Action? _beforePersist;
    private readonly object _gate = new();
    private Dictionary<string, CaseState> _cases;

    public CaseStateStore(string path, Action? beforePersist = null)
    {
        _path = path;
        _beforePersist = beforePersist;
        _cases = Load(path);
    }

    /// <summary>
    /// Issues a scope only when <paramref name="owner"/> is the server-recorded owner of the case.
    /// An unknown case, a case owned by someone else, or a missing owner all yield the same denial.
    /// </summary>
    public CaseScope AuthorizeCase(string caseId, string owner)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(caseId) || string.IsNullOrEmpty(owner)
                || !_cases.TryGetValue(caseId, out var state)
                || !string.Equals(state.Owner, owner, StringComparison.Ordinal))
            {
                throw new CaseAccessDeniedException();
            }

            return new CaseScope(caseId);
        }
    }

    /// <summary>Returns the server-recorded owner of a case, or null when the case does not exist.</summary>
    public string? OwnerOf(string caseId)
    {
        lock (_gate)
        {
            return _cases.TryGetValue(caseId, out var state) ? state.Owner : null;
        }
    }

    public CaseScope CreateCase(string caseId, string owner)
    {
        if (string.IsNullOrWhiteSpace(caseId))
        {
            throw new ArgumentException("Case id is required.", nameof(caseId));
        }

        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("Case owner is required.", nameof(owner));
        }

        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            _cases = Load(_path);
            if (_cases.ContainsKey(caseId))
            {
                throw new InvalidOperationException("Case already exists.");
            }

            var snapshot = new Dictionary<string, CaseState>(_cases)
            {
                [caseId] = Freeze(new(caseId, owner, 0, [], [], [], new Dictionary<string, string>(StringComparer.Ordinal))),
            };
            Persist(snapshot);
            _cases = snapshot;
            return new CaseScope(caseId);
        }
    }

    public CaseState Read(CaseScope scope)
    {
        lock (_gate)
        {
            return Current(scope);
        }
    }

    public BasisVersion GetBasisVersion(CaseScope scope, int version)
    {
        lock (_gate)
        {
            return Current(scope).Basis.FirstOrDefault(b => b.Version == version)
                ?? throw new KeyNotFoundException("Basis version not found for this case.");
        }
    }

    public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope)
    {
        lock (_gate)
        {
            return Current(scope).Audit;
        }
    }

    public IReadOnlyList<CaseAssessment> ListAssessments(CaseScope scope)
    {
        lock (_gate) return Current(scope).Assessments ?? Array.Empty<CaseAssessment>();
    }

    public CaseAssessment? FindAssessment(CaseScope scope, string assessmentId)
    {
        lock (_gate) return Current(scope).Assessments?
            .FirstOrDefault(record => string.Equals(record.AssessmentId, assessmentId, StringComparison.Ordinal));
    }

    public IReadOnlyList<DeliveryReceipt> ListRequestReceipts(CaseScope scope)
    {
        lock (_gate) return Current(scope).RequestReceipts ?? Array.Empty<DeliveryReceipt>();
    }

    public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope)
    {
        lock (_gate)
        {
            return Current(scope).WorkItems ?? Array.Empty<WorkItem>();
        }
    }

    /// <summary>Appends a trace. Traces are never updated or deleted; a duplicate (case, run, investigation) fails and keeps the first record.</summary>
    public void AppendTrace(CaseScope scope, InvestigationTrace trace)
    {
        InvestigationTraceRules.Validate(trace);
        if (trace.CaseId != scope.CaseId)
        {
            throw new ArgumentException("Trace case id must match the authorized case.", nameof(trace));
        }

        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            _cases = Load(_path);
            var current = Current(scope);
            var existing = current.Traces ?? Array.Empty<InvestigationTrace>();
            if (existing.Any(t => t.RunId == trace.RunId && t.InvestigationId == trace.InvestigationId))
            {
                throw new TraceConflictException();
            }

            var updated = Freeze(current with { Traces = [.. existing, SnapshotTrace(trace)] });
            var snapshot = new Dictionary<string, CaseState>(_cases) { [scope.CaseId] = updated };
            Persist(snapshot);
            _cases = snapshot;
        }
    }

    /// <summary>Returns the trace only when the case, run and investigation ids all match exactly; otherwise null.</summary>
    public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId)
    {
        lock (_gate)
        {
            var state = Current(scope);
            if (caseId != scope.CaseId)
            {
                return null;
            }

            return (state.Traces ?? Array.Empty<InvestigationTrace>())
                .FirstOrDefault(t => t.RunId == runId && t.InvestigationId == investigationId);
        }
    }

    /// <summary>Creates a work item at the current revision; the store assigns the creation timestamp.</summary>
    public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor)
    {
        WorkItemRules.Validate(draft);
        return ApplyWorkItemChange(scope, expectedRevision, actor, "create-work-item", draft.ItemId, (items, now) =>
        {
            if (items.Any(i => string.Equals(i.ItemId, draft.ItemId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Work item already exists in this case.");
            }

            items.Add(new WorkItem(scope.CaseId, draft.ItemId, draft.CapabilityId, draft.State, draft.Owner, now));
        });
    }

    public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor)
    {
        WorkItemRules.RequireText(itemId, nameof(itemId));
        WorkItemRules.RequireText(state, nameof(state));
        return ApplyWorkItemChange(scope, expectedRevision, actor, "change-work-item-state", itemId + " -> " + state, (items, _) =>
        {
            var index = items.FindIndex(i => string.Equals(i.ItemId, itemId, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new KeyNotFoundException("Work item not found for this case.");
            }

            items[index] = items[index] with { State = state };
        });
    }

    /// <summary>Checks scope and revision, applies one work-item change, and appends one audit event at the next revision.</summary>
    private CommitResult ApplyWorkItemChange(
        CaseScope scope,
        long expectedRevision,
        string actor,
        string action,
        string detail,
        Action<List<WorkItem>, DateTimeOffset> change)
    {
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            _cases = Load(_path);
            var current = Current(scope);
            if (expectedRevision != current.Revision)
            {
                throw new RevisionConflictException();
            }

            var now = DateTimeOffset.UtcNow;
            var items = (current.WorkItems ?? Array.Empty<WorkItem>()).ToList();
            change(items, now);

            var revision = current.Revision + 1;
            var audit = current.Audit.ToList();
            audit.Add(new AuditEvent(audit.Count + 1, revision, actor, action, detail, now));

            var updated = Freeze(current with { Revision = revision, Audit = audit, WorkItems = items });
            var snapshot = new Dictionary<string, CaseState>(_cases) { [scope.CaseId] = updated };
            Persist(snapshot);
            _cases = snapshot;
            return new CommitResult(updated, Replayed: false);
        }
    }

    public static BasisVersion CreateBasisVersion(int version, string content, string previousBasisDigest)
    {
        var contentDigest = Digest(content);
        return new BasisVersion(
            version,
            content,
            contentDigest,
            Digest(previousBasisDigest + "|" + contentDigest),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The only mutation path. Rejects forged scope, stale revision, and invalid basis.
    /// An exact receipt already recorded for the case is replayed without a new revision or audit event.
    /// </summary>
    public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
    {
        if (commit.Assessment is { } assessment)
        {
            AssessmentPersistenceRules.Validate(assessment);
            if (!string.Equals(assessment.CaseId, scope.CaseId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Assessment case id must match the authorized case.", nameof(commit));
            }
        }

        if (commit.RequestReceipt is { } requestReceipt)
        {
            AssessmentPersistenceRules.Validate(requestReceipt);
            if (!string.Equals(requestReceipt.CaseId, scope.CaseId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Request receipt case id must match the authorized case.", nameof(commit));
            }
        }

        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            _cases = Load(_path);
            var current = Current(scope);
            var fingerprint = CommitFingerprint(commit);

            if (commit.ReceiptId is not null && current.ReceiptFingerprints.TryGetValue(commit.ReceiptId, out var recorded))
            {
                if (recorded != fingerprint)
                {
                    throw new ReceiptConflictException();
                }

                return new CommitResult(current, Replayed: true);
            }

            if (expectedRevision != current.Revision)
            {
                throw new RevisionConflictException();
            }

            var basis = current.Basis.ToList();
            if (commit.NewBasis is { } next)
            {
                var previousDigest = basis.Count == 0 ? string.Empty : basis[^1].BasisDigest;
                if (next.Version != basis.Count + 1)
                {
                    throw new BasisIntegrityException("Basis version must be the next version; prior versions are immutable.");
                }

                if (next.ContentDigest != Digest(next.Content)
                    || next.BasisDigest != Digest(previousDigest + "|" + next.ContentDigest))
                {
                    throw new BasisIntegrityException("Basis content or chain digest does not match.");
                }

                ValidateSourceBasisContent(next.Content);
                basis.Add(next);
            }

            var assessments = (current.Assessments ?? Array.Empty<CaseAssessment>()).ToList();
            if (commit.Assessment is { } newAssessment)
            {
                if (assessments.Any(record => string.Equals(record.AssessmentId, newAssessment.AssessmentId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("Assessment id already exists in this case.");
                }

                assessments.Add(SnapshotAssessment(newAssessment));
            }

            var requestReceipts = (current.RequestReceipts ?? Array.Empty<DeliveryReceipt>()).ToList();
            if (commit.RequestReceipt is { } newRequestReceipt)
            {
                if (requestReceipts.Any(receipt => string.Equals(receipt.EventId, newRequestReceipt.EventId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("Request receipt event already exists in this case.");
                }

                requestReceipts.Add(newRequestReceipt);
            }

            var receipts = current.ReceiptIds.ToList();
            var fingerprints = new Dictionary<string, string>(current.ReceiptFingerprints, StringComparer.Ordinal);
            if (commit.ReceiptId is not null)
            {
                receipts.Add(commit.ReceiptId);
                fingerprints[commit.ReceiptId] = fingerprint;
            }

            var revision = current.Revision + 1;
            var audit = current.Audit.ToList();
            audit.Add(new AuditEvent(audit.Count + 1, revision, commit.Actor, commit.Action, commit.Detail, commit.At ?? DateTimeOffset.UtcNow));

            var updated = Freeze(current with
            {
                Revision = revision,
                Basis = basis,
                ReceiptIds = receipts,
                Audit = audit,
                ReceiptFingerprints = fingerprints,
                Assessments = assessments,
                RequestReceipts = requestReceipts,
            });
            var snapshot = new Dictionary<string, CaseState>(_cases) { [scope.CaseId] = updated };
            Persist(snapshot);
            _cases = snapshot;
            return new CommitResult(updated, Replayed: false);
        }
    }

    /// <summary>
    /// Holds an exclusive lock file so commits from other store instances on the same path are serialized.
    /// Callers reload the file after acquiring it, so revision checks see the latest persisted state.
    /// </summary>
    private FileStream AcquireFileLock()
    {
        var fullPath = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var lockPath = fullPath + ".lock";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < LockAttempts)
            {
                Thread.Sleep(LockRetryMilliseconds);
            }
        }
    }

    private CaseState Current(CaseScope scope)
    {
        if (!_cases.TryGetValue(scope.CaseId, out var state))
        {
            throw new CaseAccessDeniedException();
        }

        return state;
    }

    private void Persist(Dictionary<string, CaseState> snapshot)
    {
        _beforePersist?.Invoke();
        var document = new CaseStoreDocument(
            SchemaVersion,
            snapshot.Values.OrderBy(c => c.CaseId, StringComparer.Ordinal).ToList());
        var fullPath = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temp = fullPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temp, fullPath, overwrite: true);
    }

    private static Dictionary<string, CaseState> Load(string path)
    {
        var cases = new Dictionary<string, CaseState>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return cases;
        }

        CaseStoreDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CaseStoreDocument>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Case state file is unreadable.", ex);
        }

        if (document is null || document.SchemaVersion != SchemaVersion || document.Cases is null)
        {
            throw new InvalidDataException("Unknown case state schema.");
        }

        foreach (var state in document.Cases)
        {
            Validate(state);
            if (!cases.TryAdd(state.CaseId, Freeze(state)))
            {
                throw new InvalidDataException("Duplicate case in case state file.");
            }
        }

        return cases;
    }

    /// <summary>Returns a copy whose collections cannot be mutated by callers, so changes only happen through Commit.</summary>
    private static CaseState Freeze(CaseState state) => state with
    {
        Basis = Wrap(state.Basis),
        ReceiptIds = Wrap(state.ReceiptIds),
        Audit = Wrap(state.Audit),
        WorkItems = Wrap(state.WorkItems ?? Array.Empty<WorkItem>()),
        Traces = Wrap((state.Traces ?? Array.Empty<InvestigationTrace>()).Select(SnapshotTrace)),
        Assessments = Wrap((state.Assessments ?? Array.Empty<CaseAssessment>()).Select(SnapshotAssessment)),
        RequestReceipts = Wrap(state.RequestReceipts ?? Array.Empty<DeliveryReceipt>()),
        ReceiptFingerprints = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(state.ReceiptFingerprints, StringComparer.Ordinal)),
    };

    /// <summary>Copies a trace's steps into a read-only list so the stored record cannot change through the caller's collection.</summary>
    private static InvestigationTrace SnapshotTrace(InvestigationTrace trace) =>
        trace with { Steps = Wrap(trace.Steps) };

    internal static CaseAssessment SnapshotAssessment(CaseAssessment assessment) =>
        assessment with
        {
            Claims = Wrap(assessment.Claims.Select(claim =>
                claim with { Witnesses = Wrap(claim.Witnesses) })),
        };

    private static ReadOnlyCollection<T> Wrap<T>(IEnumerable<T> items) => new(items.ToList());

    /// <summary>Digest of the commit payload, used to tell a true replay from a reused receipt ID.</summary>
    internal static string CommitFingerprint(CaseCommit commit)
    {
        var digest = Digest(JsonSerializer.Serialize(new
        {
            basisVersion = commit.NewBasis?.Version,
            content = commit.NewBasis?.Content,
            contentDigest = commit.NewBasis?.ContentDigest,
            basisDigest = commit.NewBasis?.BasisDigest,
            commit.Actor,
            commit.Action,
            commit.Detail,
        }));
        return commit.Assessment is null && commit.RequestReceipt is null
            ? digest
            : Digest(digest + JsonSerializer.Serialize(new { commit.Assessment, commit.RequestReceipt }));
    }

    private static void Validate(CaseState state)
    {
        if (state.Revision != state.Audit.Count)
        {
            throw new InvalidDataException("Case revision does not match its audit history.");
        }

        for (var i = 0; i < state.Audit.Count; i++)
        {
            if (state.Audit[i].Sequence != i + 1 || state.Audit[i].Revision != i + 1)
            {
                throw new InvalidDataException("Audit history is not contiguous.");
            }
        }

        var previousDigest = string.Empty;
        for (var i = 0; i < state.Basis.Count; i++)
        {
            var basis = state.Basis[i];
            if (basis.Version != i + 1
                || basis.ContentDigest != Digest(basis.Content)
                || basis.BasisDigest != Digest(previousDigest + "|" + basis.ContentDigest))
            {
                throw new InvalidDataException("Basis version chain is invalid.");
            }

            ValidateSourceBasisContent(basis.Content);
            previousDigest = basis.BasisDigest;
        }

        if (state.ReceiptIds.Distinct(StringComparer.Ordinal).Count() != state.ReceiptIds.Count)
        {
            throw new InvalidDataException("Duplicate receipt in case state file.");
        }

        if (state.ReceiptFingerprints is null
            || state.ReceiptFingerprints.Count != state.ReceiptIds.Count
            || state.ReceiptIds.Any(id => !state.ReceiptFingerprints.ContainsKey(id)))
        {
            throw new InvalidDataException("Receipt fingerprints do not match receipts.");
        }

        var traces = state.Traces ?? Array.Empty<InvestigationTrace>();
        if (traces.Any(t => t.CaseId != state.CaseId)
            || traces.Select(t => (t.RunId, t.InvestigationId)).Distinct().Count() != traces.Count)
        {
            throw new InvalidDataException("Investigation traces are invalid.");
        }

        foreach (var trace in traces)
        {
            try
            {
                InvestigationTraceRules.Validate(trace);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Investigation traces are invalid.", ex);
            }
        }

        var assessments = state.Assessments ?? Array.Empty<CaseAssessment>();
        if (assessments.Select(a => a.AssessmentId).Distinct(StringComparer.Ordinal).Count() != assessments.Count
            || assessments.Any(a => a.CaseId != state.CaseId))
        {
            throw new InvalidDataException("Case assessments are invalid.");
        }

        foreach (var assessment in assessments)
        {
            try
            {
                AssessmentPersistenceRules.Validate(assessment);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Case assessments are invalid.", ex);
            }
        }

        var items = state.WorkItems ?? Array.Empty<WorkItem>();
        if (items.Select(i => i.ItemId).Distinct(StringComparer.Ordinal).Count() != items.Count
            || items.Any(i => i.CaseId != state.CaseId || string.IsNullOrWhiteSpace(i.ItemId)
                || string.IsNullOrWhiteSpace(i.CapabilityId) || string.IsNullOrWhiteSpace(i.State)
                || string.IsNullOrWhiteSpace(i.Owner)))
        {
            throw new InvalidDataException("Work items are invalid.");
        }
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateSourceBasisContent(string content)
    {
        const string formatName = "source-record-pins-v1";
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            if (HasSourcePinFormatMarker(content))
                throw new BasisIntegrityException("A source basis pin document is malformed.");
            return;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "format") != formatName) return;
            if (!root.TryGetProperty("pins", out var pins) || pins.ValueKind != JsonValueKind.Array || pins.GetArrayLength() == 0)
                throw new BasisIntegrityException("A source basis must contain at least one immutable source pin.");
            var identities = new HashSet<(string SourceId, string RecordId, int Version)>();
            foreach (var pin in pins.EnumerateArray())
            {
                var sourceId = Text(pin, "sourceId");
                var recordId = Text(pin, "recordId");
                var snapshot = Text(pin, "content");
                var digest = Text(pin, "contentDigest");
                var version = pin.TryGetProperty("recordVersion", out var value) && value.TryGetInt32(out var number) ? number : 0;
                if (sourceId is not ("technical" or "maintenance" or "finance" or "records" or "contracts")
                    || recordId is null || snapshot is null || digest is null || version < 1
                    || !string.Equals(digest, Digest(snapshot).ToLowerInvariant(), StringComparison.Ordinal)
                    || !identities.Add((sourceId, recordId, version)))
                    throw new BasisIntegrityException("A source basis contains an invalid or duplicate source pin.");
                using var record = JsonDocument.Parse(snapshot);
                var envelope = record.RootElement;
                if (Text(envelope, "sourceId") != sourceId || Text(envelope, "recordId") != recordId
                    || !envelope.TryGetProperty("recordVersion", out var recordVersion)
                    || !recordVersion.TryGetInt32(out var envelopeVersion) || envelopeVersion != version
                    || Text(envelope, "asOf") is null || Text(envelope, "caseId") is null || Text(envelope, "kind") is null
                    || !envelope.TryGetProperty("stale", out var stale) || stale.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !envelope.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                    || !envelope.TryGetProperty("synthetic", out var synthetic) || synthetic.ValueKind != JsonValueKind.True)
                    throw new BasisIntegrityException("A source pin does not contain a complete synthetic source-record envelope.");
            }
        }
    }

    private static bool HasSourcePinFormatMarker(string content)
    {
        const string formatName = "source-record-pins-v1";
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(content));
        var formatProperty = false;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    formatProperty = string.Equals(reader.GetString(), "format", StringComparison.Ordinal);
                    continue;
                }

                if (formatProperty)
                {
                    formatProperty = false;
                    if (reader.TokenType == JsonTokenType.String
                        && string.Equals(reader.GetString(), formatName, StringComparison.Ordinal))
                        return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;
}
