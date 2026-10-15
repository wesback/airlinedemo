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
        var setup = StepBody(workflow, SetupStepName);
        var checks = StepBody(workflow, StepName);

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
    }

    [Theory]
    [InlineData("identity-telemetry.tftest.hcl")]
    [InlineData("network-boundaries.tftest.hcl")]
    public void Terraform_test_files_use_mock_azurerm_provider(string fileName)
    {
        var path = Path.Combine(FindRepoRoot(), "infra", "terraform", "tests", fileName);

        Assert.Contains("mock_provider \"azurerm\"", File.ReadAllText(path));
    }

    private static string ReadWorkflow() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "build.yml"));

    private static string StepBody(string workflow, string stepName)
    {
        var header = $"- name: {stepName}";
        var start = workflow.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Workflow step '{stepName}' is missing.");

        var next = workflow.IndexOf("\n      - ", start + header.Length, StringComparison.Ordinal);
        return next < 0 ? workflow[start..] : workflow[start..next];
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
