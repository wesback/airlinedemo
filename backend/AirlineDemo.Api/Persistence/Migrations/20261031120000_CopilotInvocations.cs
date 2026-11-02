using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

/// <summary>Append-only copilot invocation ledger (admit and outcome rows); the app identity needs SELECT and INSERT only.</summary>
[DbContext(typeof(CaseStateDbContext))]
[Migration("20261031120000_CopilotInvocations")]
public class CopilotInvocations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var isSqlite = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite";
        migrationBuilder.CreateTable(
            name: "copilot_invocations",
            schema: "dbo",
            columns: table => new
            {
                InvocationId = table.Column<Guid>(type: isSqlite ? "TEXT" : "uniqueidentifier", nullable: false),
                Phase = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                AtTicks = table.Column<long>(type: "bigint", nullable: false),
                Operation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Provenance = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                CallerRef = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                SessionRef = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                AuthorizedCaseRef = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                Outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                HttpStatus = table.Column<int>(type: "int", nullable: true),
                ResultCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_copilot_invocations", x => new { x.InvocationId, x.Phase }));
        migrationBuilder.CreateIndex(
            name: "IX_copilot_invocations_case_at",
            schema: "dbo",
            table: "copilot_invocations",
            columns: new[] { "AuthorizedCaseRef", "AtTicks" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "copilot_invocations", schema: "dbo");
}
