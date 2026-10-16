using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

/// <summary>
/// Adds the append-only investigation trace table. Run under the migration identity only.
/// </summary>
[DbContext(typeof(CaseStateDbContext))]
[Migration("20261016000000_AddCaseTraces")]
public class AddCaseTraces : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "case_traces",
            schema: "dbo",
            columns: table => new
            {
                Id = table.Column<long>(type: migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "INTEGER" : "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1")
                    .Annotation("Sqlite:Autoincrement", true),
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                RunId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                InvestigationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                TraceJson = table.Column<string>(nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_case_traces", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_case_traces_CaseId_RunId_InvestigationId",
            schema: "dbo",
            table: "case_traces",
            columns: ["CaseId", "RunId", "InvestigationId"],
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "case_traces", schema: "dbo");
    }
}
