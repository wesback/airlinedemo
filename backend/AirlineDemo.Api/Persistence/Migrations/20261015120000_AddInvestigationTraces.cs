using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

/// <summary>
/// Adds the append-only investigation trace record and its ordered steps. Run under the migration identity only.
/// Rows are keyed by a SHA-256 of the exact (case, run, investigation) identifiers, so SQL padding or collation cannot merge distinct ids.
/// </summary>
[DbContext(typeof(CaseStateDbContext))]
[Migration("20261015120000_AddInvestigationTraces")]
public class AddInvestigationTraces : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The nvarchar identifier columns are informational. Exact matching uses KeyHash and the UTF-16 byte columns;
        // KeyHash is the only unique key, because a text unique index would merge ids that differ by SQL padding.
        var keyCollation = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer"
            ? "Latin1_General_100_CS_AS"
            : null;

        migrationBuilder.CreateTable(
            name: "investigation_traces",
            schema: "dbo",
            columns: table => new
            {
                KeyHash = table.Column<byte[]>(type: "varbinary(32)", fixedLength: true, maxLength: 32, nullable: false),
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
                CaseIdUtf16 = table.Column<byte[]>(type: "varbinary(256)", maxLength: 256, nullable: false),
                CaseIdLength = table.Column<int>(type: "int", nullable: false),
                RunIdUtf16 = table.Column<byte[]>(type: "varbinary(256)", maxLength: 256, nullable: false),
                RunIdLength = table.Column<int>(type: "int", nullable: false),
                InvestigationIdUtf16 = table.Column<byte[]>(type: "varbinary(256)", maxLength: 256, nullable: false),
                InvestigationIdLength = table.Column<int>(type: "int", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_investigation_traces", x => x.KeyHash);
            });

        migrationBuilder.CreateIndex(
            name: "UX_investigation_traces_case_run_investigation",
            schema: "dbo",
            table: "investigation_traces",
            columns: new[]
            {
                "CaseIdUtf16", "CaseIdLength",
                "RunIdUtf16", "RunIdLength",
                "InvestigationIdUtf16", "InvestigationIdLength",
            },
            unique: true);

        migrationBuilder.CreateTable(
            name: "investigation_trace_steps",
            schema: "dbo",
            columns: table => new
            {
                KeyHash = table.Column<byte[]>(type: "varbinary(32)", fixedLength: true, maxLength: 32, nullable: false),
                Sequence = table.Column<int>(type: "int", nullable: false),
                ToolName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                DocumentId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                PageNumber = table.Column<int>(type: "int", nullable: false),
                CitationCheckOutcome = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CorrectionCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                PolicyHandover = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_investigation_trace_steps", x => new { x.KeyHash, x.Sequence });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "investigation_trace_steps", schema: "dbo");
        migrationBuilder.DropTable(name: "investigation_traces", schema: "dbo");
    }
}
