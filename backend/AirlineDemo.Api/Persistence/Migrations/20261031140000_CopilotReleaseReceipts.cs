using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

/// <summary>
/// Insert-only copilot release receipts: the exact receipt bytes and command fingerprint, written in the same
/// transaction as the release (the app identity needs SELECT and INSERT).
/// </summary>
[DbContext(typeof(CaseStateDbContext))]
[Migration("20261031140000_CopilotReleaseReceipts")]
public class CopilotReleaseReceipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var guid = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "uniqueidentifier";
        migrationBuilder.CreateTable(
            name: "copilot_release_receipts",
            schema: "dbo",
            columns: table => new
            {
                ProposalId = table.Column<Guid>(type: guid, nullable: false),
                Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Receipt = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_copilot_release_receipts", x => x.ProposalId));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "copilot_release_receipts", schema: "dbo");
}
