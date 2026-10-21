using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AirlineDemo.Api.Persistence.Migrations;

[DbContext(typeof(CaseStateDbContext))]
[Migration("20261020120000_CasePackResetReceipts")]
public class CasePackResetReceipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var isSqlite = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite";
        migrationBuilder.CreateTable(
            name: "case_pack_reset_receipts",
            schema: "dbo",
            columns: table => new
            {
                CommandIdHash = table.Column<byte[]>(
                    type: isSqlite ? "BLOB" : "varbinary(32)", maxLength: 32, nullable: false),
                CommandId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                ExpectedRevision = table.Column<long>(type: "bigint", nullable: false),
                Variant = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                PriorRevision = table.Column<long>(type: "bigint", nullable: false),
                NewRevision = table.Column<long>(type: "bigint", nullable: false),
                NewRunId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                AuditEventId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_case_pack_reset_receipts", x => x.CommandIdHash);
                table.CheckConstraint("CK_case_pack_reset_receipts_Revision", "ExpectedRevision >= 0 AND PriorRevision = ExpectedRevision AND NewRevision = PriorRevision + 1");
                table.CheckConstraint("CK_case_pack_reset_receipts_Variant", "Variant = 'blocked'");
            });
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "case_pack_reset_receipts", schema: "dbo");
}
