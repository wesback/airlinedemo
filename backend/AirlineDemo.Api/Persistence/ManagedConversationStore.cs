using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Persistence;

public sealed class ManagedConversationRow
{
    public string Handle { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string CaseId { get; set; } = string.Empty;
    public bool IsListing { get; set; }
    public string EntitlementFingerprint { get; set; } = string.Empty;
    public long CaseRevision { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string ConfigurationDigest { get; set; } = string.Empty;
    public string? EvidenceVersion { get; set; }
    public string? EvidenceHashesDigest { get; set; }
    public string AttemptId { get; set; } = string.Empty;
    public int CreateAttemptCount { get; set; }
    public string? RemoteConversationId { get; set; }
    public string Status { get; set; } = string.Empty;
    public long CreatedAtTicks { get; set; }
    public long ExpiresAtTicks { get; set; }
    public long UpdatedAtTicks { get; set; }
    public int AdmittedTurnCount { get; set; }
    public long RowRevision { get; set; }
    public string? ActiveFence { get; set; }
    public long? TurnDeadlineTicks { get; set; }
    public string? LastResponseId { get; set; }
    public string ResponseItemIdsJson { get; set; } = "[]";
    public string? ResultDigest { get; set; }
    public string? CleanupClaimToken { get; set; }
    public long? CleanupLeaseUntilTicks { get; set; }
    public long? CleanupAttempt1Ticks { get; set; }
    public long? CleanupAttempt2Ticks { get; set; }
    public long? CleanupAttempt3Ticks { get; set; }
    public string CleanupDeletedItemIdsJson { get; set; } = "[]";
    public string CleanupDeletedResponseIdsJson { get; set; } = "[]";
    public bool CleanupConversationDeleted { get; set; }
    public long? CleanupConfirmedAtTicks { get; set; }
}

public sealed record ManagedConversationTuple(
    string UserId,
    string Role,
    string CaseId,
    string EntitlementFingerprint,
    long CaseRevision,
    string AgentName,
    string AgentVersion,
    string ConfigurationDigest,
    string? EvidenceVersion = null,
    string? EvidenceHashesDigest = null,
    bool IsListing = false);

public sealed record ManagedConversationSnapshot(
    string Handle,
    ManagedConversationTuple Tuple,
    string AttemptId,
    string? RemoteConversationId,
    bool Retired,
    long CreatedAtTicks,
    long ExpiresAtTicks,
    int AdmittedTurnCount,
    long RowRevision,
    string? ActiveFence,
    long? TurnDeadlineTicks);

public sealed record ManagedTurnAdmission(
    string Handle,
    string Fence,
    int TurnNumber,
    long RowRevision,
    long DeadlineTicks);

public sealed record ManagedTurnCompletionMetadata(
    string? ResponseId,
    IReadOnlyList<string> ResponseItemIds,
    string? ResultDigest);

public sealed record ManagedCleanupClaim(
    string Handle,
    ManagedConversationTuple Tuple,
    string AttemptId,
    int CreateAttemptCount,
    string? RemoteConversationId,
    string? LastResponseId,
    IReadOnlyList<string> ResponseItemIds,
    string ClaimToken,
    long RowRevision,
    long LeaseUntilTicks,
    IReadOnlyList<string> DeletedItemIds,
    IReadOnlyList<string> DeletedResponseIds,
    bool ConversationDeleted);

public interface IManagedConversationStore
{
    ManagedConversationSnapshot ReserveManagedConversation(ManagedConversationTuple tuple);
    ManagedConversationSnapshot? ReadManagedConversation(string handle);
    ManagedConversationSnapshot BeginRemoteCreate(string handle, ManagedConversationTuple tuple, long expectedRowRevision);
    ManagedConversationSnapshot AttachRemoteConversationId(
        string handle, ManagedConversationTuple tuple, string attemptId, string remoteConversationId, long expectedRowRevision);
    ManagedConversationSnapshot AttachRecoveredRemoteConversationId(
        string handle, ManagedConversationTuple tuple, string attemptId, string remoteConversationId, long expectedRowRevision);
    ManagedTurnAdmission AdmitManagedTurn(
        string handle, ManagedConversationTuple tuple, long expectedRowRevision, long expectedCaseRevision);
    bool CompleteManagedTurn(
        ManagedTurnAdmission admission,
        ManagedConversationTuple tuple,
        ManagedTurnCompletionMetadata metadata);
    bool RetireManagedConversation(
        string handle, ManagedConversationTuple tuple, long expectedRowRevision, string? expectedFence);
    IReadOnlyList<ManagedCleanupClaim> ClaimManagedCleanupBatch(int limit = 100);
    ManagedCleanupClaim AttachRecoveredManagedCleanupRemoteId(ManagedCleanupClaim claim, string remoteConversationId);
    ManagedCleanupClaim RecordManagedCleanupItemDeleted(ManagedCleanupClaim claim, string itemId);
    ManagedCleanupClaim RecordManagedCleanupResponseDeleted(ManagedCleanupClaim claim, string responseId);
    ManagedCleanupClaim RecordManagedCleanupConversationDeleted(ManagedCleanupClaim claim);
    bool ConfirmManagedCleanup(ManagedCleanupClaim claim);
    bool ReleaseManagedCleanup(ManagedCleanupClaim claim);
    int PurgeConfirmedManagedConversations(int limit = 100);
}

public sealed class ManagedConversationStoreException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

public sealed class SqlManagedConversationStore : IManagedConversationStore
{
    public const string ListingCaseSentinel = "$managed-listing";
    public static readonly TimeSpan ConversationLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan TurnDeadline = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CleanupLeaseDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CleanupAttemptWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan CleanupMetadataRetention = TimeSpan.FromDays(7);
    public const int MaximumAdmittedTurns = 20;
    public const int MaximumManagedCleanupClaims = 100;
    public const int MaximumCleanupAttempts = 3;

    private const string Active = "Active";
    private const string Retired = "Retired";
    private readonly DbContextOptions<CaseStateDbContext> _options;
    private readonly TimeProvider _clock;

    public SqlManagedConversationStore(DbContextOptions<CaseStateDbContext> options, TimeProvider? clock = null)
    {
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    public ManagedConversationSnapshot ReserveManagedConversation(ManagedConversationTuple tuple)
    {
        ValidateTuple(tuple);
        var now = UtcTicks();
        var handle = CreateLocalHandle();
        var row = new ManagedConversationRow
        {
            Handle = handle,
            UserId = tuple.UserId,
            Role = tuple.Role,
            CaseId = tuple.CaseId,
            IsListing = tuple.IsListing,
            EntitlementFingerprint = tuple.EntitlementFingerprint,
            CaseRevision = tuple.CaseRevision,
            AgentName = tuple.AgentName,
            AgentVersion = tuple.AgentVersion,
            ConfigurationDigest = tuple.ConfigurationDigest,
            EvidenceVersion = tuple.EvidenceVersion,
            EvidenceHashesDigest = tuple.EvidenceHashesDigest,
            AttemptId = Guid.NewGuid().ToString("N"),
            Status = Active,
            CreatedAtTicks = now,
            ExpiresAtTicks = checked(now + ConversationLifetime.Ticks),
            UpdatedAtTicks = now,
            ResponseItemIdsJson = "[]",
        };

        using var db = new CaseStateDbContext(_options);
        db.ManagedConversations.Add(row);
        db.SaveChanges();
        return Snapshot(row);
    }

    public ManagedConversationSnapshot? ReadManagedConversation(string handle)
    {
        if (!IsValidHandle(handle))
        {
            return null;
        }

        using var db = new CaseStateDbContext(_options);
        var row = db.ManagedConversations.AsNoTracking().FirstOrDefault(x => x.Handle == handle);
        return row is not null && string.Equals(row.Handle, handle, StringComparison.Ordinal) ? Snapshot(row) : null;
    }

    public ManagedConversationSnapshot BeginRemoteCreate(
        string handle, ManagedConversationTuple tuple, long expectedRowRevision)
    {
        using var db = new CaseStateDbContext(_options);
        var row = RequireRow(db, handle);
        RequireTuple(db, row, tuple, UtcTicks());
        if (row.RowRevision != expectedRowRevision || row.Status != Active || row.RemoteConversationId is not null
            || row.AdmittedTurnCount != 0 || row.ActiveFence is not null || row.CreateAttemptCount != 0)
        {
            throw new ManagedConversationStoreException("conversation_context_mismatch");
        }

        var now = UtcTicks();
        var changed = db.ManagedConversations
            .Where(x => x.Handle == handle && x.RowRevision == expectedRowRevision && x.Status == Active
                && x.RemoteConversationId == null && x.AdmittedTurnCount == 0 && x.ActiveFence == null
                && x.CreateAttemptCount == 0 && x.ExpiresAtTicks > now)
            .ExecuteUpdate(update => update
                .SetProperty(x => x.CreateAttemptCount, 1)
                .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
                .SetProperty(x => x.UpdatedAtTicks, now));
        if (changed != 1)
        {
            throw new ManagedConversationStoreException("conversation_context_mismatch");
        }

        return ReadManagedConversation(handle)!;
    }

    public ManagedConversationSnapshot AttachRemoteConversationId(
        string handle,
        ManagedConversationTuple tuple,
        string attemptId,
        string remoteConversationId,
        long expectedRowRevision)
    {
        if (string.IsNullOrWhiteSpace(remoteConversationId) || remoteConversationId.Length > 512)
        {
            throw new ArgumentException("A remote conversation identifier is required.", nameof(remoteConversationId));
        }

        using var db = new CaseStateDbContext(_options);
        var row = RequireRow(db, handle);
        var now = UtcTicks();
        RequireTuple(db, row, tuple, now);
        if (!string.Equals(row.AttemptId, attemptId, StringComparison.Ordinal)
            || row.RowRevision != expectedRowRevision || row.Status != Active || row.RemoteConversationId is not null
            || row.CreateAttemptCount != 1 || now >= row.ExpiresAtTicks)
        {
            throw new ManagedConversationStoreException("conversation_context_mismatch");
        }

        var changed = db.ManagedConversations
            .Where(x => x.Handle == handle && x.RowRevision == expectedRowRevision && x.Status == Active
                && x.AttemptId == attemptId && x.RemoteConversationId == null
                && x.CreateAttemptCount == 1 && x.ExpiresAtTicks > now)
            .ExecuteUpdate(update => update
                .SetProperty(x => x.RemoteConversationId, remoteConversationId)
                .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
                .SetProperty(x => x.UpdatedAtTicks, now));
        if (changed != 1)
        {
            throw new ManagedConversationStoreException("conversation_context_mismatch");
        }

        return ReadManagedConversation(handle)!;
    }

    public ManagedConversationSnapshot AttachRecoveredRemoteConversationId(
        string handle,
        ManagedConversationTuple tuple,
        string attemptId,
        string remoteConversationId,
        long expectedRowRevision)
    {
        if (string.IsNullOrWhiteSpace(remoteConversationId) || remoteConversationId.Length > 512)
        {
            throw new ArgumentException("A remote conversation identifier is required.", nameof(remoteConversationId));
        }

        using var db = new CaseStateDbContext(_options);
        var row = RequireRow(db, handle);
        RequireTuple(db, row, tuple, UtcTicks());
        if (!string.Equals(row.AttemptId, attemptId, StringComparison.Ordinal)
            || row.RowRevision != expectedRowRevision || row.Status != Retired
            || row.RemoteConversationId is not null || row.CreateAttemptCount != 1)
        {
            throw new ManagedConversationStoreException("conversation_context_mismatch");
        }

        var changed = db.ManagedConversations
            .Where(x => x.Handle == handle && x.RowRevision == expectedRowRevision && x.Status == Retired
                && x.AttemptId == attemptId && x.RemoteConversationId == null && x.CreateAttemptCount == 1)
            .ExecuteUpdate(update => update
                .SetProperty(x => x.RemoteConversationId, remoteConversationId)
                .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
                .SetProperty(x => x.UpdatedAtTicks, UtcTicks()));
        if (changed != 1)
        {
            throw new ManagedConversationStoreException("conversation_context_mismatch");
        }

        return ReadManagedConversation(handle)!;
    }

    public ManagedTurnAdmission AdmitManagedTurn(
        string handle, ManagedConversationTuple tuple, long expectedRowRevision, long expectedCaseRevision)
    {
        using var db = new CaseStateDbContext(_options);
        var row = RequireRow(db, handle);
        var now = UtcTicks();
        RequireTuple(db, row, tuple, now);

        if (row.Status != Active)
        {
            throw new ManagedConversationStoreException("conversation_unavailable");
        }

        if (row.RowRevision != expectedRowRevision)
        {
            if (row.ActiveFence is not null && row.TurnDeadlineTicks is { } activeDeadline)
            {
                if (now >= activeDeadline)
                {
                    TryRetire(db, row, row.RowRevision, row.ActiveFence, now);
                    throw new ManagedConversationStoreException("conversation_expired");
                }

                throw new ManagedConversationStoreException("conversation_busy");
            }

            throw new ManagedConversationStoreException("conversation_context_mismatch");
        }

        if (row.RemoteConversationId is null)
        {
            throw new ManagedConversationStoreException("conversation_not_ready");
        }

        if (now >= row.ExpiresAtTicks)
        {
            TryRetire(db, row, expectedRowRevision, row.ActiveFence, now);
            throw new ManagedConversationStoreException("conversation_expired");
        }

        if (row.CaseRevision != expectedCaseRevision || tuple.CaseRevision != expectedCaseRevision)
        {
            TryRetire(db, row, expectedRowRevision, row.ActiveFence, now);
            throw new ManagedConversationStoreException("conversation_context_mismatch");
        }

        if (row.ActiveFence is not null)
        {
            if (row.TurnDeadlineTicks is null || now >= row.TurnDeadlineTicks.Value)
            {
                TryRetire(db, row, expectedRowRevision, row.ActiveFence, now);
                throw new ManagedConversationStoreException("conversation_expired");
            }

            throw new ManagedConversationStoreException("conversation_busy");
        }

        if (row.AdmittedTurnCount >= MaximumAdmittedTurns || (row.IsListing && row.AdmittedTurnCount != 0))
        {
            TryRetire(db, row, expectedRowRevision, null, now);
            throw new ManagedConversationStoreException("conversation_turn_limit");
        }

        var fence = Guid.NewGuid().ToString("N");
        var deadline = checked(now + TurnDeadline.Ticks);
        var candidates = db.ManagedConversations.Where(x =>
            x.Handle == handle && x.RowRevision == expectedRowRevision && x.Status == Active
            && x.RemoteConversationId != null && x.ActiveFence == null
            && x.AdmittedTurnCount < MaximumAdmittedTurns && x.ExpiresAtTicks > now);
        if (!row.IsListing)
        {
            candidates = candidates.Where(x =>
                db.Cases.Any(c => c.CaseId == x.CaseId && c.Revision == expectedCaseRevision));
        }

        var changed = candidates.ExecuteUpdate(update => update
            .SetProperty(x => x.AdmittedTurnCount, x => x.AdmittedTurnCount + 1)
            .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
            .SetProperty(x => x.ActiveFence, fence)
            .SetProperty(x => x.TurnDeadlineTicks, deadline)
            .SetProperty(x => x.UpdatedAtTicks, now));
        if (changed == 1)
        {
            return new ManagedTurnAdmission(handle, fence, row.AdmittedTurnCount + 1, expectedRowRevision + 1, deadline);
        }

        var current = RequireRow(db, handle);
        RequireTuple(db, current, tuple, UtcTicks());
        if (current.Status == Retired)
        {
            throw new ManagedConversationStoreException("conversation_unavailable");
        }

        if (UtcTicks() >= current.ExpiresAtTicks)
        {
            TryRetire(db, current, current.RowRevision, current.ActiveFence, UtcTicks());
            throw new ManagedConversationStoreException("conversation_expired");
        }

        if (current.AdmittedTurnCount >= MaximumAdmittedTurns || (current.IsListing && current.AdmittedTurnCount != 0))
        {
            TryRetire(db, current, current.RowRevision, current.ActiveFence, UtcTicks());
            throw new ManagedConversationStoreException("conversation_turn_limit");
        }

        if (!current.IsListing && !CaseRevisionMatches(db, current.CaseId, expectedCaseRevision))
        {
            TryRetire(db, current, current.RowRevision, current.ActiveFence, UtcTicks());
            throw new ManagedConversationStoreException("conversation_context_mismatch");
        }

        if (current.ActiveFence is not null)
        {
            if (current.TurnDeadlineTicks is null || UtcTicks() >= current.TurnDeadlineTicks.Value)
            {
                TryRetire(db, current, current.RowRevision, current.ActiveFence, UtcTicks());
                throw new ManagedConversationStoreException("conversation_expired");
            }

            throw new ManagedConversationStoreException("conversation_busy");
        }

        throw new ManagedConversationStoreException("conversation_context_mismatch");
    }

    public bool CompleteManagedTurn(
        ManagedTurnAdmission admission,
        ManagedConversationTuple tuple,
        ManagedTurnCompletionMetadata metadata)
    {
        ValidateCompletionMetadata(metadata);
        using var db = new CaseStateDbContext(_options);
        var row = RequireRow(db, admission.Handle);
        var now = UtcTicks();
        RequireTuple(db, row, tuple, now);
        if (row.Status != Active || row.RowRevision != admission.RowRevision
            || !string.Equals(row.ActiveFence, admission.Fence, StringComparison.Ordinal)
            || row.TurnDeadlineTicks is null || now >= row.TurnDeadlineTicks.Value
            || now >= row.ExpiresAtTicks)
        {
            if (row.Status == Active && row.RowRevision == admission.RowRevision
                && string.Equals(row.ActiveFence, admission.Fence, StringComparison.Ordinal))
            {
                TryRetire(db, row, admission.RowRevision, admission.Fence, now);
            }

            return false;
        }

        var candidates = db.ManagedConversations.Where(x =>
            x.Handle == admission.Handle && x.RowRevision == admission.RowRevision
            && x.Status == Active && x.ActiveFence == admission.Fence
            && x.TurnDeadlineTicks > now && x.ExpiresAtTicks > now);
        if (!row.IsListing)
        {
            candidates = candidates.Where(x =>
                db.Cases.Any(c => c.CaseId == x.CaseId && c.Revision == x.CaseRevision));
        }

        var itemIds = JsonSerializer.Serialize(metadata.ResponseItemIds);
        int changed;
        try
        {
            changed = candidates.ExecuteUpdate(update => update
                .SetProperty(x => x.Status, row.IsListing ? Retired : Active)
                .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
                .SetProperty(x => x.ActiveFence, (string?)null)
                .SetProperty(x => x.TurnDeadlineTicks, (long?)null)
                .SetProperty(x => x.LastResponseId, metadata.ResponseId)
                .SetProperty(x => x.ResponseItemIdsJson, itemIds)
                .SetProperty(x => x.ResultDigest, metadata.ResultDigest)
                .SetProperty(x => x.UpdatedAtTicks, now));
        }
        catch (DbException)
        {
            TryRetireAfterCompletionFailure(admission);
            return false;
        }
        if (changed == 1)
        {
            return true;
        }

        var current = db.ManagedConversations.AsNoTracking().FirstOrDefault(x => x.Handle == admission.Handle);
        if (current is not null && current.Status == Active && current.RowRevision == admission.RowRevision
            && string.Equals(current.ActiveFence, admission.Fence, StringComparison.Ordinal))
        {
            TryRetire(db, current, admission.RowRevision, admission.Fence, UtcTicks());
        }

        return false;
    }

    public bool RetireManagedConversation(
        string handle, ManagedConversationTuple tuple, long expectedRowRevision, string? expectedFence)
    {
        using var db = new CaseStateDbContext(_options);
        var row = RequireRow(db, handle);
        var now = UtcTicks();
        RequireTuple(db, row, tuple, now);
        return TryRetire(db, row, expectedRowRevision, expectedFence, now);
    }

    public IReadOnlyList<ManagedCleanupClaim> ClaimManagedCleanupBatch(int limit = MaximumManagedCleanupClaims)
    {
        if (limit is < 1 or > MaximumManagedCleanupClaims)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var now = UtcTicks();
        var cutoff = now - CleanupAttemptWindow.Ticks;
        using var db = new CaseStateDbContext(_options);
        var candidates = db.ManagedConversations.AsNoTracking()
            .Where(x => x.Status == Retired || x.ExpiresAtTicks <= now)
            .Where(x => x.CleanupConfirmedAtTicks == null)
            .Where(x => x.CleanupClaimToken == null || x.CleanupLeaseUntilTicks <= now)
            .Where(x => x.ActiveFence == null || x.TurnDeadlineTicks <= now)
            .Where(x => x.CleanupAttempt1Ticks == null || x.CleanupAttempt1Ticks <= cutoff
                || x.CleanupAttempt2Ticks == null || x.CleanupAttempt2Ticks <= cutoff
                || x.CleanupAttempt3Ticks == null || x.CleanupAttempt3Ticks <= cutoff)
            .OrderBy(x => x.ExpiresAtTicks)
            .ThenBy(x => x.Handle)
            .Take(limit)
            .ToList();
        var claims = new List<ManagedCleanupClaim>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var attempts = new[] { candidate.CleanupAttempt1Ticks, candidate.CleanupAttempt2Ticks,
                    candidate.CleanupAttempt3Ticks }
                .Where(value => value is not null && value > cutoff)
                .Select(value => value!.Value)
                .Order()
                .ToList();
            if (attempts.Count >= MaximumCleanupAttempts)
                continue;

            attempts.Add(now);
            var slots = attempts.Select(value => (long?)value).ToArray();
            var token = Guid.NewGuid().ToString("N");
            var leaseUntil = checked(now + CleanupLeaseDuration.Ticks);
            var changed = db.ManagedConversations
                .Where(x => x.Handle == candidate.Handle && x.RowRevision == candidate.RowRevision
                    && (x.Status == Retired || x.ExpiresAtTicks <= now)
                    && x.CleanupConfirmedAtTicks == null
                    && (x.CleanupClaimToken == null || x.CleanupLeaseUntilTicks <= now)
                    && (x.ActiveFence == null || x.TurnDeadlineTicks <= now)
                    && (x.CleanupAttempt1Ticks == null || x.CleanupAttempt1Ticks <= cutoff
                        || x.CleanupAttempt2Ticks == null || x.CleanupAttempt2Ticks <= cutoff
                        || x.CleanupAttempt3Ticks == null || x.CleanupAttempt3Ticks <= cutoff))
                .ExecuteUpdate(update => update
                    .SetProperty(x => x.Status, Retired)
                    .SetProperty(x => x.ActiveFence, (string?)null)
                    .SetProperty(x => x.TurnDeadlineTicks, (long?)null)
                    .SetProperty(x => x.CleanupAttempt1Ticks, slots[0])
                    .SetProperty(x => x.CleanupAttempt2Ticks, slots.Length > 1 ? slots[1] : null)
                    .SetProperty(x => x.CleanupAttempt3Ticks, slots.Length > 2 ? slots[2] : null)
                    .SetProperty(x => x.CleanupClaimToken, token)
                    .SetProperty(x => x.CleanupLeaseUntilTicks, leaseUntil)
                    .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
                    .SetProperty(x => x.UpdatedAtTicks, now));
            if (changed == 1)
                claims.Add(ReadManagedCleanupClaim(db, candidate.Handle, token)!);
        }

        return claims;
    }

    public ManagedCleanupClaim AttachRecoveredManagedCleanupRemoteId(
        ManagedCleanupClaim claim, string remoteConversationId)
    {
        ValidateCleanupRemoteId(remoteConversationId);
        var now = UtcTicks();
        using var db = new CaseStateDbContext(_options);
        _ = RequireCleanupClaim(db, claim, now);
        var changed = db.ManagedConversations
            .Where(x => x.Handle == claim.Handle && x.RowRevision == claim.RowRevision
                && x.CleanupClaimToken == claim.ClaimToken && x.CleanupLeaseUntilTicks > now
                && x.Status == Retired && x.CreateAttemptCount == 1 && x.RemoteConversationId == null)
            .ExecuteUpdate(update => update
                .SetProperty(x => x.RemoteConversationId, remoteConversationId)
                .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
                .SetProperty(x => x.UpdatedAtTicks, now));
        if (changed != 1)
            throw new ManagedConversationStoreException("managed_cleanup_claim_lost");
        return ReadManagedCleanupClaim(db, claim.Handle, claim.ClaimToken)!;
    }

    public ManagedCleanupClaim RecordManagedCleanupItemDeleted(ManagedCleanupClaim claim, string itemId) =>
        RecordCleanupId(claim, itemId, isItem: true);

    public ManagedCleanupClaim RecordManagedCleanupResponseDeleted(ManagedCleanupClaim claim, string responseId) =>
        RecordCleanupId(claim, responseId, isItem: false);

    public ManagedCleanupClaim RecordManagedCleanupConversationDeleted(ManagedCleanupClaim claim)
    {
        var now = UtcTicks();
        using var db = new CaseStateDbContext(_options);
        _ = RequireCleanupClaim(db, claim, now);
        var changed = db.ManagedConversations
            .Where(x => x.Handle == claim.Handle && x.RowRevision == claim.RowRevision
                && x.CleanupClaimToken == claim.ClaimToken && x.CleanupLeaseUntilTicks > now)
            .ExecuteUpdate(update => update
                .SetProperty(x => x.CleanupConversationDeleted, true)
                .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
                .SetProperty(x => x.UpdatedAtTicks, now));
        if (changed != 1)
            throw new ManagedConversationStoreException("managed_cleanup_claim_lost");
        return ReadManagedCleanupClaim(db, claim.Handle, claim.ClaimToken)!;
    }

    public bool ConfirmManagedCleanup(ManagedCleanupClaim claim)
    {
        var now = UtcTicks();
        using var db = new CaseStateDbContext(_options);
        var row = RequireCleanupClaim(db, claim, now);
        if (!row.CleanupConversationDeleted && !(row.RemoteConversationId is null && row.CreateAttemptCount == 0))
            throw new ManagedConversationStoreException("managed_cleanup_incomplete");
        return db.ManagedConversations
            .Where(x => x.Handle == claim.Handle && x.RowRevision == claim.RowRevision
                && x.CleanupClaimToken == claim.ClaimToken && x.CleanupLeaseUntilTicks > now
                && x.CleanupConfirmedAtTicks == null)
            .ExecuteUpdate(update => update
                .SetProperty(x => x.CleanupConfirmedAtTicks, now)
                .SetProperty(x => x.CleanupClaimToken, (string?)null)
                .SetProperty(x => x.CleanupLeaseUntilTicks, (long?)null)
                .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
                .SetProperty(x => x.UpdatedAtTicks, now)) == 1;
    }

    public bool ReleaseManagedCleanup(ManagedCleanupClaim claim)
    {
        var now = UtcTicks();
        using var db = new CaseStateDbContext(_options);
        return db.ManagedConversations
            .Where(x => x.Handle == claim.Handle && x.RowRevision == claim.RowRevision
                && x.CleanupClaimToken == claim.ClaimToken && x.CleanupLeaseUntilTicks > now)
            .ExecuteUpdate(update => update
                .SetProperty(x => x.CleanupClaimToken, (string?)null)
                .SetProperty(x => x.CleanupLeaseUntilTicks, (long?)null)
                .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
                .SetProperty(x => x.UpdatedAtTicks, now)) == 1;
    }

    public int PurgeConfirmedManagedConversations(int limit = MaximumManagedCleanupClaims)
    {
        if (limit is < 1 or > MaximumManagedCleanupClaims)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var cutoff = UtcTicks() - CleanupMetadataRetention.Ticks;
        using var db = new CaseStateDbContext(_options);
        var rows = db.ManagedConversations.AsNoTracking()
            .Where(x => x.CleanupConfirmedAtTicks <= cutoff && x.CleanupClaimToken == null)
            .OrderBy(x => x.CleanupConfirmedAtTicks)
            .ThenBy(x => x.Handle)
            .Select(x => new { x.Handle, x.RowRevision, x.CleanupConfirmedAtTicks })
            .Take(limit)
            .ToList();
        var deleted = 0;
        foreach (var row in rows)
        {
            deleted += db.ManagedConversations
                .Where(x => x.Handle == row.Handle && x.RowRevision == row.RowRevision
                    && x.CleanupConfirmedAtTicks == row.CleanupConfirmedAtTicks
                    && x.CleanupConfirmedAtTicks <= cutoff && x.CleanupClaimToken == null)
                .ExecuteDelete();
        }
        return deleted;
    }

    private ManagedCleanupClaim RecordCleanupId(ManagedCleanupClaim claim, string id, bool isItem)
    {
        ValidateCleanupRemoteId(id);
        var now = UtcTicks();
        using var db = new CaseStateDbContext(_options);
        var row = RequireCleanupClaim(db, claim, now);
        var current = DeserializeCleanupIds(isItem ? row.CleanupDeletedItemIdsJson : row.CleanupDeletedResponseIdsJson);
        if (current.Contains(id, StringComparer.Ordinal))
            return ToCleanupClaim(row);
        current.Add(id);
        var json = JsonSerializer.Serialize(current);
        var candidates = db.ManagedConversations
            .Where(x => x.Handle == claim.Handle && x.RowRevision == claim.RowRevision
                && x.CleanupClaimToken == claim.ClaimToken && x.CleanupLeaseUntilTicks > now);
        var changed = isItem
            ? candidates.ExecuteUpdate(update => update.SetProperty(x => x.CleanupDeletedItemIdsJson, json)
                    .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
                    .SetProperty(x => x.UpdatedAtTicks, now))
            : candidates.ExecuteUpdate(update => update.SetProperty(x => x.CleanupDeletedResponseIdsJson, json)
                    .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
                    .SetProperty(x => x.UpdatedAtTicks, now));
        if (changed != 1)
            throw new ManagedConversationStoreException("managed_cleanup_claim_lost");
        return ReadManagedCleanupClaim(db, claim.Handle, claim.ClaimToken)!;
    }

    private static ManagedConversationRow RequireCleanupClaim(
        CaseStateDbContext db, ManagedCleanupClaim claim, long now)
    {
        var row = db.ManagedConversations.AsNoTracking().FirstOrDefault(x => x.Handle == claim.Handle);
        if (row is null || row.RowRevision != claim.RowRevision
            || !string.Equals(row.CleanupClaimToken, claim.ClaimToken, StringComparison.Ordinal)
            || row.CleanupLeaseUntilTicks is null || row.CleanupLeaseUntilTicks <= now)
            throw new ManagedConversationStoreException("managed_cleanup_claim_lost");
        return row;
    }

    private static ManagedCleanupClaim? ReadManagedCleanupClaim(
        CaseStateDbContext db, string handle, string token)
    {
        var row = db.ManagedConversations.AsNoTracking()
            .FirstOrDefault(x => x.Handle == handle && x.CleanupClaimToken == token);
        return row is null ? null : ToCleanupClaim(row);
    }

    private static ManagedCleanupClaim ToCleanupClaim(ManagedConversationRow row) =>
        new(row.Handle,
            new ManagedConversationTuple(
                row.UserId, row.Role, row.CaseId, row.EntitlementFingerprint, row.CaseRevision,
                row.AgentName, row.AgentVersion, row.ConfigurationDigest, row.EvidenceVersion,
                row.EvidenceHashesDigest, row.IsListing),
            row.AttemptId, row.CreateAttemptCount, row.RemoteConversationId,
            row.LastResponseId, DeserializeCleanupIds(row.ResponseItemIdsJson),
            row.CleanupClaimToken ?? throw new ManagedConversationStoreException("managed_cleanup_claim_lost"),
            row.RowRevision, row.CleanupLeaseUntilTicks ?? 0,
            DeserializeCleanupIds(row.CleanupDeletedItemIdsJson),
            DeserializeCleanupIds(row.CleanupDeletedResponseIdsJson),
            row.CleanupConversationDeleted);

    private static List<string> DeserializeCleanupIds(string json)
    {
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(json);
            if (ids is null || ids.Count > 100_000 || ids.Any(id => !IsCleanupIdentifier(id))
                || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                throw new JsonException();
            return ids;
        }
        catch (JsonException)
        {
            throw new ManagedConversationStoreException("managed_cleanup_metadata_invalid");
        }
    }

    private static bool IsCleanupIdentifier(string? value) =>
        value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void ValidateCleanupRemoteId(string value)
    {
        if (!IsCleanupIdentifier(value))
            throw new ArgumentException("A valid remote identifier is required.", nameof(value));
    }

    private long UtcTicks() => _clock.GetUtcNow().UtcTicks;

    private void TryRetireAfterCompletionFailure(ManagedTurnAdmission admission)
    {
        try
        {
            using var db = new CaseStateDbContext(_options);
            var row = db.ManagedConversations.AsNoTracking()
                .FirstOrDefault(x => x.Handle == admission.Handle);
            if (row is not null && row.RowRevision == admission.RowRevision
                && string.Equals(row.ActiveFence, admission.Fence, StringComparison.Ordinal))
            {
                TryRetire(db, row, admission.RowRevision, admission.Fence, UtcTicks());
            }
        }
        catch (DbException)
        {
        }
    }

    private static bool TryRetire(
        CaseStateDbContext db, ManagedConversationRow row, long expectedRowRevision, string? expectedFence, long now)
    {
        if (row.Status == Retired || row.RowRevision != expectedRowRevision)
        {
            return false;
        }

        return db.ManagedConversations
            .Where(x => x.Handle == row.Handle && x.RowRevision == expectedRowRevision
                && x.Status == Active && x.ActiveFence == expectedFence)
            .ExecuteUpdate(update => update
                .SetProperty(x => x.Status, Retired)
                .SetProperty(x => x.RowRevision, x => x.RowRevision + 1)
                .SetProperty(x => x.ActiveFence, (string?)null)
                .SetProperty(x => x.TurnDeadlineTicks, (long?)null)
                .SetProperty(x => x.UpdatedAtTicks, now)) == 1;
    }

    private static ManagedConversationRow RequireRow(CaseStateDbContext db, string handle)
    {
        if (!IsValidHandle(handle))
        {
            throw new ManagedConversationStoreException("invalid_conversation_handle");
        }

        var row = db.ManagedConversations.AsNoTracking().FirstOrDefault(x => x.Handle == handle);
        if (row is null || !string.Equals(row.Handle, handle, StringComparison.Ordinal))
        {
            throw new ManagedConversationStoreException("conversation_not_found");
        }

        return row;
    }

    private static void RequireTuple(
        CaseStateDbContext db, ManagedConversationRow row, ManagedConversationTuple tuple, long now)
    {
        if (!string.Equals(row.UserId, tuple.UserId, StringComparison.Ordinal))
        {
            throw new ManagedConversationStoreException("conversation_not_found");
        }

        if (!TupleMatches(row, tuple))
        {
            TryRetire(db, row, row.RowRevision, row.ActiveFence, now);
            throw new ManagedConversationStoreException("conversation_context_mismatch");
        }
    }

    private static bool TupleMatches(ManagedConversationRow row, ManagedConversationTuple tuple) =>
        string.Equals(row.UserId, tuple.UserId, StringComparison.Ordinal)
        && string.Equals(row.Role, tuple.Role, StringComparison.Ordinal)
        && string.Equals(row.CaseId, tuple.CaseId, StringComparison.Ordinal)
        && string.Equals(row.EntitlementFingerprint, tuple.EntitlementFingerprint, StringComparison.Ordinal)
        && row.CaseRevision == tuple.CaseRevision
        && string.Equals(row.AgentName, tuple.AgentName, StringComparison.Ordinal)
        && string.Equals(row.AgentVersion, tuple.AgentVersion, StringComparison.Ordinal)
        && string.Equals(row.ConfigurationDigest, tuple.ConfigurationDigest, StringComparison.Ordinal)
        && string.Equals(row.EvidenceVersion, tuple.EvidenceVersion, StringComparison.Ordinal)
        && string.Equals(row.EvidenceHashesDigest, tuple.EvidenceHashesDigest, StringComparison.Ordinal)
        && row.IsListing == tuple.IsListing;

    private static bool CaseRevisionMatches(CaseStateDbContext db, string caseId, long revision) =>
        db.Cases.AsNoTracking().Any(x => x.CaseId == caseId && x.Revision == revision);

    private static ManagedConversationSnapshot Snapshot(ManagedConversationRow row) =>
        new(row.Handle,
            new ManagedConversationTuple(
                row.UserId, row.Role, row.CaseId, row.EntitlementFingerprint, row.CaseRevision,
                row.AgentName, row.AgentVersion, row.ConfigurationDigest, row.EvidenceVersion,
                row.EvidenceHashesDigest, row.IsListing),
            row.AttemptId, row.RemoteConversationId, row.Status == Retired, row.CreatedAtTicks,
            row.ExpiresAtTicks, row.AdmittedTurnCount, row.RowRevision, row.ActiveFence, row.TurnDeadlineTicks);

    private static void ValidateTuple(ManagedConversationTuple tuple)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        Required(tuple.UserId, 256, nameof(tuple.UserId));
        Required(tuple.Role, 64, nameof(tuple.Role));
        Required(tuple.CaseId, 128, nameof(tuple.CaseId));
        Required(tuple.EntitlementFingerprint, 128, nameof(tuple.EntitlementFingerprint));
        Required(tuple.AgentName, 128, nameof(tuple.AgentName));
        Required(tuple.AgentVersion, 128, nameof(tuple.AgentVersion));
        Required(tuple.ConfigurationDigest, 128, nameof(tuple.ConfigurationDigest));
        Optional(tuple.EvidenceVersion, 128, nameof(tuple.EvidenceVersion));
        Optional(tuple.EvidenceHashesDigest, 128, nameof(tuple.EvidenceHashesDigest));
        if (tuple.IsListing != string.Equals(tuple.CaseId, ListingCaseSentinel, StringComparison.Ordinal)
            || (tuple.IsListing && tuple.CaseRevision != 0) || (!tuple.IsListing && tuple.CaseRevision < 0))
        {
            throw new ArgumentException("The selected case and listing revision do not match.", nameof(tuple));
        }
    }

    private static void Required(string value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new ArgumentException("A required managed conversation field is invalid.", name);
        }
    }

