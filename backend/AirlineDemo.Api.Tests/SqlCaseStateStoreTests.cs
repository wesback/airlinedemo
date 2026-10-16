using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using AirlineDemo.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Persistence.Migrations;
using AirlineDemo.Api.Reviews;
using AirlineDemo.Api.Runs;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Requests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace AirlineDemo.Api.Tests;

public sealed class SqlCaseStateStoreTests
{
    private static readonly string[] InterfaceMethods =
    [
        "AuthorizeCase", "OwnerOf", "CreateCase", "Read", "GetBasisVersion", "ReadAudit", "Commit",
        "ListWorkItems", "CreateWorkItem", "ChangeWorkItemState", "AppendTrace", "FindTrace",
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
        Assert.Contains("20261016120000_AddAssessmentHistory", db.Database.GetAppliedMigrations());
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
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Persistence:CaseStateProvider"] = "Sql", ["ConnectionStrings:CaseState"] = connectionString })
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
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Persistence:CaseStateProvider"] = "Sql", ["ConnectionStrings:CaseState"] = connectionString + "Password=secret;" })
            .Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCaseStateStore(forbidden));

        foreach (var suffix in new[] { "User ID=app;", "UID=app;", "Password=secret;", "PWD=secret;", "Access Token=token;" })
        {
            var configured = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Persistence:CaseStateProvider"] = "Sql", ["ConnectionStrings:CaseState"] = connectionString + suffix })
                .Build();
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCaseStateStore(configured));
        }

        // A repeated Authentication key, in either order, must be rejected.
        foreach (var duplicate in new[] { "Authentication=Active Directory Default;", "Authentication=Active Directory Managed Identity;" })
        {
            var configured = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Persistence:CaseStateProvider"] = "Sql", ["ConnectionStrings:CaseState"] = connectionString + duplicate })
                .Build();
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCaseStateStore(configured));
        }
        Assert.Throws<InvalidOperationException>(() =>
            CaseStateStoreRegistration.ValidateManagedIdentityConnectionString("Authentication=Active Directory Default;" + connectionString));

        var program = File.ReadAllText(Path.Combine(SourceDirectory(), "..", "AirlineDemo.Api", "Program.cs"));
        Assert.Contains("AddCaseStateStore(builder.Configuration)", program);
    }

    // AC1: commits and audit reads issue only SELECT and INSERT against case_events; no generated command updates or deletes it.
    [Fact]
    public void AC1_generated_audit_commands_are_append_only()
    {
        using var connection = OpenMigratedSqlite(out _);
        var recorder = new CommandRecorder();
        var recordingOptions = new DbContextOptionsBuilder<CaseStateDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(recorder)
            .Options;

        var store = new SqlCaseStateStore(recordingOptions);
        var scope = store.CreateCase("case-1", "owner-a");
        recorder.Commands.Clear();

        store.Commit(scope, 0, new CaseCommit(null, null, "owner-a", "open", "opened"));
        Assert.Single(store.ReadAudit(scope));

        var auditCommands = recorder.Commands.Where(c => c.Contains("case_events", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(auditCommands, c => LeadingKeyword(c) == "SELECT");
        Assert.Contains(auditCommands, c => LeadingKeyword(c) == "INSERT");
        Assert.All(auditCommands, c => Assert.Contains(LeadingKeyword(c), new[] { "SELECT", "INSERT" }));
        Assert.DoesNotContain(recorder.Commands, c =>
            c.Contains("case_events", StringComparison.OrdinalIgnoreCase)
            && (LeadingKeyword(c) is "UPDATE" or "DELETE" or "REPLACE" or "UPSERT"));
    }

    private static string LeadingKeyword(string commandText) =>
        System.Text.RegularExpressions.Regex.Match(commandText, @"^\s*(\w+)").Groups[1].Value.ToUpperInvariant();

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

    [Fact]
    public void AC_AssessmentHistorySchemaRejectsMissingBasisReference()
    {
        using var connection = OpenMigratedSqlite(out _);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO case_assessments (CaseId, AssessmentId, BasisVersion, Payload) VALUES ('case-1', 'a-1', 1, '{}')";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        command.CommandText =
            "INSERT INTO case_assessments (CaseId, AssessmentId, BasisId, Payload) VALUES ('case-1', 'a-2', 'basis-1', '{}')";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        command.CommandText =
            "INSERT INTO case_assessments (CaseId, AssessmentId, BasisId, BasisVersion, Payload) VALUES ('case-1', 'a-3', ' ', 1, '{}')";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        command.CommandText =
            "INSERT INTO case_assessments (CaseId, AssessmentId, BasisId, BasisVersion, Payload) VALUES ('case-1', 'a-4', 'basis-1', 0, '{}')";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void AC_AssessmentAndReceiptCommitRoundTripsWithinCaseScope()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-a", "owner-a");
        store.CreateCase("case-b", "owner-b");
        var assessment = new AssessmentRecord(
            "case-a", "basis-a", 7, "Request", "supported gap",
            [new AssessedClaim("MissingRequirement", "approval", "Engine", "ENG-7", [new AssessedWitness("doc-1", 3)])],
            "REQ-7", null, "assessment-7");
        var receipt = new DeliveryReceipt("event-7", "REQ-7", "case-a", "report-7", "basis-a", 7, "template-4", "body-7");
        store.Commit(scope, 0, new CaseCommit(
            null, receipt.RequestId, "assessment-service", "assessment", "delivered",
            Assessment: assessment, RequestReceipt: receipt));

        var reloaded = new SqlCaseStateStore(options);
        var restoredScope = reloaded.AuthorizeCase("case-a", "owner-a");
        var restored = Assert.Single(reloaded.Read(restoredScope).Assessments!);
        Assert.Equal(assessment.AssessmentId, restored.AssessmentId);
        Assert.Equal((assessment.CaseId, assessment.BasisId, assessment.BasisVersion, assessment.Outcome),
            (restored.CaseId, restored.BasisId, restored.BasisVersion, restored.Outcome));
        var expectedClaim = Assert.Single(assessment.Claims);
        var actualClaim = Assert.Single(restored.Claims);
        Assert.Equal((expectedClaim.Kind, expectedClaim.Value, expectedClaim.Component, expectedClaim.SerialNumber),
            (actualClaim.Kind, actualClaim.Value, actualClaim.Component, actualClaim.SerialNumber));
        Assert.Equal(new AssessedWitness("doc-1", 3), Assert.Single(actualClaim.Witnesses));
        Assert.Equal(receipt, Assert.Single(reloaded.Read(restoredScope).RequestReceipts!));
        Assert.Equal(1L, Assert.Single(reloaded.ReadAudit(restoredScope)).Revision);
        var other = reloaded.AuthorizeCase("case-b", "owner-b");
        Assert.Null(reloaded.Read(other).Assessments!.FirstOrDefault(a => a.AssessmentId == assessment.AssessmentId));
        Assert.Empty(reloaded.Read(other).RequestReceipts!);
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

    [Fact]
    public void AC1_read_of_missing_case_is_rejected()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-missing", "owner-m");

        // Make the case row unavailable, then read with its previously issued scope.
        using (var db = new CaseStateDbContext(options))
        {
            db.Cases.Remove(db.Cases.Single(c => c.CaseId == "case-missing"));
            db.SaveChanges();
        }

        Assert.Throws<CaseAccessDeniedException>(() => store.Read(scope));
        Assert.Throws<CaseAccessDeniedException>(() => store.GetBasisVersion(scope, 1));
    }

    // AC1 (basis immutability): version 1 keeps its content and digests across an append and a restart,
    // and every attempt to replace it is rejected without changing persisted state.
    [Fact]
    public void AC1_basis_digests_are_immutable_across_appends_and_restart()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-basis", "owner-b");
        var v1 = CaseStateStore.CreateBasisVersion(1, "basis one", string.Empty);
        store.Commit(scope, 0, new CaseCommit(v1, null, "owner-b", "basis-1", "v1"));
        var v2 = CaseStateStore.CreateBasisVersion(2, "basis two", v1.BasisDigest);
        store.Commit(scope, 1, new CaseCommit(v2, null, "owner-b", "basis-2", "v2"));

        var reloaded = new SqlCaseStateStore(options);
        var before = reloaded.Read(scope);
        Assert.Equal(2, before.Revision);
        Assert.Equal(2, before.Basis.Count);
        var stored = before.Basis[0];
        Assert.Equal(1, stored.Version);
        Assert.Equal(v1.Content, stored.Content);
        Assert.Equal(v1.ContentDigest, stored.ContentDigest);
        Assert.Equal(v1.BasisDigest, stored.BasisDigest);

        var auditBefore = CountAuditEvents(options);
        var decisionsBefore = CaptureDecisions(options, before.CaseId);

        Assert.Throws<BasisIntegrityException>(() =>
            reloaded.Commit(scope, before.Revision, new CaseCommit(v1 with { ContentDigest = "changed" }, null, "owner-b", "replace", "digest")));
        AssertStateUnchanged(reloaded, options, scope, before, auditBefore, decisionsBefore);

        Assert.Throws<BasisIntegrityException>(() =>
            reloaded.Commit(scope, before.Revision, new CaseCommit(v1 with { BasisDigest = "changed" }, null, "owner-b", "replace", "chain")));
        AssertStateUnchanged(reloaded, options, scope, before, auditBefore, decisionsBefore);

        Assert.Throws<BasisIntegrityException>(() =>
            reloaded.Commit(scope, before.Revision, new CaseCommit(v1 with { Content = "changed payload" }, null, "owner-b", "replace", "payload")));
        AssertStateUnchanged(reloaded, options, scope, before, auditBefore, decisionsBefore);

        // A fresh store over the same database sees the original version 1 after the rejected attempts.
        var restarted = new SqlCaseStateStore(options).Read(scope);
        Assert.Equal(v1.Content, restarted.Basis[0].Content);
        Assert.Equal(v1.ContentDigest, restarted.Basis[0].ContentDigest);
        Assert.Equal(v1.BasisDigest, restarted.Basis[0].BasisDigest);
    }

    // AC2 (receipts): an exact replay returns the original receipt and fingerprint without a revision or audit change;
    // the same receipt ID with a changed payload is rejected.
    [Fact]
    public void AC2_exact_receipt_replay_is_idempotent_and_changed_payload_is_rejected()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-replay", "owner-p");
        var basis = CaseStateStore.CreateBasisVersion(1, "replay basis", string.Empty);
        var first = store.Commit(scope, 0, new CaseCommit(basis, "receipt-p", "owner-p", "open", "first"));
        Assert.False(first.Replayed);

        var before = store.Read(scope);
        var auditBefore = CountAuditEvents(options);
        var decisionsBefore = CaptureDecisions(options, before.CaseId);
        Assert.Equal(new[] { ("receipt-p", decisionsBefore[0].Fingerprint) }, decisionsBefore);

        var replay = store.Commit(scope, before.Revision, new CaseCommit(basis, "receipt-p", "owner-p", "open", "first"));
        Assert.True(replay.Replayed);
        Assert.Equal(new[] { "receipt-p" }, replay.State.ReceiptIds);
        Assert.Equal(first.State.ReceiptFingerprints["receipt-p"], replay.State.ReceiptFingerprints["receipt-p"]);
        Assert.Equal(decisionsBefore[0].Fingerprint, replay.State.ReceiptFingerprints["receipt-p"]);
        Assert.Equal(before.Revision, replay.State.Revision);
        AssertStateUnchanged(store, options, scope, before, auditBefore, decisionsBefore);

        Assert.Throws<ReceiptConflictException>(() =>
            store.Commit(scope, before.Revision, new CaseCommit(basis, "receipt-p", "owner-p", "open", "changed")));
        AssertStateUnchanged(store, options, scope, before, auditBefore, decisionsBefore);
    }

    // AC3 (atomicity): a failure after the SQL changes are applied but before the transaction commits rolls back
    // the basis, receipt, and audit event together.
    [Fact]
    public void AC3_failed_commit_rolls_back_all_records()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-rollback", "owner-rb");
        var before = store.Read(scope);
        var auditBefore = CountAuditEvents(options);
        var decisionsBefore = CaptureDecisions(options, before.CaseId);

        // The interceptor is attached only after case creation so the case row itself is not affected.
        var interceptor = new FailAfterSaveInterceptor();
        var faultyOptions = new DbContextOptionsBuilder<CaseStateDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        var faulty = new SqlCaseStateStore(faultyOptions);

        var basis = CaseStateStore.CreateBasisVersion(1, "rollback basis", string.Empty);
        interceptor.Armed = true;
        Assert.Throws<InjectedCommitFailure>(() =>
            faulty.Commit(scope, before.Revision, new CaseCommit(basis, "receipt-rb", "owner-rb", "open", "rollback")));
        interceptor.Armed = false;

        AssertStateUnchanged(store, options, scope, before, auditBefore, decisionsBefore);
        Assert.Empty(store.Read(scope).Basis);
        Assert.Empty(store.Read(scope).ReceiptIds);
    }

    private sealed class InjectedCommitFailure : Exception
    {
    }

    private sealed class FailAfterSaveInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            if (Armed)
            {
                throw new InjectedCommitFailure();
            }

            return base.SavedChanges(eventData, result);
        }
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
        Assert.Equal(before.Basis, after.Basis);
        Assert.Equal(before.ReceiptIds, after.ReceiptIds);
        Assert.Equal(before.Audit, after.Audit);
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

    // AC: M-03 — trace records are append-only: a duplicate fails, the original is untouched, and trace commands are SELECT/INSERT only.
    [Fact]
    public void TraceWritesAreAppendOnly()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var recorder = new CommandRecorder();
        var recordingOptions = new DbContextOptionsBuilder<CaseStateDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(recorder)
            .Options;
        var store = new SqlCaseStateStore(recordingOptions);
        var scope = store.CreateCase("case-1", "owner-a");
        var original = NewTrace("case-1", "run-1", "inv-1");
        recorder.Commands.Clear();

        store.AppendTrace(scope, original);
        var duplicate = NewTrace("case-1", "run-1", "inv-1", actor: "tok-other") with
        {
            Steps = [new InvestigationTraceStep(1, "other-tool", "doc-x", 9, "unverified", "None", "None")],
        };
        Assert.Throws<TraceConflictException>(() => store.AppendTrace(scope, duplicate));

        var reloaded = new SqlCaseStateStore(options);
        var reloadedScope = reloaded.AuthorizeCase("case-1", "owner-a");
        AssertSameTrace(original, reloaded.FindTrace(reloadedScope, "case-1", "run-1", "inv-1")!);

        var traceCommands = recorder.Commands.Where(c => c.Contains("investigation_trace", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(traceCommands, c => LeadingKeyword(c) == "INSERT");
        Assert.All(traceCommands, c => Assert.Contains(LeadingKeyword(c), new[] { "SELECT", "INSERT" }));
        Assert.DoesNotContain(recorder.Commands, c =>
            c.Contains("investigation_trace", StringComparison.OrdinalIgnoreCase)
            && (LeadingKeyword(c) is "UPDATE" or "DELETE" or "REPLACE" or "UPSERT"));
    }

    // AC: TRACE-STORE-03 — FindTrace requires the exact case, run and investigation ids; each mismatch independently returns null.
    [Fact]
    public void TraceLookupRequiresExactCaseRunAndInvestigation()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "owner-a");
        var trace = NewTrace("case-1", "run-1", "inv-1");
        store.AppendTrace(scope, trace);

        Assert.Null(store.FindTrace(scope, "case-2", "run-1", "inv-1"));
        Assert.Null(store.FindTrace(scope, "case-1", "run-2", "inv-1"));
        Assert.Null(store.FindTrace(scope, "case-1", "run-1", "inv-2"));
        Assert.Null(store.FindTrace(scope, "CASE-1", "run-1", "inv-1"));
        Assert.Null(store.FindTrace(scope, "case-1", "RUN-1", "inv-1"));
        Assert.Null(store.FindTrace(scope, "case-1", "run-1", "INV-1"));
        AssertSameTrace(trace, store.FindTrace(scope, "case-1", "run-1", "inv-1")!);
    }

    // AC: all InvestigationTrace and step fields persist, steps reload in Sequence order, and (CaseId, RunId, InvestigationId) is unique in the database.
    // AC: M-03 — identifiers differing only by surrounding spaces are distinct exact keys; SQL Server would otherwise pad them into one key.
    [Fact]
    public void TraceKeysKeepSurroundingWhitespaceDistinct()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "owner-a");
        store.AppendTrace(scope, NewTrace("case-1", "run-1", "inv-1"));
        store.AppendTrace(scope, NewTrace("case-1", "run-1 ", "inv-1"));
        store.AppendTrace(scope, NewTrace("case-1", " run-1", "inv-1"));

        Assert.Equal("run-1 ", store.FindTrace(scope, "case-1", "run-1 ", "inv-1")!.RunId);
        Assert.Equal(" run-1", store.FindTrace(scope, "case-1", " run-1", "inv-1")!.RunId);
        Assert.Null(store.FindTrace(scope, "case-1", "run-1 ", "inv-2"));
        Assert.Throws<TraceConflictException>(() => store.AppendTrace(scope, NewTrace("case-1", "run-1 ", "inv-1")));
    }

    // AC: M-03 — lone surrogates are exact identifiers: they persist and reload unchanged, and stay distinct from U+FFFD.
    [Fact]
    public void TraceKeepsLoneSurrogateIdentifiersExact()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "owner-a");
        const string HighSurrogate = "run-\uD800";
        const string LowSurrogate = "run-\uDC00";
        const string Replacement = "run-\uFFFD";

        store.AppendTrace(scope, NewTrace("case-1", HighSurrogate, "inv-1"));
        store.AppendTrace(scope, NewTrace("case-1", LowSurrogate, "inv-1"));
        store.AppendTrace(scope, NewTrace("case-1", "run-1", "\uDC00inv"));
        store.AppendTrace(scope, NewTrace("case-1", Replacement, "inv-1"));
        Assert.Equal(4, CountTraceRows(options));

        Assert.Equal(HighSurrogate, store.FindTrace(scope, "case-1", HighSurrogate, "inv-1")!.RunId);
        Assert.Equal(LowSurrogate, store.FindTrace(scope, "case-1", LowSurrogate, "inv-1")!.RunId);
        Assert.Equal("\uDC00inv", store.FindTrace(scope, "case-1", "run-1", "\uDC00inv")!.InvestigationId);
        Assert.Equal(Replacement, store.FindTrace(scope, "case-1", Replacement, "inv-1")!.RunId);
        Assert.Throws<TraceConflictException>(() => store.AppendTrace(scope, NewTrace("case-1", HighSurrogate, "inv-1")));
    }

    // AC: M-03 — the database itself rejects a second (case, run, investigation) row, even with a different key hash.
    [Fact]
    public void TraceIdentifierTupleIsUniqueInDatabase()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "owner-a");
        store.AppendTrace(scope, NewTrace("case-1", "run-1", "inv-1"));

        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO investigation_traces (KeyHash, CaseId, RunId, InvestigationId, CaseIdUtf16, CaseIdLength, RunIdUtf16, RunIdLength, InvestigationIdUtf16, InvestigationIdLength, BasisVersion, ModelDeploymentName, ApiVersion, Region, ContentFilterSetting, Decision, ActorToken, Outcome) " +
            "VALUES (randomblob(32), 'case-1', 'run-1', 'inv-1', @c, 6, @r, 5, @i, 5, 1, 'm', 'v', 'r', 'c', 'd', 'a', 'o')";
        command.Parameters.AddWithValue("@c", System.Text.Encoding.Unicode.GetBytes("case-1"));
        command.Parameters.AddWithValue("@r", System.Text.Encoding.Unicode.GetBytes("run-1"));
        command.Parameters.AddWithValue("@i", System.Text.Encoding.Unicode.GetBytes("inv-1"));
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(1, CountTraceRows(options));
    }

    [Fact]
    public void TraceIdentifierLengthIsLimited()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "owner-a");

        // 64 surrogate pairs is exactly 128 UTF-16 code units and is accepted.
        var atLimit = string.Concat(Enumerable.Repeat("\uD83D\uDE00", 64));
        store.AppendTrace(scope, NewTrace("case-1", atLimit, "inv-1"));
        Assert.Equal(1, CountTraceRows(options));

        // One code unit over the limit is rejected, including when the extra unit is half a surrogate pair.
        var overByOneSurrogate = atLimit + "\uD83D";
        Assert.Throws<ArgumentException>(() => store.AppendTrace(scope, NewTrace("case-1", overByOneSurrogate, "inv-1")));
        Assert.Throws<ArgumentException>(() => store.AppendTrace(scope, NewTrace("case-1", "run-2", new string('a', 129))));
        Assert.Throws<ArgumentException>(() => store.AppendTrace(scope, NewTrace("case-1", "run-3", atLimit + "bc")));
        Assert.Equal(1, CountTraceRows(options));
        Assert.Null(store.FindTrace(scope, "case-1", "run-2", new string('a', 129)));

        // CaseId is limited too: a scope for the overlong case id reaches the length check and is rejected by name.
        var longCaseId = new string('c', 129);
        var longScope = store.CreateCase(longCaseId, "owner-a");
        var caseIdError = Assert.Throws<ArgumentException>(() => store.AppendTrace(longScope, NewTrace(longCaseId, "run-4", "inv-1")));
        Assert.Equal("CaseId", caseIdError.ParamName);
        Assert.Equal(1, CountTraceRows(options));
    }

    [Fact]
    public void TraceIdentifiersDifferingByTrailingNulAreDistinct()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "owner-a");
        store.AppendTrace(scope, NewTrace("case-1", "run-1", "inv-1"));
        store.AppendTrace(scope, NewTrace("case-1", "run-1\0", "inv-1"));

        Assert.Equal(2, CountTraceRows(options));
        Assert.Equal("run-1", store.FindTrace(scope, "case-1", "run-1", "inv-1")!.RunId);
        Assert.Equal("run-1\0", store.FindTrace(scope, "case-1", "run-1\0", "inv-1")!.RunId);
        Assert.Null(store.FindTrace(scope, "case-1", "run-1\0\0", "inv-1"));
    }

    [Fact]
    public void TracePersistenceRoundTripsAllFields()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "owner-a");
        var trace = NewTrace("case-1", "run-1", "inv-1");
        store.AppendTrace(scope, trace);

        var reloaded = new SqlCaseStateStore(options);
        var reloadedScope = reloaded.AuthorizeCase("case-1", "owner-a");
        var stored = reloaded.FindTrace(reloadedScope, "case-1", "run-1", "inv-1")!;
        AssertSameTrace(trace, stored);
        Assert.Equal(new[] { 1, 2 }, stored.Steps.Select(s => s.Sequence));

        byte[] storedKey;
        using (var read = new CaseStateDbContext(options))
        {
            storedKey = read.InvestigationTraces.Single().KeyHash;
        }

        using var db = new CaseStateDbContext(options);
        db.InvestigationTraces.Add(new InvestigationTraceRow
        {
            KeyHash = storedKey,
            CaseId = "case-1",
            RunId = "run-1",
            InvestigationId = "inv-1",
            BasisVersion = 3,
            ModelDeploymentName = "model-deploy-x",
            ApiVersion = "2025-01-01",
            Region = "eastus",
            ContentFilterSetting = "enabled",
            Decision = "approve",
            ActorToken = "tok-7f3a",
            Outcome = "ready",
        });
        Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
    }

    private static InvestigationTrace NewTrace(string caseId, string runId, string investigationId, string actor = "tok-7f3a") =>
        new(
            caseId,
            runId,
            investigationId,
            BasisVersion: 3,
            ModelDeploymentName: "model-deploy-x",
            ApiVersion: "2025-01-01",
            Region: "westeurope",
            ContentFilterSetting: "strict-filter",
            Decision: "return-to-service",
            ActorToken: actor,
            Outcome: "escalated",
            Steps:
            [
                new InvestigationTraceStep(1, "search-manual", "doc-ata-32", 14, "verified", "None", "None"),
                new InvestigationTraceStep(2, "check-limit", "doc-mel-05", 2, "rewritten", "CitationRewrite", "ManualReview"),
            ]);

    private static void AssertSameTrace(InvestigationTrace expected, InvestigationTrace actual)
    {
        Assert.Equal(expected with { Steps = null! }, actual with { Steps = null! });
        Assert.Equal(expected.Steps, actual.Steps);
    }

    private static int CountTraceRows(DbContextOptions<CaseStateDbContext> options)
    {
        using var db = new CaseStateDbContext(options);
        return db.InvestigationTraces.Count();
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

    // QW-01 to QW-03 over the EF/SQLite store: persistence, validation, scope, revision and audit.
    [Fact]
    public void QW01_to_QW03_work_items_persist_validate_scope_and_audit_in_sqlite()
    {
        using var connection = OpenMigratedSqlite(out var options);
        var store = new SqlCaseStateStore(options);
        var before = DateTimeOffset.UtcNow;
        var scope = store.CreateCase("case-a", "owner-a");
        var other = store.CreateCase("case-b", "owner-b");
        var auditBefore = CountAuditEvents(options);

        var created = store.CreateWorkItem(scope, 0, new WorkItemDraft("item-1", "cap-1", "open", "owner-a"), "presenter");
        Assert.Equal(1, created.State.Revision);
        store.CreateWorkItem(other, 0, new WorkItemDraft("item-1", "cap-1", "open", "owner-b"), "presenter");
        Assert.Equal(auditBefore + 2, CountAuditEvents(options));

        // QW-01: each missing or blank required field is rejected, and nothing is written or audited.
        var invalidDrafts = new[]
        {
            new WorkItemDraft("", "cap-1", "open", "owner-a"),
            new WorkItemDraft(" ", "cap-1", "open", "owner-a"),
            new WorkItemDraft("item-2", "", "open", "owner-a"),
            new WorkItemDraft("item-2", " ", "open", "owner-a"),
            new WorkItemDraft("item-2", "cap-1", "", "owner-a"),
            new WorkItemDraft("item-2", "cap-1", " ", "owner-a"),
            new WorkItemDraft("item-2", "cap-1", "open", ""),
            new WorkItemDraft("item-2", "cap-1", "open", " "),
        };
        foreach (var invalid in invalidDrafts)
        {
            Assert.Throws<ArgumentException>(() => store.CreateWorkItem(scope, 1, invalid, "presenter"));
        }
        Assert.Single(store.ListWorkItems(scope));
        Assert.Equal(auditBefore + 2, CountAuditEvents(options));

        Assert.Throws<InvalidOperationException>(() => store.CreateWorkItem(scope, 1, new WorkItemDraft("item-1", "cap-2", "open", "owner-a"), "presenter"));
        Assert.Equal(1, store.Read(scope).Revision);
        Assert.Equal(auditBefore + 2, CountAuditEvents(options));

        var reloaded = new SqlCaseStateStore(options);
        var reloadedScope = reloaded.AuthorizeCase("case-a", "owner-a");
        var item = Assert.Single(reloaded.ListWorkItems(reloadedScope));
        Assert.Equal(("case-a", "item-1", "cap-1", "open", "owner-a"), (item.CaseId, item.ItemId, item.CapabilityId, item.State, item.Owner));
        // The creation timestamp is server-assigned: the draft has no such property, and the stored value is the server's clock.
        Assert.Null(typeof(WorkItemDraft).GetProperty("CreatedAt"));
        Assert.InRange(item.CreatedAt, before, DateTimeOffset.UtcNow);
        Assert.Equal("item-1", Assert.Single(reloaded.ListWorkItems(reloaded.AuthorizeCase("case-b", "owner-b"))).ItemId);
        Assert.Throws<CaseAccessDeniedException>(() => reloaded.AuthorizeCase("case-a", "owner-b"));

        var createdAudit = Assert.Single(reloaded.ReadAudit(reloadedScope));
        Assert.Equal(("presenter", "create-work-item", "item-1", 1L), (createdAudit.Actor, createdAudit.Action, createdAudit.Detail, createdAudit.Revision));

        var changed = reloaded.ChangeWorkItemState(reloadedScope, 1, "item-1", "closed", "reviewer");
        Assert.Equal(2, changed.State.Revision);
        var auditAfterChange = reloaded.ReadAudit(reloadedScope);
        Assert.Equal(2, auditAfterChange.Count);
        var changeAudit = auditAfterChange[1];
        Assert.Equal(("reviewer", "change-work-item-state", "item-1 -> closed", 2L), (changeAudit.Actor, changeAudit.Action, changeAudit.Detail, changeAudit.Revision));

        Assert.Throws<RevisionConflictException>(() => reloaded.ChangeWorkItemState(reloadedScope, 1, "item-1", "reopened", "reviewer"));
        Assert.Equal("closed", Assert.Single(reloaded.ListWorkItems(reloadedScope)).State);
        Assert.Equal(2, reloaded.Read(reloadedScope).Revision);
        Assert.Equal(auditAfterChange, reloaded.ReadAudit(reloadedScope));
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
