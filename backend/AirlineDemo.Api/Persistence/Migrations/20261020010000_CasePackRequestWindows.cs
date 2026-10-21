using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

[DbContext(typeof(CaseStateDbContext))]
[Migration("20261020010000_CasePackRequestWindows")]
public class CasePackRequestWindows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var isSqlite = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite";
        migrationBuilder.CreateTable(
            name: "case_pack_request_windows",
            schema: "dbo",
            columns: table => new
            {
                AttemptId = table.Column<Guid>(type: isSqlite ? "TEXT" : "uniqueidentifier", nullable: false),
                UserHash = table.Column<byte[]>(type: isSqlite ? "BLOB" : "binary(32)", maxLength: 32, nullable: false),
                Endpoint = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                RequestedAtTicks = table.Column<long>(type: "bigint", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_case_pack_request_windows", x => x.AttemptId));
        migrationBuilder.CreateIndex(
            name: "IX_case_pack_request_windows_user_endpoint_window",
            schema: "dbo",
            table: "case_pack_request_windows",
            columns: new[] { "UserHash", "Endpoint", "RequestedAtTicks" });
        migrationBuilder.Sql(isSqlite
            ? "INSERT INTO \"case_pack_request_windows\" (\"AttemptId\", \"UserHash\", \"Endpoint\", \"RequestedAtTicks\") VALUES ('00000000-0000-0000-0000-000000000000', zeroblob(32), '', 0);"
            : "INSERT INTO dbo.case_pack_request_windows (AttemptId, UserHash, Endpoint, RequestedAtTicks) VALUES ('00000000-0000-0000-0000-000000000000', CONVERT(binary(32), 0x), N'', 0);");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "case_pack_request_windows", schema: "dbo");
}
