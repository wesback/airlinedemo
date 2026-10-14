using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AirlineDemo.Api.Persistence;

// SQL-backed ICaseStateStore over CaseStateDbContext. Each mutation runs in one serializable transaction, so the
// receipt, revision and basis checks and their writes are atomic. Behaviour matches CaseStateStore.
public sealed class SqlCaseStateStore(DbContextOptions<CaseStateDbContext> options) : ICaseStateStore
{
    private readonly DbContextOptions<CaseStateDbContext> _options = options;

    public CaseScope AuthorizeCase(string caseId, string owner)
    {
        if (string.IsNullOrEmpty(caseId) || string.IsNullOrEmpty(owner))
        {
            throw new CaseAccessDeniedException();
        }

        using var db = new CaseStateDbContext(_options);
        var row = LoadCase(db, caseId, tracked: false);
        if (row is null || !string.Equals(row.Owner, owner, StringComparison.Ordinal))
        {
            throw new CaseAccessDeniedException();
        }

        return new CaseScope(caseId);
    }

    public string? OwnerOf(string caseId)
    {
        using var db = new CaseStateDbContext(_options);
        return LoadCase(db, caseId, tracked: false)?.Owner;
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

        using var db = new CaseStateDbContext(_options);
        if (LoadCase(db, caseId, tracked: false) is not null)
        {
            throw new InvalidOperationException("Case already exists.");
        }

        db.Cases.Add(new CaseRow { CaseId = caseId, Owner = owner, Revision = 0 });
        db.SaveChanges();
        return new CaseScope(caseId);
    }

    public CaseState Read(CaseScope scope)
    {
        using var db = new CaseStateDbContext(_options);
        return ReadState(db, scope);
    }

    public BasisVersion GetBasisVersion(CaseScope scope, int version) =>
        Read(scope).Basis.FirstOrDefault(b => b.Version == version)
            ?? throw new KeyNotFoundException("Basis version not found for this case.");

    public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => Read(scope).Audit;

    public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
    {
        using var db = new CaseStateDbContext(_options);
        using var tx = db.Database.BeginTransaction(IsolationLevel.Serializable);
        var row = LoadCase(db, scope.CaseId, tracked: true) ?? throw new CaseAccessDeniedException();
        var fingerprint = Fingerprint(commit);

        if (commit.ReceiptId is not null)
        {
            var existing = db.CaseDecisions.AsNoTracking()
                .FirstOrDefault(d => d.CaseId == row.CaseId && d.ReceiptId == commit.ReceiptId);
            if (existing is not null)
            {
                if (existing.Fingerprint != fingerprint)
                {
                    throw new ReceiptConflictException();
                }

                return new CommitResult(ReadState(db, scope), Replayed: true);
            }
        }

        if (expectedRevision != row.Revision)
        {
            throw new RevisionConflictException();
        }

        if (commit.NewBasis is { } next)
        {
            var docs = db.CaseDocuments.AsNoTracking().Where(d => d.CaseId == row.CaseId).OrderBy(d => d.Version).ToList();
            var previousDigest = docs.Count == 0 ? string.Empty : docs[^1].BasisDigest;
            if (next.Version != docs.Count + 1)
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

        if (commit.ReceiptId is not null)
        {
            db.CaseDecisions.Add(new CaseDecisionRow { CaseId = row.CaseId, ReceiptId = commit.ReceiptId, Fingerprint = fingerprint });
        }

        row.Revision += 1;
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
        return new CommitResult(ReadState(db, scope), Replayed: false);
    }

    // Case ids and owners are compared ordinally, as in CaseStateStore; SQL collations are case-insensitive.
    private static CaseRow? LoadCase(CaseStateDbContext db, string caseId, bool tracked)
    {
        IQueryable<CaseRow> query = tracked ? db.Cases : db.Cases.AsNoTracking();
        var row = query.FirstOrDefault(c => c.CaseId == caseId);
        return row is not null && string.Equals(row.CaseId, caseId, StringComparison.Ordinal) ? row : null;
    }

    private static CaseState ReadState(CaseStateDbContext db, CaseScope scope)
    {
        var row = LoadCase(db, scope.CaseId, tracked: false) ?? throw new CaseAccessDeniedException();

        var basis = db.CaseDocuments.AsNoTracking()
            .Where(d => d.CaseId == row.CaseId)
            .OrderBy(d => d.Version)
            .Select(d => new BasisVersion(d.Version, d.Content, d.ContentDigest, d.BasisDigest, d.CreatedAt))
            .ToList();
        var decisions = db.CaseDecisions.AsNoTracking()
            .Where(d => d.CaseId == row.CaseId)
            .OrderBy(d => d.Id)
            .ToList();
        var events = db.CaseEvents.AsNoTracking()
            .Where(e => e.CaseId == row.CaseId)
            .OrderBy(e => e.Sequence)
            .ToList();
        var audit = events
            .Select((e, index) => new AuditEvent(index + 1, e.Revision, e.Actor, e.Action, e.Detail, e.At))
            .ToList();

        return new CaseState(
            row.CaseId,
            row.Owner,
            row.Revision,
            basis,
            decisions.Select(d => d.ReceiptId).ToList(),
            audit,
            decisions.ToDictionary(d => d.ReceiptId, d => d.Fingerprint, StringComparer.Ordinal));
    }

    // Must stay identical to CaseStateStore's fingerprint so receipts recorded by either store replay the same way.
    private static string Fingerprint(CaseCommit commit) => Digest(JsonSerializer.Serialize(new
    {
        basisVersion = commit.NewBasis?.Version,
        content = commit.NewBasis?.Content,
        contentDigest = commit.NewBasis?.ContentDigest,
        basisDigest = commit.NewBasis?.BasisDigest,
        commit.Actor,
        commit.Action,
        commit.Detail,
    }));

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
