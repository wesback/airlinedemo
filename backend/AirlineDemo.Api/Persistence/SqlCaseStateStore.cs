using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Persistence;

/// <summary>Azure SQL case state, same contract as <see cref="CaseStateStore"/>; commits are one transaction.</summary>
public sealed class SqlCaseStateStore(DbContextOptions<CaseStateDbContext> options) : ICaseStateStore
{
    public CaseScope AuthorizeCase(string caseId, string owner)
    {
        using var db = new CaseStateDbContext(options);
        var row = Find(db, caseId);
        if (row is null || string.IsNullOrEmpty(owner) || !string.Equals(row.Owner, owner, StringComparison.Ordinal))
        {
            throw new CaseAccessDeniedException();
        }

        return new CaseScope(caseId);
    }

    public string? OwnerOf(string caseId)
    {
        using var db = new CaseStateDbContext(options);
        return Find(db, caseId)?.Owner;
    }

    public CaseScope CreateCase(string caseId, string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        using var db = new CaseStateDbContext(options);
        if (Find(db, caseId) is not null)
        {
            throw new InvalidOperationException("Case already exists.");
        }

        db.Cases.Add(new CaseRow { CaseId = caseId, Owner = owner, Revision = 0 });
        db.SaveChanges();
        return new CaseScope(caseId);
    }

    public CaseState Read(CaseScope scope)
    {
        using var db = new CaseStateDbContext(options);
        return Load(db, scope.CaseId);
    }

    public BasisVersion GetBasisVersion(CaseScope scope, int version)
    {
        using var db = new CaseStateDbContext(options);
        RequireCase(db, scope.CaseId);
        var row = db.CaseDocuments.AsNoTracking()
            .FirstOrDefault(d => d.CaseId == scope.CaseId && d.Version == version)
            ?? throw new KeyNotFoundException("Basis version not found for this case.");
        return ToBasis(row);
    }