    private static void Optional(string? value, int maxLength, string name)
    {
        if (value is { Length: > 0 } && value.Length > maxLength)
        {
            throw new ArgumentException("A managed conversation field is too long.", name);
        }
    }

    private static void ValidateCompletionMetadata(ManagedTurnCompletionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.ResponseId is { Length: > 512 }
            || metadata.ResultDigest is { Length: > 128 }
            || metadata.ResponseItemIds.Count > 64
            || metadata.ResponseItemIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 512))
        {
            throw new ArgumentException("Managed completion metadata exceeds its bounds.", nameof(metadata));
        }
    }

    private static string CreateLocalHandle()
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return value.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool IsValidHandle(string? handle) =>
        handle is { Length: 43 }
        && handle.All(c => (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z')
            || (c is >= '0' and <= '9') || c is '-' or '_');
}

public static class ManagedConversationStoreRegistration
{
    public static IServiceCollection AddManagedConversationStore(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(CaseStateStoreRegistration.ConnectionStringName);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.TryAddSingleton<DbContextOptions<CaseStateDbContext>>(_ =>
            {
                CaseStateStoreRegistration.ValidateManagedIdentityConnectionString(connectionString);
                return new DbContextOptionsBuilder<CaseStateDbContext>()
                    .UseSqlServer(connectionString)
                    .Options;
            });
        }

