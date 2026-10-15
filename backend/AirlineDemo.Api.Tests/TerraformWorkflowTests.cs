using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Tests;

public sealed class TerraformWorkflowTests
{
    private const string StepName = "Terraform mock plan checks";
    private const string SetupStepName = "Setup Terraform";

    // TERRAFORM-CI-01: the build gate pins Terraform and runs backend-free checks in order.
    [Fact]
    public void Terraform_workflow_runs_mock_plan_checks()
    {
        var workflow = ReadWorkflow();
        var job = JobBody(workflow, "build-and-gate");
        var setup = StepBody(job, SetupStepName);
        var checks = StepBody(job, StepName);

        Assert.Contains("hashicorp/setup-terraform@", setup);
        Assert.Contains("terraform_version: 1.9.8", setup);
        Assert.Contains("working-directory: infra/terraform", checks);

        var commands = new[]
        {
            "terraform fmt -check -recursive",
            "terraform init -backend=false",
            "terraform validate",
            "terraform test",
        };
        var positions = commands.Select(command => checks.IndexOf(command, StringComparison.Ordinal)).ToArray();
        foreach (var (command, position) in commands.Zip(positions))
        {
            Assert.True(position >= 0, $"Terraform check '{command}' is missing from the '{StepName}' step.");
        }
        Assert.Equal(positions.OrderBy(p => p), positions);

        Assert.DoesNotContain("-backend-config", workflow);
        Assert.DoesNotContain("terraform apply", workflow);

        foreach (var fileName in new[] { "identity-telemetry.tftest.hcl", "network-boundaries.tftest.hcl" })
        {
            var path = Path.Combine(FindRepoRoot(), "infra", "terraform", "tests", fileName);
            Assert.Contains("mock_provider \"azurerm\"", File.ReadAllText(path));
        }
    }

    private static string ReadWorkflow() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "build.yml"));

    private static string JobBody(string workflow, string jobName)
    {
        var header = $"\n  {jobName}:";
        var start = workflow.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Workflow job '{jobName}' is missing.");

        var next = Regex.Match(workflow[(start + header.Length)..], @"\n  [A-Za-z0-9_-]+:\s*\n");
        return next.Success ? workflow[start..(start + header.Length + next.Index)] : workflow[start..];
    }

    private static string StepBody(string job, string stepName)
    {
        var header = $"- name: {stepName}";
        var start = job.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Workflow step '{stepName}' is missing from the job.");

        var next = job.IndexOf("\n      - ", start + header.Length, StringComparison.Ordinal);
        return next < 0 ? job[start..] : job[start..next];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".github", "workflows", "build.yml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
