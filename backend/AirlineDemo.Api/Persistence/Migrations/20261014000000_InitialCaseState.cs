using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

[DbContext(typeof(CaseStateDbContext))]
[Migration("20261014000000_InitialCaseState")]
public sealed class InitialCaseState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var sqlServer = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer";
        var dbo = sqlServer ? "dbo" : null;
        var audit = sqlServer ? "audit" : null;
        if (sqlServer)
        {
            migrationBuilder.Sql("IF SCHEMA_ID(N'audit') IS NULL EXEC(N'CREATE SCHEMA audit');");
        }

        migrationBuilder.CreateTable("cases", schema: dbo, columns: t => new
        {
            CaseId = t.Column<string>(maxLength: 128),
            Owner = t.Column<string>(maxLength: 256),
            Revision = t.Column<long>(),
        }, constraints: t => t.PrimaryKey("PK_cases", x => x.CaseId));

        migrationBuilder.CreateTable("case_documents", schema: dbo, columns: t => new
        {
            CaseId = t.Column<string>(maxLength: 128),
            Version = t.Column<int>(),
            Content = t.Column<string>(),
            ContentDigest = t.Column<string>(maxLength: 128),
            BasisDigest = t.Column<string>(maxLength: 128),
            CreatedAt = t.Column<DateTimeOffset>(),
        }, constraints: t => t.PrimaryKey("PK_case_documents", x => new { x.CaseId, x.Version }));

        migrationBuilder.CreateTable("case_decisions", schema: dbo, columns: t => new
        {
            CaseId = t.Column<string>(maxLength: 128),
            ReceiptId = t.Column<string>(maxLength: 256),
            Fingerprint = t.Column<string>(maxLength: 128),
            Revision = t.Column<long>(),
        }, constraints: t => t.PrimaryKey("PK_case_decisions", x => new { x.CaseId, x.ReceiptId }));

        migrationBuilder.CreateTable("case_events", schema: audit, columns: t => new
        {
            CaseId = t.Column<string>(maxLength: 128),
            Sequence = t.Column<long>(),
            Revision = t.Column<long>(),
            Actor = t.Column<string>(maxLength: 256),
            Action = t.Column<string>(maxLength: 128),
            Detail = t.Column<string>(),
            At = t.Column<DateTimeOffset>(),
        }, constraints: t => t.PrimaryKey("PK_case_events", x => new { x.CaseId, x.Sequence }));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        var sqlServer = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer";
        migrationBuilder.DropTable("case_events", schema: sqlServer ? "audit" : null);
        migrationBuilder.DropTable("case_decisions", schema: sqlServer ? "dbo" : null);
        migrationBuilder.DropTable("case_documents", schema: sqlServer ? "dbo" : null);
        migrationBuilder.DropTable("cases", schema: sqlServer ? "dbo" : null);
    }
}
