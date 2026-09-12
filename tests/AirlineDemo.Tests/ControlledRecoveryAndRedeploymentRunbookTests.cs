using System.Text.RegularExpressions;

namespace AirlineDemo.Tests;

public sealed class ControlledRecoveryAndRedeploymentRunbookTests
{
    [Fact]
    public void Runbook_DefinesCompleteVersionAndReceiptRecord()
    {
        var runbook = LoadText("deployment", "controlled-recovery-and-redeployment-runbook.md");
        var normalized = Normalize(runbook);

        Assert.Contains("Required version-and-receipt record", runbook,
            StringComparison.Ordinal);

        var requiredFields = new[]
        {
            "Terraform revision or plan reference",
            "Deployed image or Container Apps revision",
            "Fixture manifest hash",
            "Run identifier",
            "Migration version",
            "Smoke-test result",
            "Timestamp"
        };

        foreach (var field in requiredFields)
        {
            Assert.Contains($"| {field} |", runbook, StringComparison.Ordinal);
        }

        Assert.Contains("SHA-256", runbook, StringComparison.Ordinal);
        Assert.Contains("UTC ISO 8601", runbook, StringComparison.Ordinal);
        Assert.Contains("exact pass/fail result", runbook, StringComparison.Ordinal);
        Assert.Contains("The receipt must contain all of these fields", normalized,
            StringComparison.Ordinal);
        Assert.Contains("A missing field blocks closure", runbook,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Runbook_DefinesStatePreservingRollbackRedeployAndWorkerRestart()
    {
        var runbook = Normalize(LoadText(
            "deployment", "controlled-recovery-and-redeployment-runbook.md"));

        Assert.Contains("## Application rollback or redeploy", runbook,
            StringComparison.Ordinal);
        Assert.Contains("## State-safe worker restart", runbook,
            StringComparison.Ordinal);
        Assert.Contains("preserves existing workflow and business results",
            runbook, StringComparison.Ordinal);
        Assert.Contains("same authoritative SQL database and case-scoped evidence storage",
            runbook, StringComparison.Ordinal);
        Assert.Contains("before-change state snapshot", runbook,
            StringComparison.Ordinal);
        Assert.Contains("after-change state", runbook, StringComparison.Ordinal);
        Assert.Contains("`caseRevision` did not move unless an approved mutation occurred",
            runbook, StringComparison.Ordinal);
        Assert.Contains("existing idempotency key and canonical payload", runbook,
            StringComparison.Ordinal);
        Assert.Contains("prior immutable application revision", runbook,
            StringComparison.Ordinal);
        Assert.Contains("approved read-only smoke test", runbook,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Runbook_RejectsResetFabricationAndSilentDiscardOfBusinessResults()
    {
        var runbook = LoadText(
            "deployment", "controlled-recovery-and-redeployment-runbook.md");
        var normalized = Normalize(runbook);

        Assert.Contains("Do not use `terraform destroy`", normalized,
            StringComparison.Ordinal);
        Assert.Contains("drop or recreate the database", normalized,
            StringComparison.Ordinal);
        Assert.Contains("truncate or delete workflow/business tables", normalized,
            StringComparison.Ordinal);
        Assert.Contains("reset a run or case", normalized, StringComparison.Ordinal);
        Assert.Contains("fabricate a smoke-test result", normalized,
            StringComparison.Ordinal);
        Assert.Contains("silently discard", normalized, StringComparison.Ordinal);
        Assert.Contains("Do not recreate its database, evidence store, workflow records, or business-result records",
            normalized, StringComparison.Ordinal);

        Assert.DoesNotMatch(
            new Regex(
                @"(?im)^\s*(?:terraform\s+destroy|drop\s+database|truncate\s+table|delete\s+from|reset\s+(?:a\s+)?(?:run|case|database))\b"),
            runbook);
    }

    [Fact]
    public void Runbook_StatesBackupRestoreAndProductionRecoveryAreFutureWork()
    {
        var runbook = Normalize(LoadText(
            "deployment", "controlled-recovery-and-redeployment-runbook.md"));

        Assert.Contains("## Backup, restore, and disaster-recovery boundary", runbook,
            StringComparison.Ordinal);
        Assert.Contains("future design work", runbook, StringComparison.Ordinal);
        Assert.Contains("single-region demo has no claimed backup/restore or production disaster-recovery capability",
            runbook, StringComparison.Ordinal);
        Assert.Contains("Do not describe an unwritten restore rehearsal",
            runbook, StringComparison.Ordinal);
        Assert.Contains("regional failover as completed or supported", runbook,
            StringComparison.Ordinal);
        Assert.Contains("Do not substitute a new database, fixture data, logs, Durable history, or evaluator truth for a restore",
            runbook, StringComparison.Ordinal);
    }

    [Fact]
    public void Runbook_DoesNotClaimThatRecoveryOrDeploymentHasOccurred()
    {
        var runbook = Normalize(LoadText(
            "deployment", "controlled-recovery-and-redeployment-runbook.md"));

        Assert.Contains("documentation and evidence capture only", runbook,
            StringComparison.Ordinal);
        Assert.Contains("no deployment, rollback, restart, restore, or recovery execution is claimed",
            runbook, StringComparison.Ordinal);
        Assert.Contains("It is not an execution receipt", runbook,
            StringComparison.Ordinal);
        Assert.Contains("does not turn an unexecuted command into a successful deployment",
            runbook, StringComparison.Ordinal);
    }

    private static string LoadText(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(relativePath)}.");
    }

    private static string Normalize(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();
}