    public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope)
    {
        using var db = new CaseStateDbContext(options);
        RequireCase(db, scope.CaseId);
        return Audit(db, scope.CaseId);
    }

    public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
    {
        using var db = new CaseStateDbContext(options);
        using var tx = db.Database.BeginTransaction();
        var current = Load(db, scope.CaseId);
        var fingerprint = CaseStateStore.Fingerprint(commit);

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

        var revision = current.Revision + 1;
        if (commit.NewBasis is { } next)
        {
            var previousDigest = current.Basis.Count == 0 ? string.Empty : current.Basis[^1].BasisDigest;
            if (next.Version != current.Basis.Count + 1)
            {
                throw new BasisIntegrityException("Basis version must be the next version; prior versions are immutable.");
            }

            if (next.ContentDigest != CaseStateStore.Digest(next.Content)
                || next.BasisDigest != CaseStateStore.Digest(previousDigest + "|" + next.ContentDigest))
            {
                throw new BasisIntegrityException("Basis content or chain digest does not match.");
            }

            db.CaseDocuments.Add(new CaseDocumentRow
            {
                CaseId = scope.CaseId,
                Version = next.Version,
                Content = next.Content,
                ContentDigest = next.ContentDigest,
                BasisDigest = next.BasisDigest,
                CreatedAt = next.CreatedAt,
            });
        }

        // A concurrent writer that already moved the revision makes this UPDATE match zero rows.
        var claimed = db.Cases.Where(c => c.CaseId == scope.CaseId && c.Revision == expectedRevision)
            .ExecuteUpdate(s => s.SetProperty(c => c.Revision, revision));
        if (claimed != 1)
        {
            throw new RevisionConflictException();
        }

        if (commit.ReceiptId is not null)
        {
            db.CaseDecisions.Add(new CaseDecisionRow
            {
                CaseId = scope.CaseId,
                ReceiptId = commit.ReceiptId,
                Fingerprint = fingerprint,
                Revision = revision,
            });
        }

        db.CaseEvents.Add(new CaseEventRow
        {
            CaseId = scope.CaseId,
            Sequence = revision,
            Revision = revision,
            Actor = commit.Actor,
            Action = commit.Action,
            Detail = commit.Detail,
            At = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        var result = Load(db, scope.CaseId);
        tx.Commit();
        return new CommitResult(result, Replayed: false);
    }

    // Case ids are matched ordinally in code, because the database collation may ignore case.
    private static CaseRow? Find(CaseStateDbContext db, string caseId)
    {
        if (string.IsNullOrEmpty(caseId))
        {
            return null;
        }

        var row = db.Cases.AsNoTracking().FirstOrDefault(c => c.CaseId == caseId);
        return row is not null && string.Equals(row.CaseId, caseId, StringComparison.Ordinal) ? row : null;
    }

    private static void RequireCase(CaseStateDbContext db, string caseId) =>
        _ = Find(db, caseId) ?? throw new CaseAccessDeniedException();

    private static CaseState Load(CaseStateDbContext db, string caseId)
    {
        var row = Find(db, caseId) ?? throw new CaseAccessDeniedException();
        var decisions = db.CaseDecisions.AsNoTracking().Where(d => d.CaseId == caseId).OrderBy(d => d.Revision).ToList();
        var basis = db.CaseDocuments.AsNoTracking().Where(d => d.CaseId == caseId).OrderBy(d => d.Version)
            .ToList().Select(ToBasis).ToList();
        return new CaseState(
            row.CaseId,
            row.Owner,
            row.Revision,
            basis,
            decisions.Select(d => d.ReceiptId).ToList(),
            Audit(db, caseId),
            decisions.ToDictionary(d => d.ReceiptId, d => d.Fingerprint, StringComparer.Ordinal));
    }

    private static IReadOnlyList<AuditEvent> Audit(CaseStateDbContext db, string caseId) =>
        db.CaseEvents.AsNoTracking().Where(e => e.CaseId == caseId).OrderBy(e => e.Sequence).ToList()
            .Select(e => new AuditEvent(e.Sequence, e.Revision, e.Actor, e.Action, e.Detail, e.At)).ToList();

    private static BasisVersion ToBasis(CaseDocumentRow d) =>
        new(d.Version, d.Content, d.ContentDigest, d.BasisDigest, d.CreatedAt);
}

/// <summary>Wiring for the Azure SQL adapter. Nothing here opens a connection.</summary>
public static class CaseStateServices
{
    public const string MigrateSwitch = "migrate-case-state";

    public static IServiceCollection AddSqlCaseState(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => SqlOptions(configuration.GetConnectionString("CaseState")));
        services.AddSingleton<SqlCaseStateStore>();
        // Resolved lazily so test hosts can add CaseState:Path after registration; a set path keeps the file store.
        services.AddSingleton<ICaseStateStore>(sp =>
        {
            var path = sp.GetRequiredService<IConfiguration>()["CaseState:Path"];
            return string.IsNullOrEmpty(path) ? sp.GetRequiredService<SqlCaseStateStore>() : new CaseStateStore(path);
        });
        return services;
    }

    /// <summary>Applies the versioned migrations. Called only under the migration identity, never by the app.</summary>
    public static void Migrate(string? migrationConnectionString)
    {
        var options = new DbContextOptionsBuilder<CaseStateDbContext>()
            .UseSqlServer(migrationConnectionString ?? throw new InvalidOperationException("ConnectionStrings__CaseStateMigration is required."))
            .Options;
        using var db = new CaseStateDbContext(options);
        db.Database.Migrate();
    }

    public static DbContextOptions<CaseStateDbContext> SqlOptions(string? connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (builder.Authentication != SqlAuthenticationMethod.ActiveDirectoryManagedIdentity
            || !string.IsNullOrEmpty(builder.UserID) || !string.IsNullOrEmpty(builder.Password))
        {
            throw new InvalidOperationException("Case state connection must use managed identity with no username, password or access key.");
        }

        return new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlServer(connectionString).Options;
    }
}
