using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

/// <summary>
/// Creates the case-state tables in the provisioned schema. Run under the migration identity only.
/// </summary>
[DbContext(typeof(CaseStateDbContext))]
[Migration("20261014190000_InitialCaseStateSchema")]
public class InitialCaseStateSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "audit");

        migrationBuilder.CreateTable(
            name: "cases",
            schema: "dbo",
            columns: table => new
            {
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Owner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_cases", x => x.CaseId));

        migrationBuilder.CreateTable(
            name: "case_documents",
            schema: "dbo",
            columns: table => new
            {
                Id = table.Column<long>(type: migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "INTEGER" : "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1")
                    .Annotation("Sqlite:Autoincrement", true),
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Version = table.Column<int>(type: "int", nullable: false),
                Content = table.Column<string>(nullable: false),
                ContentDigest = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                BasisDigest = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_case_documents", x => x.Id));

        migrationBuilder.CreateTable(
            name: "case_decisions",
            schema: "dbo",
            columns: table => new
            {
                Id = table.Column<long>(type: migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "INTEGER" : "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1")
                    .Annotation("Sqlite:Autoincrement", true),
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ReceiptId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Fingerprint = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_case_decisions", x => x.Id));

        migrationBuilder.CreateTable(
            name: "case_events",
            schema: "audit",
            columns: table => new
            {
                Sequence = table.Column<long>(type: migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "INTEGER" : "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1")
                    .Annotation("Sqlite:Autoincrement", true),
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Action = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Detail = table.Column<string>(nullable: false),
                At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_case_events", x => x.Sequence));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "case_events", schema: "audit");
        migrationBuilder.DropTable(name: "case_decisions", schema: "dbo");
        migrationBuilder.DropTable(name: "case_documents", schema: "dbo");
        migrationBuilder.DropTable(name: "cases", schema: "dbo");
    }
}
