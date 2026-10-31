using AirlineDemo.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AirlineDemo.Api.Copilot;

/// <summary>
/// The immutable final-release proposal: the canonical command (proposal, caller, exact session, case, action, expected
/// revision, normalized note) plus creation and expiry. Caller and session are keyed ledger references, never raw ids.
/// </summary>
public sealed record CopilotProposal(
    Guid ProposalId, string CallerRef, string SessionRef, string CaseId, string Action, long ExpectedRevision, string Note,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)
{
    public const string FinalRelease = "final-release";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
}

public static class CopilotProposalStates
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Declined = "declined";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
    public const string Consumed = "consumed";
}

/// <summary>The immutable proposal with its separate mutable lifecycle state.</summary>
public sealed record CopilotProposalEntry(CopilotProposal Proposal, string State)
{
    /// <summary>Pending and approved proposals are expired from exactly <c>ExpiresAt</c>; approval never extends it.</summary>
    public string EffectiveState(DateTimeOffset now) =>
        State is CopilotProposalStates.Pending or CopilotProposalStates.Approved && now >= Proposal.ExpiresAt ? CopilotProposalStates.Expired : State;
}

public interface ICopilotProposalStore
{
    void Add(CopilotProposal proposal);

    CopilotProposalEntry? Find(Guid proposalId);

    /// <summary>
    /// Atomically reads the entry and applies <paramref name="next"/>; a null result leaves the state unchanged.
    /// Returns the entry as stored afterwards, or null when the proposal does not exist.
    /// </summary>
    CopilotProposalEntry? Update(Guid proposalId, Func<CopilotProposalEntry, string?> next);
}

/// <summary>
/// In-memory proposals for the file-backed case store. The lock is the case store's commit lock, so lifecycle changes
/// and the release commit are serialized by one lock.
/// </summary>
public sealed class InMemoryCopilotProposalStore(object syncRoot) : ICopilotProposalStore
{
    private readonly Dictionary<Guid, CopilotProposalEntry> _entries = [];

    public object SyncRoot { get; } = syncRoot;

    public void Add(CopilotProposal proposal)
    {
        lock (SyncRoot)
        {
            if (!_entries.TryAdd(proposal.ProposalId, new CopilotProposalEntry(proposal with { }, CopilotProposalStates.Pending)))
                throw new InvalidOperationException("Duplicate proposal id.");
        }
    }

    public CopilotProposalEntry? Find(Guid proposalId)
    {
        lock (SyncRoot) return _entries.GetValueOrDefault(proposalId);
    }

    public CopilotProposalEntry? Update(Guid proposalId, Func<CopilotProposalEntry, string?> next)
    {
        lock (SyncRoot)
        {
            if (!_entries.TryGetValue(proposalId, out var entry)) return null;
            if (next(entry) is { } state) _entries[proposalId] = entry = entry with { State = state };
            return entry;
        }
    }
}

public sealed class CopilotProposalRow
{
    public Guid ProposalId { get; set; }
    public string CallerRef { get; set; } = string.Empty;
    public string SessionRef { get; set; } = string.Empty;
    public string CaseId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public long ExpectedRevision { get; set; }
    public string Note { get; set; } = string.Empty;
    public long CreatedAtTicks { get; set; }
    public long ExpiresAtTicks { get; set; }
}

public sealed class CopilotProposalStateRow
{
    public Guid ProposalId { get; set; }
    public string State { get; set; } = string.Empty;
}

/// <summary>SQL proposals: the tuple row is insert-only; only the separate state row is updated, in a serializable transaction.</summary>
public sealed class SqlCopilotProposalStore(DbContextOptions<CaseStateDbContext> options) : ICopilotProposalStore
{
    public void Add(CopilotProposal proposal)
    {
        using var db = new CaseStateDbContext(options);
        db.CopilotProposals.Add(new CopilotProposalRow
        {
            ProposalId = proposal.ProposalId, CallerRef = proposal.CallerRef, SessionRef = proposal.SessionRef, CaseId = proposal.CaseId,
            Action = proposal.Action, ExpectedRevision = proposal.ExpectedRevision, Note = proposal.Note,
            CreatedAtTicks = proposal.CreatedAt.UtcTicks, ExpiresAtTicks = proposal.ExpiresAt.UtcTicks,
        });
        db.CopilotProposalStates.Add(new CopilotProposalStateRow { ProposalId = proposal.ProposalId, State = CopilotProposalStates.Pending });
        db.SaveChanges();
    }

    public CopilotProposalEntry? Find(Guid proposalId)
    {
        using var db = new CaseStateDbContext(options);
        return Read(db, proposalId);
    }

    public CopilotProposalEntry? Update(Guid proposalId, Func<CopilotProposalEntry, string?> next)
    {
        using var db = new CaseStateDbContext(options);
        using var tx = db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var entry = Read(db, proposalId);
        if (entry is null) return null;
        if (next(entry) is { } state)
        {
            db.CopilotProposalStates.Single(row => row.ProposalId == proposalId).State = state;
            db.SaveChanges();
            entry = entry with { State = state };
        }
        tx.Commit();
        return entry;
    }

    internal static CopilotProposalEntry? Read(CaseStateDbContext db, Guid proposalId)
    {
        var row = db.CopilotProposals.AsNoTracking().SingleOrDefault(item => item.ProposalId == proposalId);
        var state = db.CopilotProposalStates.AsNoTracking().SingleOrDefault(item => item.ProposalId == proposalId);
        return row is null || state is null ? null : new CopilotProposalEntry(new CopilotProposal(row.ProposalId, row.CallerRef, row.SessionRef,
            row.CaseId, row.Action, row.ExpectedRevision, row.Note, new DateTimeOffset(row.CreatedAtTicks, TimeSpan.Zero),
            new DateTimeOffset(row.ExpiresAtTicks, TimeSpan.Zero)), state.State);
    }
}

public static class CopilotProposalRegistration
{
    public static IServiceCollection AddCopilotProposals(this IServiceCollection services, IConfiguration configuration)
    {
        if (string.Equals(configuration["Persistence:CaseStateProvider"], "Sql", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration.GetConnectionString(CaseStateStoreRegistration.ConnectionStringName)!;
            var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlServer(connectionString).Options;
            services.AddSingleton<ICopilotProposalStore>(new SqlCopilotProposalStore(options));
        }
        else
        {
            // Shares the file-backed store's commit lock; any other store (a test double) gets a lock of its own.
            services.AddSingleton<ICopilotProposalStore>(provider => new InMemoryCopilotProposalStore(
                (provider.GetRequiredService<ICaseStateStore>() as CaseStateStore)?.SyncRoot ?? new object()));
        }
        services.AddSingleton<CopilotProposalService>();
        return services;
    }
}
