using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

/// <summary>
/// Copilot final-release proposals: the tuple table is insert-only; the separate lifecycle table is the only updated row
/// (the app identity needs SELECT and INSERT on both and UPDATE on <c>copilot_proposal_states</c>).
/// </summary>
[DbContext(typeof(CaseStateDbContext))]
[Migration("20261031130000_CopilotProposals")]
public class CopilotProposals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var isSqlite = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite";
        var guid = isSqlite ? "TEXT" : "uniqueidentifier";
        migrationBuilder.CreateTable(
            name: "copilot_proposals",
            schema: "dbo",
            columns: table => new
            {
                ProposalId = table.Column<Guid>(type: guid, nullable: false),
                CallerRef = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SessionRef = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ExpectedRevision = table.Column<long>(type: "bigint", nullable: false),
                Note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                CreatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                ExpiresAtTicks = table.Column<long>(type: "bigint", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_copilot_proposals", x => x.ProposalId));
        migrationBuilder.CreateTable(
            name: "copilot_proposal_states",
            schema: "dbo",
            columns: table => new
            {
                ProposalId = table.Column<Guid>(type: guid, nullable: false),
                State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_copilot_proposal_states", x => x.ProposalId));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "copilot_proposal_states", schema: "dbo");
        migrationBuilder.DropTable(name: "copilot_proposals", schema: "dbo");
    }
}
