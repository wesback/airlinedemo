using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AirlineDemo.Api.Persistence.Migrations;

[DbContext(typeof(CaseStateDbContext))]
[Migration("20261016120000_AddAssessmentHistory")]
public sealed class AddAssessmentHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var nonEmptyBasis = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer"
            ? "LEN(LTRIM(RTRIM([BasisId]))) > 0"
            : "length(trim(BasisId)) > 0";
        migrationBuilder.CreateTable(
            name: "case_assessments",
            schema: "dbo",
            columns: table => new
            {
                CaseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                AssessmentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                BasisId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                BasisVersion = table.Column<int>(type: "int", nullable: false),
                Payload = table.Column<string>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_case_assessments", x => new { x.CaseId, x.AssessmentId });
                table.CheckConstraint("CK_case_assessments_basis_id", nonEmptyBasis);
                table.CheckConstraint("CK_case_assessments_basis_version", "BasisVersion > 0");
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
                TemplateVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Body = table.Column<string>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_case_request_receipts", x => new { x.CaseId, x.EventId });
            });
        migrationBuilder.CreateIndex(
            name: "IX_case_request_receipts_CaseId_RequestId",
            schema: "dbo",
            table: "case_request_receipts",
            columns: new[] { "CaseId", "RequestId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "case_request_receipts", schema: "dbo");
        migrationBuilder.DropTable(name: "case_assessments", schema: "dbo");
    }
}
