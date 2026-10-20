using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

[DbContext(typeof(CaseStateDbContext))]
[Migration("20261019120000_AddCaseHistory")]
public class AddCaseHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var maxTextType = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite"
            ? "TEXT"
            : "nvarchar(max)";

        migrationBuilder.CreateTable(
            name: "case_journey_events",
            schema: "dbo",
            columns: table => new
            {
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                EventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                RunId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                EventType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                BasisId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                BasisVersion = table.Column<int>(type: "int", nullable: true),
                ActorRole = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Summary = table.Column<string>(type: maxTextType, nullable: false),
                CapabilityLabel = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                WitnessesJson = table.Column<string>(type: maxTextType, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_case_journey_events", x => new { x.CaseId, x.Sequence });
                table.CheckConstraint("CK_case_journey_events_Sequence", "Sequence > 0");
                table.CheckConstraint("CK_case_journey_events_BasisIdentity",
                    migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite"
                        ? "(BasisId IS NULL AND BasisVersion IS NULL) OR (BasisId IS NOT NULL AND length(trim(BasisId)) > 0 AND BasisVersion > 0)"
                        : "(BasisId IS NULL AND BasisVersion IS NULL) OR (BasisId IS NOT NULL AND LEN(BasisId) > 0 AND BasisVersion > 0)");
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "case_journey_events", schema: "dbo");
}
