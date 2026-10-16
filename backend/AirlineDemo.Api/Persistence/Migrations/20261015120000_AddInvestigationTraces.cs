using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

/// <summary>
/// Adds the append-only investigation trace tables, keyed by case, run and investigation. Run under the migration identity only.
/// </summary>
[DbContext(typeof(CaseStateDbContext))]
[Migration("20261015120000_AddInvestigationTraces")]
public class AddInvestigationTraces : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "case_investigation_traces",
            schema: "dbo",
            columns: table => new
            {
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                RunId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                InvestigationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                BasisVersion = table.Column<int>(type: "int", nullable: false),
                ModelDeploymentName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ApiVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Region = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ContentFilterSetting = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Decision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ActorToken = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Outcome = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_case_investigation_traces", x => new { x.CaseId, x.RunId, x.InvestigationId }));

        migrationBuilder.CreateTable(
            name: "case_investigation_trace_steps",
            schema: "dbo",
            columns: table => new
            {
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                RunId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                InvestigationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Sequence = table.Column<int>(type: "int", nullable: false),
                ToolName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                DocumentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                PageNumber = table.Column<int>(type: "int", nullable: false),
                CitationCheckOutcome = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CorrectionCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                PolicyHandover = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_case_investigation_trace_steps", x => new { x.CaseId, x.RunId, x.InvestigationId, x.Sequence }));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "case_investigation_trace_steps", schema: "dbo");
        migrationBuilder.DropTable(name: "case_investigation_traces", schema: "dbo");
    }
}
