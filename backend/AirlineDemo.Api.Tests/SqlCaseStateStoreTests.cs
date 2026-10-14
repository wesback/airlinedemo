using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AirlineDemo.Api.Tests;

public sealed class SqlCaseStateStoreTests
{
    private const string ManagedIdentityConnection =
        "Server=tcp:case-state.database.windows.net,1433;Database=case_state;Encrypt=True;Authentication=Active Directory Managed Identity";

    // AC1: interface members, implementations, and runtime registration; no database connection is opened.
    [Fact]
    public void AC1_interface_and_runtime_registration_use_sql_store()
    {
        var names = typeof(ICaseStateStore).GetMethods().Select(m => m.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "AuthorizeCase", "Commit", "CreateCase", "GetBasisVersion", "OwnerOf", "Read", "ReadAudit" }, names);
        Assert.True(typeof(ICaseStateStore).IsAssignableFrom(typeof(CaseStateStore)));
        Assert.True(typeof(ICaseStateStore).IsAssignableFrom(typeof(SqlCaseStateStore)));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CaseState"] = ManagedIdentityConnection })
            .Build();
        var services = new ServiceCollection().AddSqlCaseState(config);
        Assert.Single(services, d => d.ServiceType == typeof(ICaseStateStore));
        Assert.Contains(services, d => d.ServiceType == typeof(SqlCaseStateStore));
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(CaseStateStore));
    }

    // AC2: table/schema mapping matches the grants, the migration is versioned, and the app adapter never migrates.
    [Fact]
    public void AC2_schema_matches_grants_and_migration_is_separate()
    {
        using var db = new CaseStateDbContext(CaseStateServices.SqlOptions(ManagedIdentityConnection));
        Assert.Equal("dbo.cases", Qualified(db, typeof(CaseRow)));
        Assert.Equal("dbo.case_documents", Qualified(db, typeof(CaseDocumentRow)));
        Assert.Equal("dbo.case_decisions", Qualified(db, typeof(CaseDecisionRow)));
        Assert.Equal("audit.case_events", Qualified(db, typeof(CaseEventRow)));

        var migration = typeof(InitialCaseState).GetCustomAttribute<MigrationAttribute>();
        Assert.EndsWith("_InitialCaseState", migration!.Id);
        Assert.DoesNotContain(typeof(SqlCaseStateStore).GetMethods(), m => m.Name.Contains("Migrate"));
    }

    // AC3: migrate, create, restart (new store on the same database), and reload without duplicate records.
    [Fact]
    public void AC3_migration_and_restart_round_trip()
    {
        using var connection = MigratedInMemory(out var options);
        var store = new SqlCaseStateStore(options);
        var scope = store.CreateCase("case-1", "owner-a");
        var commit = new CaseCommit(
            CaseStateStore.CreateBasisVersion(1, "basis v1", string.Empty), "receipt-1", "reviewer", "decide", "approved");
        store.Commit(scope, 0, commit);

        var restarted = new SqlCaseStateStore(options);
        var reloaded = restarted.Read(restarted.AuthorizeCase("case-1", "owner-a"));
        Assert.Equal(1, reloaded.Revision);
        Assert.Equal("basis v1", Assert.Single(reloaded.Basis).Content);
        Assert.Equal(new[] { "receipt-1" }, reloaded.ReceiptIds);
        Assert.Single(reloaded.Audit);

        var replay = restarted.Commit(restarted.AuthorizeCase("case-1", "owner-a"), 1, commit);
        Assert.True(replay.Replayed);
        Assert.Equal(1, replay.State.Revision);
        Assert.Single(replay.State.Basis);
        Assert.Single(replay.State.Audit);
    }

    // AC4: owner, duplicate, scope isolation, and stale-revision behaviour, each with its negative case.
    [Fact]
    public void AC4_scope_and_stale_revision_behavior()
    {
        using var connection = MigratedInMemory(out var options);
        var store = new SqlCaseStateStore(options);
        var a = store.CreateCase("case-a", "Owner");
        var b = store.CreateCase("case-b", "Owner");

        Assert.Equal("Owner", store.OwnerOf("case-a"));
        Assert.Null(store.OwnerOf("missing"));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-a", "owner"));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-a", "Other"));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("case-a", string.Empty));
        Assert.Throws<CaseAccessDeniedException>(() => store.AuthorizeCase("missing", "Owner"));
        Assert.Throws<InvalidOperationException>(() => store.CreateCase("case-a", "Owner"));

        store.Commit(b, 0, new CaseCommit(
            CaseStateStore.CreateBasisVersion(1, "b basis", string.Empty), "receipt-b", "actor", "decide", "b"));
        Assert.Equal("case-a", store.Read(a).CaseId);
        Assert.Throws<KeyNotFoundException>(() => store.GetBasisVersion(a, 1));
        Assert.Equal("b basis", store.GetBasisVersion(b, 1).Content);

        var before = store.Read(a);
        var stale = new CaseCommit(
            CaseStateStore.CreateBasisVersion(1, "a basis", string.Empty), "receipt-a", "actor", "decide", "stale");
        Assert.Throws<RevisionConflictException>(() => store.Commit(a, before.Revision + 5, stale));
        var after = store.Read(a);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Audit.Count, after.Audit.Count);
        Assert.Empty(after.Basis);
    }

    // Open in-memory SQLite connection with the versioned migration applied; the caller disposes the connection.
    private static SqliteConnection MigratedInMemory(out DbContextOptions<CaseStateDbContext> options)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(connection).Options;
        using var migrate = new CaseStateDbContext(options);
        migrate.Database.Migrate();
        return connection;
    }

    private static string Qualified(DbContext db, Type entity)
    {
        var type = db.Model.FindEntityType(entity)!;
        return $"{type.GetSchema()}.{type.GetTableName()}";
    }
}
