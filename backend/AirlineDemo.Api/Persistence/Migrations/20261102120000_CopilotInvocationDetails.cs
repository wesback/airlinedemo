using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

/// <summary>
/// Insert-only copilot invocation details: record identities, screening verdicts, proposal links and release gate
/// results for the case activity trail (the app identity needs SELECT and INSERT).
/// </summary>
[DbContext(typeof(CaseStateDbContext))]
[Migration("20261102120000_CopilotInvocationDetails")]
public class CopilotInvocationDetails : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var guid = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "uniqueidentifier";
        migrationBuilder.CreateTable(
            name: "copilot_invocation_details",
            schema: "dbo",
            columns: table => new
            {
                DetailId = table.Column<Guid>(type: guid, nullable: false),
                InvocationId = table.Column<Guid>(type: guid, nullable: false),
                Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                AtTicks = table.Column<long>(type: "bigint", nullable: false),
                SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                RecordId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                RecordVersion = table.Column<int>(type: "int", nullable: true),
                ScreeningState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                FlaggedFields = table.Column<string>(type: "nvarchar(2200)", maxLength: 2200, nullable: true),
                ProposalId = table.Column<Guid>(type: guid, nullable: true),
                Gate = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                GateResult = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                BlockerCodes = table.Column<string>(type: "nvarchar(2200)", maxLength: 2200, nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_copilot_invocation_details", x => x.DetailId));
        migrationBuilder.CreateIndex(
            name: "IX_copilot_invocation_details_invocation",
            schema: "dbo",
            table: "copilot_invocation_details",
            column: "InvocationId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "copilot_invocation_details", schema: "dbo");
}
