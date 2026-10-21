using System.Collections.ObjectModel;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Persistence;

/// <summary>
/// Azure SQL adapter for <see cref="ICaseStateStore"/> built on <see cref="CaseStateDbContext"/>.
/// It only reads and inserts: audit events are appended by <see cref="Commit"/> and never updated or deleted.
/// Schema changes are applied by the migration identity, never by this store.
/// </summary>
public sealed class SqlCaseStateStore : ICaseStateStore
{
    private readonly DbContextOptions<CaseStateDbContext> _options;

    public SqlCaseStateStore(DbContextOptions<CaseStateDbContext> options) => _options = options;

    public CaseScope AuthorizeCase(string caseId, string owner)
    {
        using var db = NewContext();
        if (string.IsNullOrEmpty(caseId) || string.IsNullOrEmpty(owner))
        {
            throw new CaseAccessDeniedException();
        }

        var row = FindCase(db, caseId);
        if (row is null || !Same(row.Owner, owner))
        {
            throw new CaseAccessDeniedException();
        }

        return new CaseScope(caseId);
    }

    public string? OwnerOf(string caseId)
    {
        using var db = NewContext();
        return FindCase(db, caseId)?.Owner;
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

        using var db = NewContext();
        if (FindCase(db, caseId) is not null)
        {
            throw new InvalidOperationException("Case already exists.");
        }

        db.Cases.Add(new CaseRow { CaseId = caseId, Owner = owner, Revision = 0 });
        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // The primary key is unique under the database collation, which can be stricter than ordinal matching.
            throw new InvalidOperationException("Case already exists.");
        }