        services.TryAddSingleton<IManagedConversationStore>(provider =>
        {
            var options = provider.GetService<DbContextOptions<CaseStateDbContext>>();
            return options is null
                ? new UnavailableManagedConversationStore()
                : new SqlManagedConversationStore(options, provider.GetService<TimeProvider>());
        });
        return services;
    }

    private sealed class UnavailableManagedConversationStore : IManagedConversationStore
    {
        private static ManagedConversationStoreException Unavailable() =>
            new("conversation_store_unavailable");

        public ManagedConversationSnapshot ReserveManagedConversation(ManagedConversationTuple tuple) => throw Unavailable();

        public ManagedConversationSnapshot? ReadManagedConversation(string handle) => throw Unavailable();

        public ManagedConversationSnapshot BeginRemoteCreate(
            string handle, ManagedConversationTuple tuple, long expectedRowRevision) => throw Unavailable();

        public ManagedConversationSnapshot AttachRemoteConversationId(
            string handle, ManagedConversationTuple tuple, string attemptId, string remoteConversationId,
            long expectedRowRevision) => throw Unavailable();

        public ManagedConversationSnapshot AttachRecoveredRemoteConversationId(
            string handle, ManagedConversationTuple tuple, string attemptId, string remoteConversationId,
            long expectedRowRevision) => throw Unavailable();

        public ManagedTurnAdmission AdmitManagedTurn(
            string handle, ManagedConversationTuple tuple, long expectedRowRevision, long expectedCaseRevision) =>
            throw Unavailable();

        public bool CompleteManagedTurn(
            ManagedTurnAdmission admission, ManagedConversationTuple tuple,
            ManagedTurnCompletionMetadata metadata) => throw Unavailable();

        public bool RetireManagedConversation(
            string handle, ManagedConversationTuple tuple, long expectedRowRevision, string? expectedFence) =>
            throw Unavailable();

        public IReadOnlyList<ManagedCleanupClaim> ClaimManagedCleanupBatch(int limit = 100) => throw Unavailable();

        public ManagedCleanupClaim AttachRecoveredManagedCleanupRemoteId(
            ManagedCleanupClaim claim, string remoteConversationId) => throw Unavailable();

        public ManagedCleanupClaim RecordManagedCleanupItemDeleted(
            ManagedCleanupClaim claim, string itemId) => throw Unavailable();

        public ManagedCleanupClaim RecordManagedCleanupResponseDeleted(
            ManagedCleanupClaim claim, string responseId) => throw Unavailable();

        public ManagedCleanupClaim RecordManagedCleanupConversationDeleted(
            ManagedCleanupClaim claim) => throw Unavailable();

        public bool ConfirmManagedCleanup(ManagedCleanupClaim claim) => throw Unavailable();

        public bool ReleaseManagedCleanup(ManagedCleanupClaim claim) => throw Unavailable();

        public int PurgeConfirmedManagedConversations(int limit = 100) => throw Unavailable();
    }
}
