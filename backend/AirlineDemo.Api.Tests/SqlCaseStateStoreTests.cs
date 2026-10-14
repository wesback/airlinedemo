using System.Reflection;
using System.Runtime.CompilerServices;
using AirlineDemo.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Persistence.Migrations;
using AirlineDemo.Api.Reviews;
using AirlineDemo.Api.Runs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace AirlineDemo.Api.Tests;

public sealed class SqlCaseStateStoreTests
{
    private static readonly string[] InterfaceMethods =
    [
        "AuthorizeCase", "OwnerOf", "CreateCase", "Read", "GetBasisVersion", "ReadAudit", "Commit",
    ];

    // AC1: interface contract and file-backed registration; no database connection.
    [Fact]
    public void AC1_interface_contract_and_file_registration()
    {
        var methods = typeof(ICaseStateStore).GetMethods().Select(m => m.Name).OrderBy(n => n).ToArray();
        Assert.Equal(InterfaceMethods.OrderBy(n => n), methods);

        Assert.True(typeof(ICaseStateStore).IsAssignableFrom(typeof(CaseStateStore)));
        var map = typeof(CaseStateStore).GetInterfaceMap(typeof(ICaseStateStore));
        Assert.All(map.TargetMethods, m => Assert.Equal(typeof(CaseStateStore), m.DeclaringType));

        Assert.Equal(typeof(ICaseStateStore), typeof(DemoRunReset).GetConstructors().Single().GetParameters().Single().ParameterType);
        Assert.Equal(typeof(ICaseStateStore), typeof(TechnicalReviewPolicy).GetMethod(nameof(TechnicalReviewPolicy.ScopeFor))!.GetParameters()[0].ParameterType);

        var program = File.ReadAllText(Path.Combine(SourceDirectory(), "..", "AirlineDemo.Api", "Program.cs"));
        Assert.Contains("AddCaseStateStore(builder.Configuration)", program);
    }

    // AC2: EF model maps the four tables; the versioned migration applies to an in-memory SQLite connection.
    [Fact]
    public void AC2_schema_mapping_and_migration()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(connection).Options;
        using var db = new CaseStateDbContext(options);
        db.Database.Migrate();

        Assert.Contains("20261014190000_InitialCaseStateSchema", db.Database.GetAppliedMigrations());
        AssertMapped(db, typeof(CaseRow), "dbo", "cases");
        AssertMapped(db, typeof(CaseDocumentRow), "dbo", "case_documents");
        AssertMapped(db, typeof(CaseDecisionRow), "dbo", "case_decisions");
        AssertMapped(db, typeof(CaseEventRow), "audit", "case_events");

