namespace AirlineDemo.Tests;

public sealed class DemoSmokeRunbookTests
{
    [Fact]
    public void RunbookDefinesSelectedManifestLoadAndProtectedArtifactBoundaries()
    {
        var runbook = ReadRunbook();

        Assert.Contains("configuration.runId", runbook, StringComparison.Ordinal);
        Assert.Contains(
            "pathBoundaries.selectedInitialInputManifest.entries",
            runbook,
            StringComparison.Ordinal);
        Assert.Contains("application-inputs/package-001/manifest.json", runbook, StringComparison.Ordinal);
        Assert.Contains("requirements.json", runbook, StringComparison.Ordinal);
        Assert.Contains("SHA-256", runbook, StringComparison.Ordinal);
        Assert.Contains("application-inputs/*", runbook, StringComparison.Ordinal);
        Assert.Contains("all(. as $document", runbook, StringComparison.Ordinal);
        Assert.Contains("endswith($document.fileName)", runbook, StringComparison.Ordinal);
        Assert.Contains("find \"$APP_DOCUMENT_ROOT/application-inputs/package-001\"", runbook, StringComparison.Ordinal);
        Assert.Contains("diff -u \"$LOAD_ROOT/declared-package-files.txt\"", runbook, StringComparison.Ordinal);
        Assert.Contains("undeclared file", runbook, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not copy the whole generator output directory", runbook, StringComparison.Ordinal);
        Assert.Contains("staged-responses/", runbook, StringComparison.Ordinal);
        Assert.Contains("evaluator-only/", runbook, StringComparison.Ordinal);
        Assert.Contains("replay-only/", runbook, StringComparison.Ordinal);
        Assert.Contains("answer keys", runbook, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never installed as initial application storage", runbook, StringComparison.Ordinal);
        Assert.Contains("does not read", runbook, StringComparison.Ordinal);
        Assert.Contains("`replay-only/events.json`", runbook, StringComparison.Ordinal);
    }

    [Fact]
    public void RunbookSeparatesReviewerAndMockPartnerAuthenticationAndNamesAuthoritativeOutcomes()
    {
        var runbook = ReadRunbook();

        Assert.Contains("## 4. Reviewer authentication and authoritative decision smoke check", runbook);
        Assert.Contains("## 5. Mock-partner authentication and scoped response smoke check", runbook);
        Assert.Contains("subject=reviewer-001", runbook, StringComparison.Ordinal);
        Assert.Contains("subject=mock-partner", runbook, StringComparison.Ordinal);
        Assert.Contains("export REVIEWER_AUTH=\"Bearer ", runbook, StringComparison.Ordinal);
        Assert.Contains("export MOCK_PARTNER_AUTH=\"Bearer ", runbook, StringComparison.Ordinal);
        Assert.Contains("/api/packages", runbook, StringComparison.Ordinal);
        Assert.Contains("/api/cases/$CASE_ID/reviews", runbook, StringComparison.Ordinal);
        Assert.Contains("/api/cases/$CASE_ID\"", runbook, StringComparison.Ordinal);
        Assert.Contains("/api/mock-inbox", runbook, StringComparison.Ordinal);
        Assert.Contains("/api/partner-responses", runbook, StringComparison.Ordinal);
        Assert.Contains("HTTP `202`", runbook);
        Assert.Contains("`operationId`, `caseId`, and", runbook);
        Assert.Contains("`receiptId`", runbook);
        Assert.Contains("server-issued", runbook, StringComparison.Ordinal);
        Assert.Contains("`reviewId`", runbook, StringComparison.Ordinal);
        Assert.Contains("`receiptId`", runbook, StringComparison.Ordinal);
        Assert.Contains("caseRevision", runbook, StringComparison.Ordinal);
        Assert.Contains("caseRevision > ($revision | tonumber)", runbook, StringComparison.Ordinal);
        Assert.Contains("inboxItemId", runbook, StringComparison.Ordinal);
        Assert.Contains("recipientRef` equal to", runbook, StringComparison.Ordinal);
        Assert.Contains("queue reassessment", runbook, StringComparison.Ordinal);
    }

    [Fact]
    public void RunbookRequiresOperatorRecordAndForbidsPreExecutionSuccessClaims()
    {
        var runbook = ReadRunbook();
        var recordStart = runbook.IndexOf("## 6. Operator record", StringComparison.Ordinal);
        Assert.True(recordStart >= 0);
        var record = runbook[recordStart..];

        foreach (var field in new[]
        {
            "runIdentifier:",
            "selectedManifestSha256:",
            "deployedImageOrRevision:",
            "smokeOutcome:"
        })
        {
            Assert.Contains(field, record, StringComparison.Ordinal);
            Assert.Contains(
                $"{field} PENDING_OPERATOR_EXECUTION",
                record,
                StringComparison.Ordinal);
        }

        Assert.Contains("Before executing this", record, StringComparison.Ordinal);
        Assert.Contains(
            "every result field must remain `PENDING_OPERATOR_EXECUTION`",
            record,
            StringComparison.Ordinal);
        Assert.Contains("never", record, StringComparison.Ordinal);
        Assert.Contains("pre-fill `PASS`", record, StringComparison.Ordinal);
        Assert.Contains("Do not record a successful outcome", record, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "smokeOutcome: PASS",
            record,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "smokeOutcome: complete",
            record,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deployment image digest", runbook, StringComparison.Ordinal);
        Assert.Contains("Container Apps revision identifier", runbook, StringComparison.Ordinal);
    }

    private static string ReadRunbook()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            (!File.Exists(Path.Combine(directory.FullName, "AirlineDemo.slnx")) ||
             !Directory.Exists(Path.Combine(directory.FullName, "deployment"))))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(
            Path.Combine(directory!.FullName, "deployment", "demo-smoke-runbook.md"));
    }
}
