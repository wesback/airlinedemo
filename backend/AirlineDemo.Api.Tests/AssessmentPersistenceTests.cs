using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Requests;

namespace AirlineDemo.Api.Tests;

public sealed class AssessmentPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "assessment-history-" + Guid.NewGuid().ToString("N"));
    private string PathFor => Path.Combine(_directory, "cases.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void AC005_ReloadPreservesExactCaseAndBasisIdentity()
    {
        var store = new CaseStateStore(PathFor);
        var scope = store.CreateCase("case-1", "owner-1");
        var otherScope = store.CreateCase("case-2", "owner-2");
        var assessment = new CaseAssessment("assessment-1", scope.CaseId, "basis-7", 7, "Request", "reason",
            [new("MissingRequirement", "oil sample", "Engine", "ENG-123", [new("doc-1", 9)])], "request-1", "work-1");
        var receipt = new DeliveryReceipt("event-1", "request-1", scope.CaseId, "report-1", "basis-7", 7, "template-3", "request body");
        store.Commit(scope, 0, new CaseCommit(null, receipt.EventId, "actor", "assessment", "saved",
            Assessment: assessment, RequestReceipt: receipt));
        var otherAssessment = assessment with { AssessmentId = "assessment-2", CaseId = otherScope.CaseId };
        store.Commit(otherScope, 0, new CaseCommit(null, null, "actor", "assessment", "saved", Assessment: otherAssessment));

        var reloaded = new CaseStateStore(PathFor);
        var stored = reloaded.FindAssessment(reloaded.AuthorizeCase("case-1", "owner-1"), assessment.AssessmentId)!;
        Assert.Equal(("assessment-1", "case-1", "basis-7", 7, "Request", "reason", "request-1", "work-1"),
            (stored.AssessmentId, stored.CaseId, stored.BasisId, stored.BasisVersion, stored.Outcome, stored.Reason, stored.RequestId, stored.WorkItemId));
        var claim = Assert.Single(stored.Claims);
        Assert.Equal(("MissingRequirement", "oil sample", "Engine", "ENG-123"), (claim.Kind, claim.Value, claim.Component, claim.SerialNumber));
        Assert.Equal(("doc-1", 9), (Assert.Single(claim.Witnesses).DocumentId, claim.Witnesses[0].Page));
        Assert.Equal(receipt, Assert.Single(reloaded.ListRequestReceipts(reloaded.AuthorizeCase("case-1", "owner-1"))));
        Assert.Null(reloaded.FindAssessment(reloaded.AuthorizeCase("case-2", "owner-2"), assessment.AssessmentId));
        Assert.Null(reloaded.FindAssessment(reloaded.AuthorizeCase("case-1", "owner-1"), otherAssessment.AssessmentId));
    }
}
