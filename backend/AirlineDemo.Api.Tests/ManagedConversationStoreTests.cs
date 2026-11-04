using AirlineDemo.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class ManagedConversationStoreTests
{
    [Fact]
    public void Reserve_NullRemoteId_ThenExactRevisionAttach()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var tuple = CaseTuple();

        var reserved = store.ReserveManagedConversation(tuple);

        Assert.Equal(43, reserved.Handle.Length);
        Assert.True(SqlManagedConversationStore.IsValidHandle(reserved.Handle));
        Assert.Equal(32, Convert.FromBase64String(reserved.Handle.Replace('-', '+').Replace('_', '/') + "=").Length);
        Assert.Null(reserved.RemoteConversationId);
        Assert.Equal(tuple, reserved.Tuple);
        Assert.Equal(0, reserved.RowRevision);
        Assert.Equal(reserved.CreatedAtTicks + SqlManagedConversationStore.ConversationLifetime.Ticks, reserved.ExpiresAtTicks);

        var attempted = store.BeginRemoteCreate(reserved.Handle, tuple, reserved.RowRevision);
        Assert.Equal(1, attempted.RowRevision);
        Assert.Throws<ManagedConversationStoreException>(() =>
            store.BeginRemoteCreate(reserved.Handle, tuple, attempted.RowRevision));

        var attached = store.AttachRemoteConversationId(
            reserved.Handle, tuple, reserved.AttemptId, "remote-conversation-1", attempted.RowRevision);
        Assert.Equal("remote-conversation-1", attached.RemoteConversationId);
        Assert.Equal(2, attached.RowRevision);
        Assert.Throws<ManagedConversationStoreException>(() =>
            store.AttachRemoteConversationId(
                reserved.Handle, tuple, reserved.AttemptId, "remote-conversation-2", attached.RowRevision));
        Assert.Equal(attached, store.ReadManagedConversation(reserved.Handle));

        using var db = fixture.NewContext();
        var row = Assert.Single(db.ManagedConversations.AsNoTracking().ToList());
        Assert.Equal(tuple.UserId, row.UserId);
        Assert.Equal(tuple.Role, row.Role);
        Assert.Equal(tuple.CaseId, row.CaseId);
        Assert.Equal(tuple.EntitlementFingerprint, row.EntitlementFingerprint);
        Assert.Equal(tuple.CaseRevision, row.CaseRevision);
        Assert.Equal(tuple.AgentName, row.AgentName);
        Assert.Equal(tuple.AgentVersion, row.AgentVersion);
        Assert.Equal(tuple.ConfigurationDigest, row.ConfigurationDigest);
        Assert.Equal(1, row.CreateAttemptCount);
        Assert.Equal("[]", row.ResponseItemIdsJson);
    }

    [Fact]
    public void Attach_RejectsSecondOrDuplicateRemoteIdentity()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var first = store.ReserveManagedConversation(CaseTuple());
        var second = store.ReserveManagedConversation(CaseTuple());
        var firstAttempt = store.BeginRemoteCreate(first.Handle, first.Tuple, first.RowRevision);
        var secondAttempt = store.BeginRemoteCreate(second.Handle, second.Tuple, second.RowRevision);

        store.AttachRemoteConversationId(first.Handle, first.Tuple, first.AttemptId, "shared-remote-id", firstAttempt.RowRevision);

        Assert.Throws<SqliteException>(() => store.AttachRemoteConversationId(
            second.Handle, second.Tuple, second.AttemptId, "shared-remote-id", secondAttempt.RowRevision));
        Assert.Null(store.ReadManagedConversation(second.Handle)!.RemoteConversationId);

        var third = store.ReserveManagedConversation(CaseTuple());
        Assert.Null(third.RemoteConversationId);
        Assert.NotEqual(first.Handle, third.Handle);
    }

    [Fact]
    public void Registration_UsesExistingManagedIdentitySqlConnection()
    {
        const string connectionString =
            "Server=tcp:case-state.database.windows.net,1433;Database=casestate;Encrypt=True;Authentication=Active Directory Managed Identity;";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CaseState"] = connectionString,
            })
            .Build();
        var services = new ServiceCollection();

        services.AddManagedConversationStore(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SqlManagedConversationStore>(provider.GetRequiredService<IManagedConversationStore>());

        const string malformedConnectionString = "Server=case-state;Database=casestate;User ID=app;";
        var malformedConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CaseState"] = malformedConnectionString,
            })
            .Build();
        var malformedServices = new ServiceCollection();
        malformedServices.AddManagedConversationStore(malformedConfiguration);
        using var malformedProvider = malformedServices.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() =>
            malformedProvider.GetRequiredService<IManagedConversationStore>());

        var unavailableServices = new ServiceCollection();
        unavailableServices.AddManagedConversationStore(new ConfigurationBuilder().Build());
        using var unavailableProvider = unavailableServices.BuildServiceProvider();
        var unavailableStore = unavailableProvider.GetRequiredService<IManagedConversationStore>();
        var unavailable = Assert.Throws<ManagedConversationStoreException>(() =>
            unavailableStore.ReserveManagedConversation(CaseTuple()));
        Assert.Equal("conversation_store_unavailable", unavailable.Code);
    }

    [Fact]
    public void Reservation_FailedInsertRollsBackWithoutMetadata()
    {
        using var fixture = new SqliteFixture();
        using (var db = fixture.NewContext())
        {
            db.Database.ExecuteSqlRaw(
                "CREATE TRIGGER reject_managed_reservation BEFORE INSERT ON managed_conversations " +
                "BEGIN SELECT RAISE(ABORT, 'managed reservation rejected'); END;");
        }

        Assert.Throws<DbUpdateException>(() => fixture.Store().ReserveManagedConversation(CaseTuple()));
        using var check = fixture.NewContext();
        Assert.Empty(check.ManagedConversations);
    }

    [Fact]
    public void Registration_InjectedClockDrivesSqlReservationAndAdmission()
    {
        using var fixture = new SqliteFixture();
        var services = new ServiceCollection();
        services.AddSingleton(fixture.Options);
        services.AddSingleton<TimeProvider>(fixture.Clock);
        services.AddManagedConversationStore(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var store = Assert.IsType<SqlManagedConversationStore>(
            provider.GetRequiredService<IManagedConversationStore>());
        var tuple = CaseTuple();
        new SqlCaseStateStore(fixture.Options).CreateCase(tuple.CaseId, tuple.UserId);
        var reserved = store.ReserveManagedConversation(tuple);
        Assert.Equal(fixture.Clock.GetUtcNow().UtcTicks, reserved.CreatedAtTicks);
        var creating = store.BeginRemoteCreate(reserved.Handle, tuple, reserved.RowRevision);
        var attached = store.AttachRemoteConversationId(
            creating.Handle, tuple, creating.AttemptId, "clock-remote-id", creating.RowRevision);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        var admission = store.AdmitManagedTurn(attached.Handle, tuple, attached.RowRevision, tuple.CaseRevision);

        Assert.Equal(fixture.Clock.GetUtcNow().UtcTicks + SqlManagedConversationStore.TurnDeadline.Ticks,
            admission.DeadlineTicks);
    }

    [Fact]
    public void Admission_RetiredBindingDistinguishesUnavailableFromChangedTupleAndOtherOwner()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var tuple = CaseTuple();
        var attached = Attach(fixture, tuple);
        Assert.True(store.RetireManagedConversation(attached.Handle, tuple, attached.RowRevision, null));
        var retired = store.ReadManagedConversation(attached.Handle)!;

        var unavailable = Assert.Throws<ManagedConversationStoreException>(() =>
            store.AdmitManagedTurn(retired.Handle, tuple, retired.RowRevision, tuple.CaseRevision));
        Assert.Equal("conversation_unavailable", unavailable.Code);
        var changed = Assert.Throws<ManagedConversationStoreException>(() =>
            store.AdmitManagedTurn(retired.Handle, tuple with { AgentVersion = "8" },
                retired.RowRevision, tuple.CaseRevision));
        Assert.Equal("conversation_context_mismatch", changed.Code);
        var otherOwner = Assert.Throws<ManagedConversationStoreException>(() =>
            store.AdmitManagedTurn(retired.Handle, tuple with { UserId = "demo-user-2" },
                retired.RowRevision, tuple.CaseRevision));
        Assert.Equal("conversation_not_found", otherOwner.Code);
        Assert.Equal(retired, store.ReadManagedConversation(retired.Handle));
    }

    [Fact]
    public void TupleChange_RetiresInsteadOfRewritingImmutableMetadata()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var tuple = CaseTuple();
        var reserved = store.ReserveManagedConversation(tuple);
        var changed = tuple with { Role = "investigator" };

        Assert.Throws<ManagedConversationStoreException>(() =>
            store.BeginRemoteCreate(reserved.Handle, changed, reserved.RowRevision));
        var after = store.ReadManagedConversation(reserved.Handle)!;
        Assert.True(after.Retired);
        Assert.Equal(tuple, after.Tuple);
        Assert.Null(after.RemoteConversationId);
    }

    [Fact]
    public void OtherUserCannotRetireOrDistinguishAStoredHandle()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var tuple = CaseTuple();
        var reserved = store.ReserveManagedConversation(tuple);
        var otherUser = tuple with { UserId = "demo-user-2" };

        var denied = Assert.Throws<ManagedConversationStoreException>(() =>
            store.BeginRemoteCreate(reserved.Handle, otherUser, reserved.RowRevision));
        Assert.Equal("conversation_not_found", denied.Code);
        var unchanged = store.ReadManagedConversation(reserved.Handle)!;
        Assert.False(unchanged.Retired);
        Assert.Equal(tuple, unchanged.Tuple);
    }

    [Fact]
    public void Admission_TwoStoreInstancesAdmitOneWriter()
    {
        using var fixture = new SqliteFixture();
        var first = fixture.Store();
        var second = fixture.Store();
        var attached = Attach(fixture, CaseTuple());
        using var barrier = new Barrier(2);

        var attempts = new[]
        {
            Task.Run(() => TryAdmit(first, attached, barrier)),
            Task.Run(() => TryAdmit(second, attached, barrier)),
        };
        Task.WaitAll(attempts);

        Assert.Equal(1, attempts.Count(x => x.Result.Admission is not null));
        Assert.Equal(1, attempts.Count(x => x.Result.ErrorCode == "conversation_busy"));
        var snapshot = first.ReadManagedConversation(attached.Handle)!;
        Assert.Equal(1, snapshot.AdmittedTurnCount);
        Assert.NotNull(snapshot.ActiveFence);
        Assert.Equal(snapshot.RowRevision, attempts.Single(x => x.Result.Admission is not null).Result.Admission!.RowRevision);
    }

    [Fact]
    public void Admission_CommitFailureLeavesTurnAndBusinessRevisionUnchanged()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var tuple = CaseTuple();
        var attached = Attach(fixture, tuple);
        var scope = new CaseScope(tuple.CaseId);
        using (var db = fixture.NewContext())
        {
            db.Database.ExecuteSqlRaw(
                "CREATE TRIGGER reject_managed_turn BEFORE UPDATE OF AdmittedTurnCount ON managed_conversations " +
                "BEGIN SELECT RAISE(ABORT, 'managed turn rejected'); END;");
        }

        Assert.Throws<SqliteException>(() =>
            store.AdmitManagedTurn(attached.Handle, tuple, attached.RowRevision, tuple.CaseRevision));

        var unchanged = store.ReadManagedConversation(attached.Handle)!;
        Assert.Equal(attached.Tuple, unchanged.Tuple);
        Assert.Equal(attached.RowRevision, unchanged.RowRevision);
        Assert.Equal(0, unchanged.AdmittedTurnCount);
        Assert.Null(unchanged.ActiveFence);
        Assert.Equal(0, new SqlCaseStateStore(fixture.Options).Read(scope).Revision);
    }

    [Fact]
    public void Admission_AllowsTurnTwentyAndRetiresOnAttemptTwentyOne()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var tuple = CaseTuple();
        var snapshot = Attach(fixture, tuple);

        for (var turn = 1; turn <= 20; turn++)
        {
            var admitted = store.AdmitManagedTurn(snapshot.Handle, tuple, snapshot.RowRevision, tuple.CaseRevision);
            Assert.Equal(turn, admitted.TurnNumber);
            Assert.True(store.CompleteManagedTurn(admitted, tuple, Completion()));
            snapshot = store.ReadManagedConversation(snapshot.Handle)!;
        }

        Assert.Equal(20, snapshot.AdmittedTurnCount);
        Assert.Throws<ManagedConversationStoreException>(() =>
            store.AdmitManagedTurn(snapshot.Handle, tuple, snapshot.RowRevision, tuple.CaseRevision));
        Assert.True(store.ReadManagedConversation(snapshot.Handle)!.Retired);
        Assert.Equal(20, store.ReadManagedConversation(snapshot.Handle)!.AdmittedTurnCount);
        using var db = fixture.NewContext();
        var stored = Assert.Single(db.ManagedConversations.AsNoTracking().ToList());
        Assert.Equal("response-1", stored.LastResponseId);
        Assert.Equal("[\"item-1\"]", stored.ResponseItemIdsJson);
        Assert.Equal("result-digest", stored.ResultDigest);
    }

    [Fact]
    public void Admission_AbsoluteExpiryAndDeadlineEqualityRetireWithoutRenewal()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var tuple = CaseTuple();
        var snapshot = Attach(fixture, tuple);

        var firstAdmission = store.AdmitManagedTurn(snapshot.Handle, tuple, snapshot.RowRevision, tuple.CaseRevision);
        fixture.Clock.Advance(SqlManagedConversationStore.TurnDeadline);
        Assert.Throws<ManagedConversationStoreException>(() =>
            store.AdmitManagedTurn(snapshot.Handle, tuple, firstAdmission.RowRevision, tuple.CaseRevision));
        Assert.True(store.ReadManagedConversation(snapshot.Handle)!.Retired);

        var second = Attach(fixture, tuple);
        var admitted = store.AdmitManagedTurn(second.Handle, tuple, second.RowRevision, tuple.CaseRevision);
        fixture.Clock.Advance(SqlManagedConversationStore.TurnDeadline);
        Assert.False(store.CompleteManagedTurn(admitted, tuple, Completion()));
        var expired = store.ReadManagedConversation(second.Handle)!;
        Assert.True(expired.Retired);
        Assert.Null(expired.ActiveFence);

        var third = Attach(fixture, tuple);
        fixture.Clock.Advance(SqlManagedConversationStore.ConversationLifetime - TimeSpan.FromTicks(1));
        var justBeforeExpiry = store.AdmitManagedTurn(third.Handle, tuple, third.RowRevision, tuple.CaseRevision);
        Assert.True(store.CompleteManagedTurn(justBeforeExpiry, tuple, Completion()));

        var fourth = Attach(fixture, tuple);
        fixture.Clock.Advance(SqlManagedConversationStore.ConversationLifetime);
        Assert.Throws<ManagedConversationStoreException>(() =>
            store.AdmitManagedTurn(fourth.Handle, tuple, fourth.RowRevision, tuple.CaseRevision));
        Assert.True(store.ReadManagedConversation(fourth.Handle)!.Retired);
    }

    [Fact]
    public void Completion_StaleFenceCannotCompleteOrRetireAnotherConversation()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var tuple = CaseTuple();
        var old = Attach(fixture, tuple);
        var admission = store.AdmitManagedTurn(old.Handle, tuple, old.RowRevision, tuple.CaseRevision);
        Assert.True(store.RetireManagedConversation(old.Handle, tuple, admission.RowRevision, admission.Fence));

        var fresh = Attach(fixture, tuple);
        var freshAdmission = store.AdmitManagedTurn(fresh.Handle, tuple, fresh.RowRevision, tuple.CaseRevision);
        Assert.False(store.CompleteManagedTurn(admission, tuple, Completion()));
        Assert.False(store.RetireManagedConversation(old.Handle, tuple, admission.RowRevision, admission.Fence));
        var freshAfterOldCompletion = store.ReadManagedConversation(fresh.Handle)!;
        Assert.Equal(freshAdmission.Fence, freshAfterOldCompletion.ActiveFence);
        Assert.False(freshAfterOldCompletion.Retired);
    }

    [Fact]
    public void ActiveFence_SurvivesStoreRestartAndExpiresClosed()
    {
        using var fixture = new SqliteFixture();
        var tuple = CaseTuple();
        var original = Attach(fixture, tuple);
        var admission = fixture.Store().AdmitManagedTurn(
            original.Handle, tuple, original.RowRevision, tuple.CaseRevision);
        var restartedStore = fixture.Store();

        Assert.Equal(admission.Fence, restartedStore.ReadManagedConversation(original.Handle)!.ActiveFence);
        var busy = Assert.Throws<ManagedConversationStoreException>(() =>
            restartedStore.AdmitManagedTurn(original.Handle, tuple, admission.RowRevision, tuple.CaseRevision));
        Assert.Equal("conversation_busy", busy.Code);

        fixture.Clock.Advance(SqlManagedConversationStore.TurnDeadline);
        var expired = Assert.Throws<ManagedConversationStoreException>(() =>
            restartedStore.AdmitManagedTurn(original.Handle, tuple, admission.RowRevision, tuple.CaseRevision));
        Assert.Equal("conversation_expired", expired.Code);
        Assert.True(restartedStore.ReadManagedConversation(original.Handle)!.Retired);
    }

    [Fact]
    public void Completion_RevokedTupleRetiresAndRejectsLateMetadata()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var tuple = CaseTuple();
        var attached = Attach(fixture, tuple);
        var admission = store.AdmitManagedTurn(attached.Handle, tuple, attached.RowRevision, tuple.CaseRevision);
        var revoked = tuple with { EntitlementFingerprint = "revoked-entitlements" };

        Assert.Throws<ManagedConversationStoreException>(() =>
            store.CompleteManagedTurn(admission, revoked, Completion()));
        var after = store.ReadManagedConversation(attached.Handle)!;
        Assert.True(after.Retired);
        Assert.Equal(tuple, after.Tuple);
        using var check = fixture.NewContext();
        Assert.Null(Assert.Single(check.ManagedConversations.AsNoTracking().ToList()).LastResponseId);
    }

    [Fact]
    public void LostCreateReply_RetainsAttemptAndRecoveryOnlyAttachesToRetiredTombstone()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var tuple = CaseTuple();
        var reserved = store.ReserveManagedConversation(tuple);
        var attempted = store.BeginRemoteCreate(reserved.Handle, tuple, reserved.RowRevision);

        Assert.True(store.RetireManagedConversation(
            reserved.Handle, tuple, attempted.RowRevision, expectedFence: null));
        var retired = store.ReadManagedConversation(reserved.Handle)!;
        Assert.True(retired.Retired);
        Assert.Null(retired.RemoteConversationId);
        Assert.Equal(reserved.AttemptId, retired.AttemptId);

        var recovered = store.AttachRecoveredRemoteConversationId(
            retired.Handle, tuple, retired.AttemptId, "recovered-remote-id", retired.RowRevision);
        Assert.True(recovered.Retired);
        Assert.Equal("recovered-remote-id", recovered.RemoteConversationId);
        Assert.Throws<ManagedConversationStoreException>(() =>
            store.AdmitManagedTurn(recovered.Handle, tuple, recovered.RowRevision, tuple.CaseRevision));
        Assert.Throws<ManagedConversationStoreException>(() =>
            store.AttachRecoveredRemoteConversationId(
                recovered.Handle, tuple, recovered.AttemptId, "another-id", recovered.RowRevision));
    }

    [Fact]
    public void CompletionFailure_RetiresFenceAndSuppressesCompletionMetadata()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var tuple = CaseTuple();
        var attached = Attach(fixture, tuple);
        var admission = store.AdmitManagedTurn(attached.Handle, tuple, attached.RowRevision, tuple.CaseRevision);
        using (var db = fixture.NewContext())
        {
            db.Database.ExecuteSqlRaw(
                "CREATE TRIGGER reject_managed_completion BEFORE UPDATE OF LastResponseId ON managed_conversations " +
                "BEGIN SELECT RAISE(ABORT, 'managed completion rejected'); END;");
        }

        Assert.False(store.CompleteManagedTurn(admission, tuple, Completion()));
        var after = store.ReadManagedConversation(attached.Handle)!;
        Assert.True(after.Retired);
        Assert.Null(after.ActiveFence);
        using var check = fixture.NewContext();
        var row = check.ManagedConversations.AsNoTracking().Single();
        Assert.Null(row.LastResponseId);
        Assert.Equal("[]", row.ResponseItemIdsJson);
    }

    [Fact]
    public void Admission_ListingSkipsMissingCaseAndRetiresAfterOneTurn()
    {
        using var fixture = new SqliteFixture();
        var store = fixture.Store();
        var tuple = CaseTuple() with
        {
            CaseId = SqlManagedConversationStore.ListingCaseSentinel,
            CaseRevision = 0,
            IsListing = true,
        };
        var snapshot = Attach(fixture, tuple);
        var admission = store.AdmitManagedTurn(snapshot.Handle, tuple, snapshot.RowRevision, 0);

        Assert.True(store.CompleteManagedTurn(admission, tuple, Completion()));
        Assert.True(store.ReadManagedConversation(snapshot.Handle)!.Retired);
    }

    [Fact]
    public void Admission_ChangedCaseRevisionRetiresTheOldTuple()
    {
        using var fixture = new SqliteFixture();
        var tuple = CaseTuple();
        var store = fixture.Store();
        var snapshot = Attach(fixture, tuple);
        using (var db = fixture.NewContext())
        {
            db.Cases.Where(x => x.CaseId == tuple.CaseId)
                .ExecuteUpdate(update => update.SetProperty(x => x.Revision, 1));
        }

        Assert.Throws<ManagedConversationStoreException>(() =>
            store.AdmitManagedTurn(snapshot.Handle, tuple, snapshot.RowRevision, tuple.CaseRevision));
        Assert.True(store.ReadManagedConversation(snapshot.Handle)!.Retired);
    }

    private static ManagedConversationSnapshot Attach(SqliteFixture fixture, ManagedConversationTuple tuple)
    {
        var store = fixture.Store();
        if (!tuple.IsListing && new SqlCaseStateStore(fixture.Options).OwnerOf(tuple.CaseId) is null)
        {
            new SqlCaseStateStore(fixture.Options).CreateCase(tuple.CaseId, tuple.UserId);
        }

        var reserved = store.ReserveManagedConversation(tuple);
        var attempt = store.BeginRemoteCreate(reserved.Handle, tuple, reserved.RowRevision);
        return store.AttachRemoteConversationId(
            reserved.Handle, tuple, reserved.AttemptId, $"remote-{Guid.NewGuid():N}", attempt.RowRevision);
    }

    private static (ManagedTurnAdmission? Admission, string? ErrorCode) TryAdmit(
        SqlManagedConversationStore store, ManagedConversationSnapshot snapshot, Barrier barrier)
    {
        barrier.SignalAndWait();
        try
        {
            return (store.AdmitManagedTurn(
                snapshot.Handle, snapshot.Tuple, snapshot.RowRevision, snapshot.Tuple.CaseRevision), null);
        }
        catch (ManagedConversationStoreException exception)
        {
            return (null, exception.Code);
        }
    }

    private static ManagedConversationTuple CaseTuple() =>
        new("demo-user-1", "presenter", "case-one", "entitlements-digest", 0,
            "case-agent", "7", "configuration-digest");

    private static ManagedTurnCompletionMetadata Completion() =>
        new("response-1", ["item-1"], "result-digest");

    internal sealed class SqliteFixture : IDisposable
    {
        private readonly SqliteConnection _anchor;
        private readonly string _connectionString;

        public SqliteFixture()
        {
            _connectionString = $"Data Source=managed-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _anchor = new SqliteConnection(_connectionString);
            _anchor.Open();
            Options = new DbContextOptionsBuilder<CaseStateDbContext>()
                .UseSqlite(_connectionString)
                .Options;
            using var db = NewContext();
            db.Database.Migrate();
            Clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        }

        public DbContextOptions<CaseStateDbContext> Options { get; }
        public AdjustableTimeProvider Clock { get; }

        public SqlManagedConversationStore Store() => new(Options, Clock);

        public CaseStateDbContext NewContext() => new(Options);

        public void Dispose() => _anchor.Dispose();
    }

    internal sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
