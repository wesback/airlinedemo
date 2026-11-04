using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

[DbContext(typeof(CaseStateDbContext))]
[Migration("20261103130000_ManagedConversationCleanup")]
public sealed class ManagedConversationCleanup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var jsonType = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite"
            ? "TEXT"
            : "nvarchar(max)";
        migrationBuilder.AddColumn<string>(
            name: "CleanupClaimToken", schema: "dbo", table: "managed_conversations",
            type: "nvarchar(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "CleanupLeaseUntilTicks", schema: "dbo", table: "managed_conversations",
            type: "bigint", nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "CleanupAttempt1Ticks", schema: "dbo", table: "managed_conversations",
            type: "bigint", nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "CleanupAttempt2Ticks", schema: "dbo", table: "managed_conversations",
            type: "bigint", nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "CleanupAttempt3Ticks", schema: "dbo", table: "managed_conversations",
            type: "bigint", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "CleanupDeletedItemIdsJson", schema: "dbo", table: "managed_conversations",
            type: jsonType, nullable: false, defaultValue: "[]");
        migrationBuilder.AddColumn<string>(
            name: "CleanupDeletedResponseIdsJson", schema: "dbo", table: "managed_conversations",
            type: jsonType, nullable: false, defaultValue: "[]");
        migrationBuilder.AddColumn<bool>(
            name: "CleanupConversationDeleted", schema: "dbo", table: "managed_conversations",
            type: "bit", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<long>(
            name: "CleanupConfirmedAtTicks", schema: "dbo", table: "managed_conversations",
            type: "bigint", nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_managed_conversations_cleanup",
            schema: "dbo",
            table: "managed_conversations",
            columns: ["Status", "ExpiresAtTicks", "CleanupConfirmedAtTicks"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_managed_conversations_cleanup",
            schema: "dbo",
            table: "managed_conversations");
        if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            return;
        migrationBuilder.DropColumn(name: "CleanupClaimToken", schema: "dbo", table: "managed_conversations");
        migrationBuilder.DropColumn(name: "CleanupLeaseUntilTicks", schema: "dbo", table: "managed_conversations");
        migrationBuilder.DropColumn(name: "CleanupAttempt1Ticks", schema: "dbo", table: "managed_conversations");
        migrationBuilder.DropColumn(name: "CleanupAttempt2Ticks", schema: "dbo", table: "managed_conversations");
        migrationBuilder.DropColumn(name: "CleanupAttempt3Ticks", schema: "dbo", table: "managed_conversations");
        migrationBuilder.DropColumn(name: "CleanupDeletedItemIdsJson", schema: "dbo", table: "managed_conversations");
        migrationBuilder.DropColumn(name: "CleanupDeletedResponseIdsJson", schema: "dbo", table: "managed_conversations");
        migrationBuilder.DropColumn(name: "CleanupConversationDeleted", schema: "dbo", table: "managed_conversations");
        migrationBuilder.DropColumn(name: "CleanupConfirmedAtTicks", schema: "dbo", table: "managed_conversations");
    }
}
