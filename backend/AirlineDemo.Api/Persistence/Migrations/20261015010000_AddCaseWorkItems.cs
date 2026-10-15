using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

/// <summary>
/// Adds the persisted review work-item source. Run under the migration identity only.
/// </summary>
[DbContext(typeof(CaseStateDbContext))]
[Migration("20261015010000_AddCaseWorkItems")]
public class AddCaseWorkItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "case_work_items",
            schema: "dbo",
            columns: table => new
            {
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ItemId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                CapabilityId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                State = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Owner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_case_work_items", x => new { x.CaseId, x.ItemId }));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "case_work_items", schema: "dbo");
    }
}