        foreach (var table in new[] { "cases", "case_documents", "case_decisions", "case_events" })
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            check.Parameters.AddWithValue("$name", table);
            Assert.Equal(1L, (long)check.ExecuteScalar()!);
        }

        // SQLite ignores schemas, so check the migration's own operations for the schema of each table.
        var operations = new InitialCaseStateSchema().UpOperations.ToList();
        var created = operations.OfType<CreateTableOperation>().ToDictionary(o => o.Name, o => o.Schema);
        Assert.Equal("dbo", created["cases"]);
        Assert.Equal("dbo", created["case_documents"]);
        Assert.Equal("dbo", created["case_decisions"]);
        Assert.Equal("audit", created["case_events"]);

        var ensured = operations.OfType<EnsureSchemaOperation>().Select(o => o.Name).ToArray();
        Assert.Contains("audit", ensured);
        var auditIndex = operations.FindIndex(o => o is EnsureSchemaOperation e && e.Name == "audit");
        var eventsIndex = operations.FindIndex(o => o is CreateTableOperation c && c.Name == "case_events");
        Assert.True(auditIndex >= 0 && auditIndex < eventsIndex, "audit schema must be created before audit.case_events");
    }

    // AC1: with ConnectionStrings:CaseState set, the registered store is the SQL adapter; no store call or connection is made.
    [Fact]
    public void AC1_runtime_registration_uses_managed_identity_sql_store()
    {
        const string connectionString =
            "Server=tcp:case-state.database.windows.net,1433;Database=casestate;Encrypt=True;Authentication=Active Directory Managed Identity;";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CaseState"] = connectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddCaseStateStore(configuration);
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ICaseStateStore>();

        Assert.IsType<SqlCaseStateStore>(store);
        CaseStateStoreRegistration.ValidateManagedIdentityConnectionString(connectionString);
        Assert.Throws<InvalidOperationException>(() =>
            CaseStateStoreRegistration.ValidateManagedIdentityConnectionString(connectionString + "User ID=app;"));
        Assert.Throws<InvalidOperationException>(() =>
            CaseStateStoreRegistration.ValidateManagedIdentityConnectionString(connectionString + "UID=app;"));
        Assert.Throws<InvalidOperationException>(() =>
            CaseStateStoreRegistration.ValidateManagedIdentityConnectionString(connectionString + "User=app;"));
        Assert.Throws<InvalidOperationException>(() =>
            CaseStateStoreRegistration.ValidateManagedIdentityConnectionString(connectionString + "Password=secret;"));
        Assert.Throws<InvalidOperationException>(() =>
            CaseStateStoreRegistration.ValidateManagedIdentityConnectionString(connectionString + "Access Token=token;"));
        Assert.Throws<InvalidOperationException>(() =>
            CaseStateStoreRegistration.ValidateManagedIdentityConnectionString(connectionString + "PWD=secret;"));
        Assert.Throws<InvalidOperationException>(() =>
            CaseStateStoreRegistration.ValidateManagedIdentityConnectionString("Server=x;Database=y;Encrypt=True;"));

        var forbidden = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CaseState"] = connectionString + "Password=secret;" })
            .Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCaseStateStore(forbidden));

        foreach (var suffix in new[] { "User ID=app;", "UID=app;", "Password=secret;", "PWD=secret;", "Access Token=token;" })
        {
            var configured = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CaseState"] = connectionString + suffix })
                .Build();
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCaseStateStore(configured));
        }

        // A repeated Authentication key, in either order, must be rejected.
        foreach (var duplicate in new[] { "Authentication=Active Directory Default;", "Authentication=Active Directory Managed Identity;" })
        {
            var configured = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CaseState"] = connectionString + duplicate })
                .Build();
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCaseStateStore(configured));
        }
        Assert.Throws<InvalidOperationException>(() =>
            CaseStateStoreRegistration.ValidateManagedIdentityConnectionString("Authentication=Active Directory Default;" + connectionString));

        var program = File.ReadAllText(Path.Combine(SourceDirectory(), "..", "AirlineDemo.Api", "Program.cs"));
        Assert.Contains("AddCaseStateStore(builder.Configuration)", program);
    }

    // AC2: the adapter maps the four tables and exposes no audit update or delete; a successful commit appends one audit event.
    [Fact]
    public void AC2_adapter_uses_grant_compatible_tables_and_append_only_audit()
    {
        using var connection = OpenMigratedSqlite(out var options);
        using (var db = new CaseStateDbContext(options))
        {
            AssertMapped(db, typeof(CaseRow), "dbo", "cases");
            AssertMapped(db, typeof(CaseDocumentRow), "dbo", "case_documents");
            AssertMapped(db, typeof(CaseDecisionRow), "dbo", "case_decisions");
            AssertMapped(db, typeof(CaseEventRow), "audit", "case_events");
        }

        var publicMethods = typeof(SqlCaseStateStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToArray();
        Assert.Equal(InterfaceMethods.OrderBy(n => n), publicMethods.OrderBy(n => n));

        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "owner-a");
        Assert.Equal(0, CountAuditEvents(options));

        store.Commit(scope, 0, new CaseCommit(null, null, "owner-a", "open", "opened"));
        Assert.Equal(1, CountAuditEvents(options));
        Assert.Equal(1L, store.Read(scope).Revision);
    }

    // AC3: migrate, commit basis and receipt, restart the context and store, and reload each record exactly once.
    [Fact]
    public void AC3_migration_and_restart_round_trip()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var basis = CaseStateStore.CreateBasisVersion(1, "Aircraft return readiness basis", string.Empty);

        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "owner-a");
        var committed = store.Commit(scope, 0, new CaseCommit(basis, "receipt-1", "owner-a", "decide", "approved"));
        Assert.False(committed.Replayed);

        // Restart: a new store over a new context on the same open in-memory connection.
        var reloaded = new SqlCaseStateStore(options);
        var state = reloaded.Read(reloaded.AuthorizeCase("case-1", "owner-a"));

        Assert.Equal("owner-a", state.Owner);
        Assert.Equal(1L, state.Revision);
        var stored = Assert.Single(state.Basis);
        Assert.Equal(1, stored.Version);
        Assert.Equal(basis.Content, stored.Content);
        Assert.Equal(basis.ContentDigest, stored.ContentDigest);
        Assert.Equal(basis.BasisDigest, stored.BasisDigest);
        Assert.Equal(new[] { "receipt-1" }, state.ReceiptIds);
        Assert.Equal(committed.State.ReceiptFingerprints["receipt-1"], state.ReceiptFingerprints["receipt-1"]);
        var audit = Assert.Single(state.Audit);
        Assert.Equal(1L, audit.Sequence);
        Assert.Equal(1L, audit.Revision);
        Assert.Equal("decide", audit.Action);

        using var db = new CaseStateDbContext(options);
        Assert.Contains("20261014190000_InitialCaseStateSchema", db.Database.GetAppliedMigrations());
        Assert.Equal(1, db.Cases.Count());
        Assert.Equal(1, db.CaseDocuments.Count());
        Assert.Equal(1, db.CaseDecisions.Count());
        Assert.Equal(1, db.CaseEvents.Count());
    }

    // AC4: ordinal owner matching, scope isolation, duplicate IDs, and stale-revision rejection, each with a positive case.
    [Fact]
    public void AC4_scope_and_stale_revision_behavior()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope1 = store.CreateCase("case-1", "owner-a");
        var scope2 = store.CreateCase("case-2", "owner-b");
        var basis1 = CaseStateStore.CreateBasisVersion(1, "basis for case one", string.Empty);
        var basis2 = CaseStateStore.CreateBasisVersion(1, "basis for case two", string.Empty);
        store.Commit(scope1, 0, new CaseCommit(basis1, null, "owner-a", "open", "case one"));
        store.Commit(scope2, 0, new CaseCommit(basis2, null, "owner-b", "open", "case two"));
        var receiptCommit = store.Commit(scope2, 1, new CaseCommit(null, "receipt-2", "owner-b", "review", "case two again"));

        Assert.Equal("case-1", store.AuthorizeCase("case-1", "owner-a").CaseId);
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-1", "OWNER-A"));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-1", "owner-b"));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("missing", "owner-a"));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-1", string.Empty));

        Assert.Throws<InvalidOperationException>(() => store.CreateCase("case-1", "owner-c"));
        Assert.Equal("owner-a", store.OwnerOf("case-1"));

        var state1 = store.Read(scope1);
        Assert.Equal("case-1", state1.CaseId);
        Assert.Equal("owner-a", state1.Owner);
        Assert.Equal(1L, state1.Revision);
        Assert.Empty(state1.ReceiptIds);
        Assert.Empty(state1.ReceiptFingerprints);
        Assert.Equal(new[] { "case one" }, state1.Audit.Select(a => a.Detail));

        var state2 = store.Read(scope2);
        Assert.Equal("case-2", state2.CaseId);
        Assert.Equal("owner-b", state2.Owner);
        Assert.Equal(2L, state2.Revision);
        Assert.Equal(new[] { "receipt-2" }, state2.ReceiptIds);
        Assert.Equal(new[] { "receipt-2" }, state2.ReceiptFingerprints.Keys.ToArray());
        Assert.Equal(receiptCommit.State.ReceiptFingerprints["receipt-2"], state2.ReceiptFingerprints["receipt-2"]);
        Assert.Equal(new[] { "case two", "case two again" }, state2.Audit.Select(a => a.Detail));
        Assert.Equal(new[] { "case two", "case two again" }, store.ReadAudit(scope2).Select(a => a.Detail));
        Assert.Equal(new[] { "case one" }, store.ReadAudit(scope1).Select(a => a.Detail));

        Assert.Equal(new[] { "basis for case one" }, store.Read(scope1).Basis.Select(b => b.Content));
        Assert.Equal(new[] { "basis for case two" }, store.Read(scope2).Basis.Select(b => b.Content));
        Assert.Equal("basis for case one", store.GetBasisVersion(scope1, 1).Content);
        Assert.Equal("basis for case two", store.GetBasisVersion(scope2, 1).Content);
        Assert.Throws<KeyNotFoundException>(() => store.GetBasisVersion(scope1, 2));
        Assert.Throws<KeyNotFoundException>(() => store.GetBasisVersion(scope2, 2));

        Assert.Throws<RevisionConflictException>(() =>
            store.Commit(scope1, 0, new CaseCommit(null, null, "owner-a", "stale", "should fail")));
        var after = store.Read(scope1);
        Assert.Equal(1L, after.Revision);
        Assert.Single(after.Audit);
        Assert.Equal(3, CountAuditEvents(options));

        Assert.Equal(2L, store.Commit(scope1, 1, new CaseCommit(null, null, "owner-a", "close", "ok")).State.Revision);
    }

    [Fact]
    public void AC2_stale_invalid_basis_and_receipt_conflicts_leave_state_unchanged()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-r", "owner-r");
        var basis = CaseStateStore.CreateBasisVersion(1, "basis r", string.Empty);
        store.Commit(scope, 0, new CaseCommit(basis, "receipt-r", "owner-r", "open", "first"));

        var before = store.Read(scope);
        var auditBefore = CountAuditEvents(options);
        var decisionsBefore = CaptureDecisions(options, before.CaseId);
        Assert.NotEmpty(decisionsBefore);

        // Exact receipt replay: no new revision, no new audit event.
        var replay = store.Commit(scope, 0, new CaseCommit(basis, "receipt-r", "owner-r", "open", "first"));
        Assert.True(replay.Replayed);
        AssertStateUnchanged(store, options, scope, before, auditBefore, decisionsBefore);

        // Same receipt ID with a different payload is a conflict.
        Assert.Throws<ReceiptConflictException>(() =>
            store.Commit(scope, before.Revision, new CaseCommit(null, "receipt-r", "owner-r", "close", "changed")));
        AssertStateUnchanged(store, options, scope, before, auditBefore, decisionsBefore);

        // Stale expected revision.
        Assert.Throws<RevisionConflictException>(() =>
            store.Commit(scope, 0, new CaseCommit(null, null, "owner-r", "stale", "stale")));
        AssertStateUnchanged(store, options, scope, before, auditBefore, decisionsBefore);

        // Invalid basis: wrong content digest, then wrong chain digest.
        var next = CaseStateStore.CreateBasisVersion(2, "basis r2", basis.BasisDigest);
        var badContent = next with { ContentDigest = "bad" };
        Assert.Throws<BasisIntegrityException>(() =>
            store.Commit(scope, before.Revision, new CaseCommit(badContent, null, "owner-r", "bad", "bad content")));
        AssertStateUnchanged(store, options, scope, before, auditBefore, decisionsBefore);

        var badChain = next with { BasisDigest = "bad" };
        Assert.Throws<BasisIntegrityException>(() =>
            store.Commit(scope, before.Revision, new CaseCommit(badChain, null, "owner-r", "bad", "bad chain")));
        AssertStateUnchanged(store, options, scope, before, auditBefore, decisionsBefore);
    }

    private static void AssertStateUnchanged(
        SqlCaseStateStore store,
        DbContextOptions<CaseStateDbContext> options,
        CaseScope scope,
        CaseState before,
        int auditBefore,
        IReadOnlyList<(string ReceiptId, string Fingerprint)> decisionsBefore)
    {
        var after = store.Read(scope);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Basis.Select(b => b.BasisDigest), after.Basis.Select(b => b.BasisDigest));
        Assert.Equal(before.ReceiptIds, after.ReceiptIds);
        Assert.Equal(before.Audit.Select(a => a.Detail), after.Audit.Select(a => a.Detail));
        Assert.Equal(auditBefore, CountAuditEvents(options));
        Assert.Equal(decisionsBefore, CaptureDecisions(options, before.CaseId));
    }

    private static IReadOnlyList<(string ReceiptId, string Fingerprint)> CaptureDecisions(
        DbContextOptions<CaseStateDbContext> options,
        string caseId)
    {
        using var db = new CaseStateDbContext(options);
        return db.CaseDecisions
            .Where(d => d.CaseId == caseId)
            .OrderBy(d => d.Id)
            .Select(d => new ValueTuple<string, string>(d.ReceiptId, d.Fingerprint))
            .ToList();
    }

    private static SqliteConnection OpenMigratedSqlite(out DbContextOptions<CaseStateDbContext> options)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(connection).Options;
        using var db = new CaseStateDbContext(options);
        db.Database.Migrate();
        return connection;
    }

    private static int CountAuditEvents(DbContextOptions<CaseStateDbContext> options)
    {
        using var db = new CaseStateDbContext(options);
        return db.CaseEvents.Count();
    }

    private static void AssertMapped(CaseStateDbContext db, Type entity, string schema, string table)
    {
        var type = db.Model.FindEntityType(entity);
        Assert.NotNull(type);
        Assert.Equal(schema, type.GetSchema());
        Assert.Equal(table, type.GetTableName());
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
