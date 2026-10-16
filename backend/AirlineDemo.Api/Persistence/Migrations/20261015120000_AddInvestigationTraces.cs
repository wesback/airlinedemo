using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

/// <summary>
/// Adds the append-only investigation trace record and its ordered steps. Run under the migration identity only.
/// </summary>
[DbContext(typeof(CaseStateDbContext))]
[Migration("20261015120000_AddInvestigationTraces")]
public class AddInvestigationTraces : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Identifiers are compared exactly, so the key columns must not collapse case on case-insensitive databases.
        var keyCollation = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer"
            ? "Latin1_General_100_CS_AS"
            : null;

        migrationBuilder.CreateTable(
            name: "investigation_traces",
            schema: "dbo",
            columns: table => new
            {
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: keyCollation),
                RunId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: keyCollation),
                InvestigationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: keyCollation),
                BasisVersion = table.Column<int>(type: "int", nullable: false),
                ModelDeploymentName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                ApiVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Region = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ContentFilterSetting = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Decision = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ActorToken = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Outcome = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_investigation_traces", x => new { x.CaseId, x.RunId, x.InvestigationId }));

        migrationBuilder.CreateTable(
            name: "investigation_trace_steps",
            schema: "dbo",
            columns: table => new
            {
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: keyCollation),
                RunId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: keyCollation),
                InvestigationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: keyCollation),
                Sequence = table.Column<int>(type: "int", nullable: false),
                ToolName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                DocumentId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                PageNumber = table.Column<int>(type: "int", nullable: false),
                CitationCheckOutcome = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CorrectionCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                PolicyHandover = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_investigation_trace_steps", x => new { x.CaseId, x.RunId, x.InvestigationId, x.Sequence }));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "investigation_trace_steps", schema: "dbo");
        migrationBuilder.DropTable(name: "investigation_traces", schema: "dbo");
    }
}
