using System.Text.Json;

namespace AirlineDemo.Api.Tests;

public sealed class SupplyChainWorkflowTests
{
    private const string ExceptionsPath = "docs/security/dependency-audit-exceptions.json";

    [Theory]
    [InlineData("Restore and build .NET")]
    [InlineData("Test .NET")]
    [InlineData("Build and test React app")]
    [InlineData("SBOM (.NET)")]
    [InlineData("SBOM (npm)")]
    [InlineData("Audit (.NET)")]
    [InlineData("Audit (npm)")]
    [InlineData("Source restriction")]
    [InlineData("Secret scan")]
    [InlineData("Exception validation")]
    [InlineData("Severity gate")]
    public void Workflow_contains_required_step(string stepName)
    {
        var workflow = ReadWorkflow();

        Assert.Contains($"- name: {stepName}", workflow);
    }

    [Fact]
    public void Workflow_builds_and_tests_both_applications()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("dotnet build", StepBody(workflow, "Restore and build .NET"));
        Assert.Contains("dotnet test", StepBody(workflow, "Test .NET"));
        var react = StepBody(workflow, "Build and test React app");
        Assert.Contains("npm run build", react);
        Assert.Contains("npm test", react);
    }

    [Fact]
    public void SBOM_step_generates_dotnet_cyclonedx_output()
    {
        var step = StepBody(ReadWorkflow(), "SBOM (.NET)");

        Assert.Contains("dotnet CycloneDX", step);
        Assert.Contains("test -s artifacts/sbom/dotnet-sbom.cdx.json", step);
    }

    [Fact]
    public void SBOM_step_generates_npm_cyclonedx_output()
    {
        var step = StepBody(ReadWorkflow(), "SBOM (npm)");

        Assert.Contains("@cyclonedx/cyclonedx-npm", step);
        Assert.Contains("test -s ../artifacts/sbom/npm-sbom.cdx.json", step);
    }

    [Fact]
    public void Audit_step_runs_dotnet_vulnerable_listing_and_fails_on_missing_output()
    {
        var step = StepBody(ReadWorkflow(), "Audit (.NET)");

        Assert.Contains("dotnet list", step);
        Assert.Contains("package --vulnerable", step);
        Assert.Contains("test -s artifacts/audit/dotnet-vulnerable.txt", step);
    }

    [Fact]
    public void Audit_step_runs_npm_audit_and_fails_on_missing_output()
    {
        var step = StepBody(ReadWorkflow(), "Audit (npm)");

        Assert.Contains("npm audit --json", step);
        Assert.Contains("test -s ../artifacts/audit/npm-audit.json", step);
    }

    [Fact]
    public void Severity_gate_blocks_unexcepted_high_and_critical_findings()
    {
        var step = StepBody(ReadWorkflow(), "Severity gate");

        Assert.Contains("'high'", step);
        Assert.Contains("'critical'", step);
        Assert.Contains("(High|Critical)", step);
        Assert.Contains("process.exit(1)", step);
        Assert.Contains(ExceptionsPath, step);
    }

    [Fact]
    public void Exception_validation_rejects_missing_expired_or_incomplete_exceptions()
    {
        var step = StepBody(ReadWorkflow(), "Exception validation");

        Assert.Contains(ExceptionsPath, step);
        Assert.Contains("'reason'", step);
        Assert.Contains("'expires'", step);
        Assert.Contains("expired on", step);
        Assert.Contains("process.exit(1)", step);
    }

    [Fact]
    public void Exceptions_file_is_a_valid_exceptions_list()
    {
        var root = FindRepoRoot();

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ExceptionsPath)));

        Assert.True(document.RootElement.TryGetProperty("exceptions", out var exceptions));
        Assert.Equal(JsonValueKind.Array, exceptions.ValueKind);
    }

    [Fact]
    public void Source_restriction_allows_only_nuget_org_and_npmjs_org()
    {
        var step = StepBody(ReadWorkflow(), "Source restriction");

        Assert.Contains("dotnet nuget list source", step);
        Assert.Contains("api\\.nuget\\.org/v3/index\\.json", step);
        Assert.Contains("registry\\.npmjs\\.org", step);
        Assert.Contains("exit 1", step);
    }

    [Fact]
    public void Secret_scan_checks_tracked_content_and_fails_on_matches()
    {
        var step = StepBody(ReadWorkflow(), "Secret scan");

        Assert.Contains("git grep", step);
        Assert.Contains("AccountKey", step);
        Assert.Contains("Server=", step);
        Assert.Contains("exit 1", step);
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

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root with .github/workflows/build.yml not found.");
    }
}
