using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

[DbContext(typeof(CaseStateDbContext))]
[Migration("20261016120000_AddAssessmentHistory")]
public class AddAssessmentHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "case_assessments",
            schema: "dbo",
            columns: table => new
            {
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                AssessmentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                BasisId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                BasisVersion = table.Column<int>(type: "int", nullable: false),
                Outcome = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Reason = table.Column<string>(nullable: false),
                RequestId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                WorkItemId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                ClaimsJson = table.Column<string>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_case_assessments", x => new { x.CaseId, x.AssessmentId });
                table.CheckConstraint("CK_case_assessments_BasisId", migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite"
                    ? "length(trim(BasisId)) > 0"
                    : "LEN(BasisId) > 0");
                table.CheckConstraint("CK_case_assessments_BasisVersion", "BasisVersion > 0");
            });

        migrationBuilder.CreateTable(
            name: "case_request_receipts",
            schema: "dbo",
            columns: table => new
            {
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                EventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                RequestId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ReportId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                BasisId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                BasisVersion = table.Column<int>(type: "int", nullable: false),
                TemplateVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Body = table.Column<string>(nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_case_request_receipts", x => new { x.CaseId, x.EventId }));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "case_request_receipts", schema: "dbo");
        migrationBuilder.DropTable(name: "case_assessments", schema: "dbo");
    }
}
