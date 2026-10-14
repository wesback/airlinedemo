using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        Assert.Contains("AddSingleton<ICaseStateStore>(_ => CreateCaseStateStore(config));", program);
        Assert.Contains("\"File\" => new CaseStateStore(", program);
        Assert.Contains("\"Sql\" => new SqlCaseStateStore(", program);
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

    // AC3: the SQL adapter, run against the migrated schema on an in-memory SQLite connection (no Azure SQL).
    [Fact]
    public void AC3_case_creation_and_ownership_checks_use_sql()
    {
        using var connection = OpenMigratedConnection(out var options);
        var store = new SqlCaseStateStore(options);

        var created = store.CreateCase("case-1", "alice");
        Assert.Equal("case-1", created.CaseId);
        Assert.Equal("alice", store.OwnerOf("case-1"));
        Assert.Null(store.OwnerOf("missing"));
        Assert.Equal("case-1", store.AuthorizeCase("case-1", "alice").CaseId);

        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-1", "bob"));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("missing", "alice"));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("CASE-1", "alice"));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("", "alice"));
        Assert.Throws<InvalidOperationException>(() => store.CreateCase("case-1", "bob"));
        Assert.Throws<ArgumentException>(() => store.CreateCase(" ", "alice"));
        Assert.Throws<ArgumentException>(() => store.CreateCase("case-2", " "));

        var state = store.Read(created);
        Assert.Equal("alice", state.Owner);
        Assert.Equal(0L, state.Revision);
        Assert.Empty(state.Basis);
        Assert.Empty(state.Audit);
    }

    // AC3: basis versions, commits, and audit reads round-trip through the SQL adapter.
    [Fact]
    public void AC3_basis_reads_commits_and_audit_reads_round_trip_through_sql()
    {
        using var connection = OpenMigratedConnection(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "alice");

        var v1 = Basis(1, "page one", "");
        var first = store.Commit(scope, 0, new CaseCommit(v1, "receipt-1", "reviewer", "basis.add", "first basis"));
        Assert.False(first.Replayed);
        Assert.Equal(1L, first.State.Revision);
        Assert.Equal(["receipt-1"], first.State.ReceiptIds);
        Assert.Equal("first basis", first.State.Audit.Single().Detail);

        var v2 = Basis(2, "page two", v1.BasisDigest);
        var second = store.Commit(scope, 1, new CaseCommit(v2, null, "reviewer", "basis.add", "second basis"));
        Assert.Equal(2L, second.State.Revision);

        Assert.Equal("page one", store.GetBasisVersion(scope, 1).Content);
        Assert.Equal(v2.BasisDigest, store.GetBasisVersion(scope, 2).BasisDigest);
        Assert.Throws<KeyNotFoundException>(() => store.GetBasisVersion(scope, 3));

        var audit = store.ReadAudit(scope);
        Assert.Equal(2, audit.Count);
        Assert.Equal([1L, 2L], audit.Select(e => e.Sequence));
        Assert.Equal([1L, 2L], audit.Select(e => e.Revision));
        Assert.Equal(["first basis", "second basis"], audit.Select(e => e.Detail));
    }

    // AC4: stale revisions, invalid basis digests, and receipt conflicts leave persisted state unchanged.
    [Fact]
    public void AC4_failed_commits_leave_persisted_state_unchanged()
    {
        using var connection = OpenMigratedConnection(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "alice");
        var v1 = Basis(1, "page one", "");
        store.Commit(scope, 0, new CaseCommit(v1, "receipt-1", "reviewer", "basis.add", "first"));
        var before = Snapshot(store, scope);

        Assert.Throws<RevisionConflictException>(() =>
            store.Commit(scope, 0, new CaseCommit(Basis(2, "stale", v1.BasisDigest), "receipt-2", "reviewer", "basis.add", "stale")));
        Assert.Equal(before, Snapshot(store, scope));

        var tamperedContent = new BasisVersion(2, "page two", Hex("other content"), Hex("|" + Hex("other content")), DateTimeOffset.UnixEpoch);
        Assert.Throws<BasisIntegrityException>(() =>
            store.Commit(scope, 1, new CaseCommit(tamperedContent, "receipt-3", "reviewer", "basis.add", "tampered")));
        Assert.Equal(before, Snapshot(store, scope));

        var wrongChain = Basis(2, "page two", "not-the-previous-digest");
        Assert.Throws<BasisIntegrityException>(() =>
            store.Commit(scope, 1, new CaseCommit(wrongChain, "receipt-4", "reviewer", "basis.add", "wrong chain")));
        Assert.Equal(before, Snapshot(store, scope));

        var skippedVersion = Basis(3, "page three", v1.BasisDigest);
        Assert.Throws<BasisIntegrityException>(() =>
            store.Commit(scope, 1, new CaseCommit(skippedVersion, "receipt-5", "reviewer", "basis.add", "skipped")));
        Assert.Equal(before, Snapshot(store, scope));

        Assert.Throws<ReceiptConflictException>(() =>
            store.Commit(scope, 1, new CaseCommit(null, "receipt-1", "reviewer", "basis.add", "different detail")));
        Assert.Equal(before, Snapshot(store, scope));
    }

    // AC4: an exact receipt replay returns the existing state without a new revision or audit event.
    [Fact]
    public void AC4_exact_receipt_replay_adds_no_revision_or_audit_event()
    {
        using var connection = OpenMigratedConnection(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "alice");
        var commit = new CaseCommit(Basis(1, "page one", ""), "receipt-1", "reviewer", "basis.add", "first");

        var first = store.Commit(scope, 0, commit);
        var before = Snapshot(store, scope);

        var replay = store.Commit(scope, 0, commit with { });
        Assert.True(replay.Replayed);
        Assert.Equal(first.State.Revision, replay.State.Revision);
        Assert.Single(store.ReadAudit(scope));
        Assert.Equal(before, Snapshot(store, scope));
    }

    private static SqliteConnection OpenMigratedConnection(out DbContextOptions<CaseStateDbContext> options)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(connection).Options;
        // The migration's identity columns are SQL Server annotations, so SQLite would not auto-increment the
        // decision and document keys. EnsureCreated builds the same tables from the model with SQLite identities;
        // AC2 separately verifies the migration itself.
        using var db = new CaseStateDbContext(options);
        db.Database.EnsureCreated();
        return connection;
    }

    private static BasisVersion Basis(int version, string content, string previousDigest)
    {
        var contentDigest = Hex(content);
        return new BasisVersion(version, content, contentDigest, Hex(previousDigest + "|" + contentDigest), DateTimeOffset.UnixEpoch.AddDays(version));
    }

    private static string Snapshot(ICaseStateStore store, CaseScope scope) =>
        JsonSerializer.Serialize(store.Read(scope));

    private static string Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void AssertMapped(CaseStateDbContext db, Type entity, string schema, string table)
    {
        var type = db.Model.FindEntityType(entity);
        Assert.NotNull(type);
        Assert.Equal(schema, type.GetSchema());
        Assert.Equal(table, type.GetTableName());
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
