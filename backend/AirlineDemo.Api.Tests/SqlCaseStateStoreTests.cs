using System.Runtime.CompilerServices;
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
        Assert.Contains("AddSingleton<ICaseStateStore>(_ => new CaseStateStore(", program);
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

    private static void AssertMapped(CaseStateDbContext db, Type entity, string schema, string table)
    {
        var type = db.Model.FindEntityType(entity);
        Assert.NotNull(type);
        Assert.Equal(schema, type.GetSchema());
        Assert.Equal(table, type.GetTableName());
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
