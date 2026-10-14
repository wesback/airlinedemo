using System.Collections.ObjectModel;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// The only mutation path. Runs in one serializable transaction: rejects a stale revision and an invalid basis,
    /// replays an exact receipt without a new revision, and otherwise inserts basis, decision, and one audit event.
    /// </summary>
    public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
    {
        using var db = NewContext();
        using var tx = db.Database.BeginTransaction(IsolationLevel.Serializable);

        var row = FindCase(db, scope.CaseId) ?? throw new CaseAccessDeniedException();
        var current = Load(db, row.CaseId);
        var fingerprint = Fingerprint(commit);

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
            new ReadOnlyDictionary<string, string>(fingerprints));
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>Digest of the commit payload, used to tell a true replay from a reused receipt ID.</summary>
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

/// <summary>Registers the case-state store from <c>ConnectionStrings:CaseState</c>.</summary>
public static class CaseStateStoreRegistration
{
    public const string ConnectionStringName = "CaseState";

    private const string RequiredAuthentication = "Active Directory Managed Identity";

    private static readonly string[] ForbiddenKeys = ["userid", "uid", "password", "pwd", "accesstoken"];

    /// <summary>
    /// Uses the Azure SQL store when <c>ConnectionStrings:CaseState</c> is set; without it, the file-backed store
    /// stays in place for local development and tests. The SQL connection string must use managed identity only.
    /// </summary>
    public static IServiceCollection AddCaseStateStore(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<ICaseStateStore>(_ => new CaseStateStore(
                configuration["CaseState:Path"] ?? Path.Combine(AppContext.BaseDirectory, "case-state.json")));
            return services;
        }

        ValidateManagedIdentityConnectionString(connectionString);
        var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlServer(connectionString).Options;
        services.AddSingleton<ICaseStateStore>(_ => new SqlCaseStateStore(options));
        return services;
    }

    /// <summary>Rejects any connection string that carries a username, password, or token, or that does not use managed identity.</summary>
    public static void ValidateManagedIdentityConnectionString(string connectionString)
    {
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
