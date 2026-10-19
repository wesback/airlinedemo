using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;
[DbContext(typeof(CaseStateDbContext))]
[Migration("20261017120000_SourceReadWindows")]
public class SourceReadWindows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "source_read_windows",
            schema: "dbo",
            columns: table => new
            {
                LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CaseHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                SessionHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                RequestedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                ExpiresAtTicks = table.Column<long>(type: "bigint", nullable: false),
                ReleasedAtTicks = table.Column<long>(type: "bigint", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_source_read_windows", x => x.LeaseId));
        migrationBuilder.CreateIndex("IX_source_read_windows_session_window", "source_read_windows",
            new[] { "CaseHash", "SessionHash", "RequestedAtTicks" }, schema: "dbo");
        migrationBuilder.CreateIndex("IX_source_read_windows_case_lease", "source_read_windows",
            new[] { "CaseHash", "ReleasedAtTicks", "ExpiresAtTicks" }, schema: "dbo");
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            migrationBuilder.Sql("INSERT INTO dbo.source_read_windows (LeaseId, CaseHash, SessionHash, RequestedAtTicks, ExpiresAtTicks, ReleasedAtTicks) VALUES ('00000000-0000-0000-0000-000000000000', CONVERT(binary(32), 0x), CONVERT(binary(32), 0x), 0, 0, 0);");
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "source_read_windows", schema: "dbo");
}
