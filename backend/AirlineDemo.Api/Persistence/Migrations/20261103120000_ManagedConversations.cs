using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

[DbContext(typeof(CaseStateDbContext))]
[Migration("20261103120000_ManagedConversations")]
public sealed class ManagedConversations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var responseItemIdsType = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite"
            ? "TEXT"
            : "nvarchar(max)";
        migrationBuilder.CreateTable(
            name: "managed_conversations",
            schema: "dbo",
            columns: table => new
            {
                Handle = table.Column<string>(type: "nvarchar(43)", maxLength: 43, nullable: false),
                UserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Role = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                IsListing = table.Column<bool>(type: "bit", nullable: false),
                EntitlementFingerprint = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                CaseRevision = table.Column<long>(type: "bigint", nullable: false),
                AgentName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                AgentVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ConfigurationDigest = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                EvidenceVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                EvidenceHashesDigest = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                AttemptId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                CreateAttemptCount = table.Column<int>(type: "int", nullable: false),
                RemoteConversationId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                CreatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                ExpiresAtTicks = table.Column<long>(type: "bigint", nullable: false),
                UpdatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                AdmittedTurnCount = table.Column<int>(type: "int", nullable: false),
                RowRevision = table.Column<long>(type: "bigint", nullable: false),
                ActiveFence = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                TurnDeadlineTicks = table.Column<long>(type: "bigint", nullable: true),
                LastResponseId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                ResponseItemIdsJson = table.Column<string>(type: responseItemIdsType, nullable: false),
                ResultDigest = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_managed_conversations", x => x.Handle));

        migrationBuilder.CreateIndex(
            name: "UX_managed_conversations_attempt",
            schema: "dbo",
            table: "managed_conversations",
            column: "AttemptId",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "UX_managed_conversations_remote_id",
            schema: "dbo",
            table: "managed_conversations",
            column: "RemoteConversationId",
            unique: true,
            filter: "[RemoteConversationId] IS NOT NULL");
        migrationBuilder.CreateIndex(
            name: "UX_managed_conversations_active_fence",
            schema: "dbo",
            table: "managed_conversations",
            column: "ActiveFence",
            unique: true,
            filter: "[ActiveFence] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "managed_conversations", schema: "dbo");
}
