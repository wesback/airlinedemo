using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AirlineDemo.Api.Persistence;

public sealed class CaseAccessDeniedException()
    : Exception("Case not found or not in scope.");

public sealed class RevisionConflictException()
    : Exception("Case revision is stale; no change was made.");

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

public sealed record CaseState(
    string CaseId,
    long Revision,
    IReadOnlyList<BasisVersion> Basis,
    IReadOnlyList<string> ReceiptIds,
    IReadOnlyList<AuditEvent> Audit);

public sealed record CaseCommit(
    BasisVersion? NewBasis,
    string? ReceiptId,
    string Actor,
    string Action,
    string Detail);

public sealed record CommitResult(CaseState State, bool Replayed);

internal sealed record CaseStoreDocument(int SchemaVersion, List<CaseState> Cases);

/// <summary>
/// File-backed, scoped, versioned case state. Every mutation goes through <see cref="Commit"/>,
/// which checks the server-issued scope and current revision, validates basis digests, and
/// persists atomically: on any failure neither memory nor the file changes.
/// The audit log is exposed read-only; there is no update or delete operation.
/// </summary>
public sealed class CaseStateStore
{
    private const int SchemaVersion = 1;

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

    public CaseScope AuthorizeCase(string caseId)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(caseId) || !_cases.ContainsKey(caseId))
            {
                throw new CaseAccessDeniedException();
            }

            return new CaseScope(caseId);
        }
    }

    public CaseScope CreateCase(string caseId)
    {
        if (string.IsNullOrWhiteSpace(caseId))
        {
            throw new ArgumentException("Case id is required.", nameof(caseId));
        }

        lock (_gate)
        {
            if (_cases.ContainsKey(caseId))
            {
                throw new InvalidOperationException("Case already exists.");
            }

            var snapshot = new Dictionary<string, CaseState>(_cases)
            {
                [caseId] = new(caseId, 0, [], [], []),
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
        lock (_gate)
        {
            var current = Current(scope);

            if (commit.ReceiptId is not null && current.ReceiptIds.Contains(commit.ReceiptId))
            {
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

                basis.Add(next);
            }

            var receipts = current.ReceiptIds.ToList();
            if (commit.ReceiptId is not null)
            {
                receipts.Add(commit.ReceiptId);
            }

            var revision = current.Revision + 1;
            var audit = current.Audit.ToList();
            audit.Add(new AuditEvent(audit.Count + 1, revision, commit.Actor, commit.Action, commit.Detail, DateTimeOffset.UtcNow));

            var updated = current with { Revision = revision, Basis = basis, ReceiptIds = receipts, Audit = audit };
            var snapshot = new Dictionary<string, CaseState>(_cases) { [scope.CaseId] = updated };
            Persist(snapshot);
            _cases = snapshot;
            return new CommitResult(updated, Replayed: false);
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
            if (!cases.TryAdd(state.CaseId, state))
            {
                throw new InvalidDataException("Duplicate case in case state file.");
            }
        }

        return cases;
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

            previousDigest = basis.BasisDigest;
        }

        if (state.ReceiptIds.Distinct(StringComparer.Ordinal).Count() != state.ReceiptIds.Count)
        {
            throw new InvalidDataException("Duplicate receipt in case state file.");
        }
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