        return new CaseScope(caseId);
    }

    public CaseState Read(CaseScope scope)
    {
        using var db = NewContext();
        return Load(db, scope.CaseId);
    }

    public BasisVersion GetBasisVersion(CaseScope scope, int version)
    {
        using var db = NewContext();
        return Load(db, scope.CaseId).Basis.FirstOrDefault(b => b.Version == version)
            ?? throw new KeyNotFoundException("Basis version not found for this case.");
    }

    public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope)
    {
        using var db = NewContext();
        return Load(db, scope.CaseId).Audit;
    }

    public IReadOnlyList<CaseAssessment> ListAssessments(CaseScope scope)
    {
        using var db = NewContext();
        return Load(db, scope.CaseId).Assessments ?? Array.Empty<CaseAssessment>();
    }

    public CaseAssessment? FindAssessment(CaseScope scope, string assessmentId)
    {
        using var db = NewContext();
        return Load(db, scope.CaseId).Assessments!
            .FirstOrDefault(record => Same(record.AssessmentId, assessmentId));
    }

    public IReadOnlyList<DeliveryReceipt> ListRequestReceipts(CaseScope scope)
    {
        using var db = NewContext();
        return Load(db, scope.CaseId).RequestReceipts ?? Array.Empty<DeliveryReceipt>();
    }

    /// <summary>
    /// The only mutation path. Runs in one serializable transaction: rejects a stale revision and an invalid basis,
    /// replays an exact receipt without a new revision, and otherwise inserts basis, assessment and receipt history,
    /// decision, and one audit event.
    /// </summary>
    public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
    {
        if (commit.JourneyEvent is { } journeyEvent)
        {
            CaseJourneyRules.Validate(journeyEvent);
            if (!Same(journeyEvent.CaseId, scope.CaseId))
            {
                throw new ArgumentException("Journey event case id must match the authorized case.", nameof(commit));
            }
        }

        if (commit.Assessment is { } assessment)
        {
            AssessmentPersistenceRules.Validate(assessment);
            if (!Same(assessment.CaseId, scope.CaseId))
            {
                throw new ArgumentException("Assessment case id must match the authorized case.", nameof(commit));
            }
        }

        if (commit.RequestReceipt is { } requestReceipt)
        {
            AssessmentPersistenceRules.Validate(requestReceipt);
            if (!Same(requestReceipt.CaseId, scope.CaseId))
            {
                throw new ArgumentException("Request receipt case id must match the authorized case.", nameof(commit));
            }
        }

        CaseStateStore.ValidateBlockedVariantResetCommit(scope, expectedRevision, commit);

        using var db = NewContext();
        using var tx = db.Database.BeginTransaction(IsolationLevel.Serializable);

        var row = FindCase(db, scope.CaseId) ?? throw new CaseAccessDeniedException();
        var current = Load(db, row.CaseId);
        var fingerprint = CaseStateStore.CommitFingerprint(commit);

        if (commit.ReceiptId is { } receiptId)
        {
            var existing = db.CaseDecisions
                .Where(d => d.CaseId == row.CaseId && d.ReceiptId == receiptId)
                .AsEnumerable()
                .FirstOrDefault(d => Same(d.ReceiptId, receiptId));
            if (existing is not null)
            {
                if (!Same(existing.Fingerprint, fingerprint))
                {
                    throw new ReceiptConflictException();
                }

                tx.Commit();
                return new CommitResult(current, Replayed: true);
            }
        }

        if (expectedRevision != row.Revision)
        {
            throw new RevisionConflictException();
        }

        if (commit.NewBasis is { } next)
        {
            var previousDigest = current.Basis.Count == 0 ? string.Empty : current.Basis[^1].BasisDigest;
            if (next.Version != current.Basis.Count + 1)
            {
                throw new BasisIntegrityException("Basis version must be the next version; prior versions are immutable.");
            }

            if (next.ContentDigest != Digest(next.Content)
                || next.BasisDigest != Digest(previousDigest + "|" + next.ContentDigest))
            {
                throw new BasisIntegrityException("Basis content or chain digest does not match.");
            }

            db.CaseDocuments.Add(new CaseDocumentRow
            {
                CaseId = row.CaseId,
                Version = next.Version,
                Content = next.Content,
                ContentDigest = next.ContentDigest,
                BasisDigest = next.BasisDigest,
                CreatedAt = next.CreatedAt,
            });
        }

        if (commit.Assessment is { } newAssessment)
        {
            if (FindAssessmentRow(db, row.CaseId, newAssessment.AssessmentId) is not null)
            {
                throw new InvalidOperationException("Assessment id already exists in this case.");
            }

            db.CaseAssessments.Add(new CaseAssessmentRow
            {
                CaseId = row.CaseId,
                AssessmentId = newAssessment.AssessmentId,
                BasisId = newAssessment.BasisId,
                BasisVersion = newAssessment.BasisVersion,
                Outcome = newAssessment.Outcome,
                Reason = newAssessment.Reason,
                RequestId = newAssessment.RequestId,
                WorkItemId = newAssessment.WorkItemId,
                ClaimsJson = JsonSerializer.Serialize(newAssessment.Claims),
            });
        }

        if (commit.RequestReceipt is { } newRequestReceipt)
        {
            if (FindRequestReceiptRow(db, row.CaseId, newRequestReceipt.EventId) is not null)
            {
                throw new InvalidOperationException("Request receipt event already exists in this case.");
            }

            db.CaseRequestReceipts.Add(new CaseRequestReceiptRow
            {
                CaseId = row.CaseId,
                EventId = newRequestReceipt.EventId,
                RequestId = newRequestReceipt.RequestId,
                ReportId = newRequestReceipt.ReportId,
                BasisId = newRequestReceipt.BasisId,
                BasisVersion = newRequestReceipt.BasisVersion,
                TemplateVersion = newRequestReceipt.TemplateVersion,
                Body = newRequestReceipt.Body,
            });
        }

        if (commit.JourneyEvent is { } newJourneyEvent)
        {
            if (current.JourneyEvents!.Any(item => Same(item.EventId, newJourneyEvent.EventId)))
            {
                throw new JourneyEventConflictException();
            }

            var persistedEvent = CaseJourneyRules.FromDraft(newJourneyEvent, current.JourneyEvents.Count + 1);
            db.CaseJourneyEvents.Add(new CaseJourneyEventRow
            {
                CaseId = row.CaseId,
                Sequence = persistedEvent.Sequence,
                EventId = persistedEvent.EventId,
                RunId = persistedEvent.RunId,
                EventType = persistedEvent.EventType,
                BasisId = persistedEvent.BasisId,
                BasisVersion = persistedEvent.BasisVersion,
                ActorRole = persistedEvent.ActorRole,
                Summary = persistedEvent.Summary,
                CapabilityLabel = persistedEvent.CapabilityLabel,
                WitnessesJson = JsonSerializer.Serialize(persistedEvent.Witnesses),
            });
        }

        if (commit.BlockedVariantResetReceipt is { } newResetReceipt)
        {
            var reset = newResetReceipt.Receipt;
            db.CasePackResetReceipts.Add(new CasePackResetReceiptRow
            {
                CommandIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(reset.CommandId)),
                CommandId = reset.CommandId,
                CaseId = reset.CaseId,
                Actor = newResetReceipt.Actor,
                ExpectedRevision = newResetReceipt.ExpectedRevision,
                Variant = newResetReceipt.Variant,
                PriorRevision = reset.PriorRevision,
                NewRevision = reset.NewRevision,
                NewRunId = reset.NewRunId,
                AuditEventId = reset.AuditEventId,
            });
        }

        if (commit.ReceiptId is { } newReceiptId)
        {
            db.CaseDecisions.Add(new CaseDecisionRow
            {
                CaseId = row.CaseId,
                ReceiptId = newReceiptId,
                Fingerprint = fingerprint,
            });
        }

        row.Revision = current.Revision + 1;
        db.CaseEvents.Add(new CaseEventRow
        {
            CaseId = row.CaseId,
            Revision = row.Revision,
            Actor = commit.Actor,
            Action = commit.Action,
            Detail = commit.Detail,
            At = DateTimeOffset.UtcNow,
        });

        db.SaveChanges();
        tx.Commit();
        return new CommitResult(Load(db, row.CaseId), Replayed: false);
    }

    public CasePackResetCommitResult CommitBlockedVariantReset(
        CaseScope scope, long expectedRevision, string actor, string commandId, string variant,
        DateTimeOffset? auditAt = null)
        => CasePackResetCommitter.Commit(this, scope, expectedRevision, actor, commandId, variant, auditAt);

    public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope)
    {
        using var db = NewContext();
        return Load(db, scope.CaseId).WorkItems ?? Array.Empty<WorkItem>();
    }

    /// <summary>Creates a work item at the current revision; the store assigns the creation timestamp.</summary>
    public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor)
    {
        WorkItemRules.Validate(draft);
        return MutateWorkItems(scope, expectedRevision, actor, "create-work-item", draft.ItemId, (db, caseId, now) =>
        {
            if (FindWorkItem(db, caseId, draft.ItemId) is not null)
            {
                throw new InvalidOperationException("Work item already exists in this case.");
            }

            db.WorkItems.Add(new WorkItemRow
            {
                CaseId = caseId,
                ItemId = draft.ItemId,
                CapabilityId = draft.CapabilityId,
                State = draft.State,
                Owner = draft.Owner,
                CreatedAt = now,
            });
        });
    }

    public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor)
    {
        WorkItemRules.RequireText(itemId, nameof(itemId));
        WorkItemRules.RequireText(state, nameof(state));
        return MutateWorkItems(scope, expectedRevision, actor, "change-work-item-state", itemId + " -> " + state, (db, caseId, _) =>
        {
            var row = FindWorkItem(db, caseId, itemId) ?? throw new KeyNotFoundException("Work item not found for this case.");
            row.State = state;
        });
    }

    /// <summary>Runs one work-item change in a serializable transaction with the revision check and one audit event.</summary>
    private CommitResult MutateWorkItems(
        CaseScope scope,
        long expectedRevision,
        string actor,
        string action,
        string detail,
        Action<CaseStateDbContext, string, DateTimeOffset> change)
    {
        using var db = NewContext();
        using var tx = db.Database.BeginTransaction(IsolationLevel.Serializable);

        var row = FindCase(db, scope.CaseId) ?? throw new CaseAccessDeniedException();
        if (expectedRevision != row.Revision)
        {
            throw new RevisionConflictException();
        }

        var now = DateTimeOffset.UtcNow;
        change(db, row.CaseId, now);
        row.Revision += 1;
        db.CaseEvents.Add(new CaseEventRow
        {
            CaseId = row.CaseId,
            Revision = row.Revision,
            Actor = actor,
            Action = action,
            Detail = detail,
            At = now,
        });

        db.SaveChanges();
        tx.Commit();
        return new CommitResult(Load(db, row.CaseId), Replayed: false);
    }

    /// <summary>Appends a trace. Traces are never updated or deleted; a duplicate (case, run, investigation) fails and keeps the first record.</summary>
    public void AppendTrace(CaseScope scope, InvestigationTrace trace)
    {
        InvestigationTraceRules.Validate(trace);
        const int MaxTraceIdentifierUtf16Units = 128;
        foreach (var (name, value) in new[]
        {
            (nameof(trace.CaseId), trace.CaseId),
            (nameof(trace.RunId), trace.RunId),
            (nameof(trace.InvestigationId), trace.InvestigationId),
        })
        {
            if (value.Length > MaxTraceIdentifierUtf16Units)
            {
                throw new ArgumentException("Trace identifiers are limited to 128 UTF-16 code units.", name);
            }
        }

        if (trace.CaseId != scope.CaseId)
        {
            throw new ArgumentException("Trace case id must match the authorized case.", nameof(trace));
        }

        using var db = NewContext();
        using var tx = db.Database.BeginTransaction(IsolationLevel.Serializable);
        var keyHash = TraceKey(trace.CaseId, trace.RunId, trace.InvestigationId);
        if (FindTraceRow(db, trace.CaseId, trace.RunId, trace.InvestigationId) is not null)
        {
            throw new TraceConflictException();
        }

        db.InvestigationTraces.Add(new InvestigationTraceRow
        {
            KeyHash = keyHash,
            CaseId = trace.CaseId,
            RunId = trace.RunId,
            InvestigationId = trace.InvestigationId,
            BasisVersion = trace.BasisVersion,
            ModelDeploymentName = trace.ModelDeploymentName,
            ApiVersion = trace.ApiVersion,
            Region = trace.Region,
            ContentFilterSetting = trace.ContentFilterSetting,
            Decision = trace.Decision,
            ActorToken = trace.ActorToken,
            Outcome = trace.Outcome,
            CaseIdUtf16 = Utf16Bytes(trace.CaseId),
            RunIdUtf16 = Utf16Bytes(trace.RunId),
            InvestigationIdUtf16 = Utf16Bytes(trace.InvestigationId),
            CaseIdLength = trace.CaseId.Length,
            RunIdLength = trace.RunId.Length,
            InvestigationIdLength = trace.InvestigationId.Length,
        });
        db.InvestigationTraceSteps.AddRange(trace.Steps.Select(s => new InvestigationTraceStepRow
        {
            KeyHash = keyHash,
            Sequence = s.Sequence,
            ToolName = s.ToolName,
            DocumentId = s.DocumentId,
            PageNumber = s.PageNumber,
            CitationCheckOutcome = s.CitationCheckOutcome,
            CorrectionCode = s.CorrectionCode,
            PolicyHandover = s.PolicyHandover,
        }));

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            throw new TraceConflictException();
        }

        tx.Commit();
    }

    /// <summary>Returns the trace only when the case, run and investigation ids all match exactly; otherwise null.</summary>
    public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId)
    {
        if (!Same(caseId, scope.CaseId))
        {
            return null;
        }

        using var db = NewContext();
        var row = FindTraceRow(db, caseId, runId, investigationId);
        if (row is null)
        {
            return null;
        }

        var keyHash = row.KeyHash;
        var steps = db.InvestigationTraceSteps
            .Where(s => s.KeyHash == keyHash)
            .OrderBy(s => s.Sequence)
            .ToList()
            .Select(s => new InvestigationTraceStep(
                s.Sequence,
                s.ToolName,
                s.DocumentId,
                s.PageNumber,
                s.CitationCheckOutcome,
                s.CorrectionCode,
                s.PolicyHandover))
            .ToList();

        return new InvestigationTrace(
            caseId,
            runId,
            investigationId,
            row.BasisVersion,
            row.ModelDeploymentName,
            row.ApiVersion,
            row.Region,
            row.ContentFilterSetting,
            row.Decision,
            row.ActorToken,
            row.Outcome,
            steps);
    }

    /// <summary>Finds a trace by key, then applies an ordinal check so the database collation cannot widen a match.</summary>
    private static InvestigationTraceRow? FindTraceRow(CaseStateDbContext db, string caseId, string runId, string investigationId)
    {
        if (string.IsNullOrEmpty(caseId) || string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(investigationId))
        {
            return null;
        }

        var keyHash = TraceKey(caseId, runId, investigationId);
        var row = db.InvestigationTraces.FirstOrDefault(t => t.KeyHash == keyHash);
        return row is not null
            && row.CaseIdUtf16.AsSpan().SequenceEqual(Utf16Bytes(caseId))
            && row.RunIdUtf16.AsSpan().SequenceEqual(Utf16Bytes(runId))
            && row.InvestigationIdUtf16.AsSpan().SequenceEqual(Utf16Bytes(investigationId))
                ? row
                : null;
    }

    // Hashes the exact, length-prefixed (case, run, investigation) triple. Each UTF-16 code unit is written as-is,
    // so lone surrogates and U+FFFD stay distinct; SQL padding or collation cannot merge distinct ids.
    private static byte[] TraceKey(string caseId, string runId, string investigationId)
    {
        using var stream = new System.IO.MemoryStream();
        foreach (var part in new[] { caseId, runId, investigationId })
        {
            WriteUInt32(stream, part.Length);
            foreach (var unit in part)
            {
                stream.WriteByte((byte)(unit >> 8));
                stream.WriteByte((byte)unit);
            }
        }

        return SHA256.HashData(stream.ToArray());
    }

    // Exact code units, little-endian, so lone surrogates survive where SQL text would replace them.
    private static byte[] Utf16Bytes(string value)
    {
        var bytes = new byte[value.Length * 2];
        for (var i = 0; i < value.Length; i++)
        {
            bytes[2 * i] = (byte)value[i];
            bytes[2 * i + 1] = (byte)(value[i] >> 8);
        }

        return bytes;
    }

    private static void WriteUInt32(System.IO.Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static WorkItemRow? FindWorkItem(CaseStateDbContext db, string caseId, string itemId) =>
        db.WorkItems
            .Where(w => w.CaseId == caseId && w.ItemId == itemId)
            .AsEnumerable()
            .FirstOrDefault(w => Same(w.CaseId, caseId) && Same(w.ItemId, itemId));

    private static CaseAssessmentRow? FindAssessmentRow(CaseStateDbContext db, string caseId, string assessmentId) =>
        db.CaseAssessments
            .Where(a => a.CaseId == caseId && a.AssessmentId == assessmentId)
            .AsEnumerable()
            .FirstOrDefault(a => Same(a.CaseId, caseId) && Same(a.AssessmentId, assessmentId));

    private static CaseRequestReceiptRow? FindRequestReceiptRow(CaseStateDbContext db, string caseId, string eventId) =>
        db.CaseRequestReceipts
            .Where(r => r.CaseId == caseId && r.EventId == eventId)
            .AsEnumerable()
            .FirstOrDefault(r => Same(r.CaseId, caseId) && Same(r.EventId, eventId));

    private CaseStateDbContext NewContext() => new(_options);

    /// <summary>Finds a case by key, then applies an ordinal check so the database collation cannot widen a match.</summary>
    private static CaseRow? FindCase(CaseStateDbContext db, string caseId)
    {
        if (string.IsNullOrEmpty(caseId))
        {
            return null;
        }

        var row = db.Cases.FirstOrDefault(c => c.CaseId == caseId);
        return row is not null && Same(row.CaseId, caseId) ? row : null;
    }

    private static CaseState Load(CaseStateDbContext db, string caseId)
    {
        var row = FindCase(db, caseId) ?? throw new CaseAccessDeniedException();

        var basis = db.CaseDocuments
            .Where(d => d.CaseId == caseId)
            .OrderBy(d => d.Version)
            .AsEnumerable()
            .Where(d => Same(d.CaseId, caseId))
            .Select(d => new BasisVersion(d.Version, d.Content, d.ContentDigest, d.BasisDigest, d.CreatedAt))
            .ToList();

        var decisions = db.CaseDecisions
            .Where(d => d.CaseId == caseId)
            .OrderBy(d => d.Id)
            .AsEnumerable()
            .Where(d => Same(d.CaseId, caseId))
            .ToList();

        var audit = db.CaseEvents
            .Where(e => e.CaseId == caseId)
            .OrderBy(e => e.Sequence)
            .AsEnumerable()
            .Where(e => Same(e.CaseId, caseId))
            .Select((e, i) => new AuditEvent(i + 1, e.Revision, e.Actor, e.Action, e.Detail, e.At))
            .ToList();

        var items = db.WorkItems
            .Where(w => w.CaseId == caseId)
            .AsEnumerable()
            .Where(w => Same(w.CaseId, caseId))
            .OrderBy(w => w.CreatedAt)
            .ThenBy(w => w.ItemId, StringComparer.Ordinal)
            .Select(w => new WorkItem(w.CaseId, w.ItemId, w.CapabilityId, w.State, w.Owner, w.CreatedAt))
            .ToList();

        var assessments = db.CaseAssessments
            .Where(a => a.CaseId == caseId)
            .AsEnumerable()
            .Where(a => Same(a.CaseId, caseId))
            .OrderBy(a => a.AssessmentId, StringComparer.Ordinal)
            .Select(a => new CaseAssessment(
                a.AssessmentId,
                a.CaseId,
                a.BasisId,
                a.BasisVersion,
                a.Outcome,
                a.Reason,
                JsonSerializer.Deserialize<IReadOnlyList<AssessedClaim>>(a.ClaimsJson)
                    ?? throw new InvalidDataException("Assessment claims are unreadable."),
                a.RequestId,
                a.WorkItemId))
            .Select(CaseStateStore.SnapshotAssessment)
            .ToList();

        var requestReceipts = db.CaseRequestReceipts
            .Where(r => r.CaseId == caseId)
            .AsEnumerable()
            .Where(r => Same(r.CaseId, caseId))
            .OrderBy(r => r.EventId, StringComparer.Ordinal)
            .Select(r => new DeliveryReceipt(
                r.EventId, r.RequestId, r.CaseId, r.ReportId, r.BasisId,
                r.BasisVersion, r.TemplateVersion, r.Body))
            .ToList();

        var journeyEvents = db.CaseJourneyEvents
            .Where(e => e.CaseId == caseId)
            .OrderBy(e => e.Sequence)
            .AsEnumerable()
            .Where(e => Same(e.CaseId, caseId))
            .Select(e => CaseJourneyRules.Snapshot(new CaseJourneyEvent(
                e.EventId,
                e.RunId,
                e.Sequence,
                e.CaseId,
                e.EventType,
                e.BasisId,
                e.BasisVersion,
                e.ActorRole,
                e.Summary,
                e.CapabilityLabel,
                JsonSerializer.Deserialize<IReadOnlyList<CaseJourneyWitness>>(e.WitnessesJson)
                    ?? throw new InvalidDataException("Journey event witnesses are unreadable."))))
            .ToList();

        var resetReceipts = db.CasePackResetReceipts
            .AsEnumerable()
            .Where(receipt => Same(receipt.CaseId, caseId))
            .OrderBy(receipt => receipt.PriorRevision)
            .Select(receipt => new CasePackResetCommandReceipt(
                new CasePackResetReceipt(
                    1,
                    receipt.CommandId,
                    receipt.CaseId,
                    receipt.PriorRevision,
                    receipt.NewRevision,
                    receipt.NewRunId,
                    receipt.AuditEventId),
                    receipt.Actor,
                    receipt.ExpectedRevision,
                    receipt.Variant))
            .ToList();

        if (journeyEvents.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() != journeyEvents.Count
            || journeyEvents.Where((item, index) => item.Sequence != index + 1 || !Same(item.CaseId, caseId)).Any())
        {
            throw new InvalidDataException("Journey event history is invalid.");
        }

        foreach (var journeyEvent in journeyEvents)
        {
            try
            {
                CaseJourneyRules.Validate(journeyEvent);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Journey event history is invalid.", ex);
            }
        }

        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            fingerprints[decision.ReceiptId] = decision.Fingerprint;
        }

        return new CaseState(
            row.CaseId,
            row.Owner,
            row.Revision,
            basis.AsReadOnly(),
            decisions.Select(d => d.ReceiptId).ToList().AsReadOnly(),
            audit.AsReadOnly(),
            new ReadOnlyDictionary<string, string>(fingerprints),
            items.AsReadOnly(),
            Assessments: assessments.AsReadOnly(),
            RequestReceipts: requestReceipts.AsReadOnly(),
            JourneyEvents: journeyEvents.AsReadOnly(),
            BlockedVariantResetReceipts: resetReceipts.AsReadOnly());
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>Digest of the commit payload, used to tell a true replay from a reused receipt ID.</summary>
    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Registers the case-state store from <c>ConnectionStrings:CaseState</c>.</summary>
public static class CaseStateStoreRegistration
{
    public const string ConnectionStringName = "CaseState";

    private const string RequiredAuthentication = "Active Directory Managed Identity";

    private static readonly string[] ForbiddenKeys = ["userid", "user", "uid", "password", "pwd", "accesstoken"];

    /// <summary>
    /// Selects the case-state store with <c>Persistence:CaseStateProvider</c>: <c>File</c> (default) or <c>Sql</c>.
    /// <c>Sql</c> reads <c>ConnectionStrings:CaseState</c>, which must use managed identity only.
    /// Unknown values fail registration.
    /// </summary>
    public static IServiceCollection AddCaseStateStore(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Persistence:CaseStateProvider"];
        if (string.IsNullOrWhiteSpace(provider) || string.Equals(provider, "File", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ICaseStateStore>(_ => new CaseStateStore(
                configuration["CaseState:Path"] ?? Path.Combine(AppContext.BaseDirectory, "case-state.json")));
            return services;
        }

        if (!string.Equals(provider, "Sql", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Persistence:CaseStateProvider must be File or Sql, but was '{provider}'.");
        }

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:CaseState is required when Persistence:CaseStateProvider is Sql.");
        }

        ValidateManagedIdentityConnectionString(connectionString);
        var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlServer(connectionString).Options;
        services.AddSingleton<ICaseStateStore>(_ => new SqlCaseStateStore(options));
        return services;
    }

    /// <summary>Rejects any connection string that carries a username, password, or token, or that does not use managed identity.</summary>
    public static void ValidateManagedIdentityConnectionString(string connectionString)
    {
        if (connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(s => !s.Contains('=')))
        {
            throw new InvalidOperationException("ConnectionStrings:CaseState must contain only key=value pairs.");
        }

        var pairs = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .Select(p => (Key: p[0].Replace(" ", string.Empty).ToLowerInvariant(), Value: p[1].Trim()))
            .ToList();

        if (pairs.Any(p => ForbiddenKeys.Contains(p.Key)))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:CaseState must not contain a user ID, password, or access token; use managed identity.");
        }

        // A repeated Authentication key would let a later value override the managed identity setting.
        var authentication = pairs.Where(p => p.Key == "authentication").ToList();
        if (authentication.Count != 1
            || !string.Equals(authentication[0].Value, RequiredAuthentication, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:CaseState must specify Authentication={RequiredAuthentication}.");
        }
    }
}
