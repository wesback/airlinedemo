using System.Diagnostics;

namespace AirlineDemo.Tests;

public sealed class DemoBootstrapScriptTests
{
    [Fact]
    public async Task BootstrapDryRunExecutesTheCompleteOrderedFlowAndPrintsEndpoints()
    {
        var result = await RunScriptAsync(
            "scripts/bootstrap-demo.sh",
            ("AIRLINEDEMO_DRY_RUN", "1"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("DEMO_BOOTSTRAP_SUCCESS", result.Output, StringComparison.Ordinal);
        Assert.Contains("resource_group=", result.Output, StringComparison.Ordinal);
        Assert.Contains("app_endpoint=https://<container-app-fqdn>", result.Output,
            StringComparison.Ordinal);
        Assert.Contains("fixture_root=", result.Output, StringComparison.Ordinal);
        AssertOrdered(
            result.Output,
            "terraform-registry",
            "image-build",
            "image-push",
            "terraform-apply",
            "migrate",
            "fixtures",
            "smoke",
            "DEMO_BOOTSTRAP_SUCCESS");
    }

    [Fact]
    public async Task BootstrapReportsMissingPrivateConfigurationAndSimulatedFailures()
    {
        var missing = await RunScriptAsync(
            "scripts/bootstrap-demo.sh",
            ("AIRLINEDEMO_DRY_RUN", "0"),
            ("AIRLINEDEMO_OPERATOR_VARS_FILE", ""));
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("AIRLINEDEMO_OPERATOR_VARS_FILE is required", missing.Output,
            StringComparison.Ordinal);

        var failed = await RunScriptAsync(
            "scripts/bootstrap-demo.sh",
            ("AIRLINEDEMO_DRY_RUN", "1"),
            ("AIRLINEDEMO_FAIL_STEP", "migrate"));
        Assert.NotEqual(0, failed.ExitCode);
        Assert.Contains("step 'migrate' failed", failed.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LifecycleScriptsPrintStructuredDryRunSuccessSummaries()
    {
        foreach (var scriptAndMarker in new[]
        {
            ("scripts/migrate-demo.sh", "DEMO_MIGRATION_SUCCESS"),
            ("scripts/load-demo-fixtures.sh", "DEMO_FIXTURES_SUCCESS"),
            ("scripts/destroy-demo.sh", "DEMO_DESTROY_SUCCESS")
        })
        {
            var result = await RunScriptAsync(
                scriptAndMarker.Item1,
                ("AIRLINEDEMO_DRY_RUN", "1"));

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(scriptAndMarker.Item2, result.Output, StringComparison.Ordinal);
            Assert.Contains("resource_group=", result.Output, StringComparison.Ordinal);
            Assert.Contains("app_endpoint=", result.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LifecycleScriptsReportSimulatedStepFailures()
    {
        var cases = new[]
        {
            ("scripts/migrate-demo.sh", "migrate"),
            ("scripts/load-demo-fixtures.sh", "fixtures"),
            ("scripts/destroy-demo.sh", "terraform-destroy")
        };

        foreach (var (script, step) in cases)
        {
            var result = await RunScriptAsync(
                script,
                ("AIRLINEDEMO_DRY_RUN", "1"),
                ("AIRLINEDEMO_FAIL_STEP", step));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains($"step '{step}' failed", result.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GettingStartedIsSelfContainedForTheLifecycleCommands()
    {
        var document = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "GETTING-STARTED.md"));

        foreach (var command in new[]
        {
            "dotnet run --project src/AirlineDemo.Generator",
            "./scripts/bootstrap-demo.sh",
            "npm run lint:demo-scripts",
            "npm run validate:terraform",
            "dotnet test AirlineDemo.slnx",
            "./scripts/destroy-demo.sh"
        })
        {
            Assert.Contains(command, document, StringComparison.Ordinal);
        }

        Assert.Contains("DEMO_BOOTSTRAP_SUCCESS", document, StringComparison.Ordinal);
        Assert.Contains("DEMO_DESTROY_SUCCESS", document, StringComparison.Ordinal);
        Assert.DoesNotContain("see deployment/", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("consult deployment/", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a production deployment", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("real partners", document, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DestroyScriptRequiresExplicitConfirmationAndProtectsStateBoundary()
    {
        var script = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "scripts", "destroy-demo.sh"));

        Assert.Contains("DELETE AIRLINEDEMO DEMO", script, StringComparison.Ordinal);
        Assert.Contains("read -r confirmation", script, StringComparison.Ordinal);
        Assert.Contains("terraform-destroy-plan", script, StringComparison.Ordinal);
        Assert.Contains("terraform-destroy", script, StringComparison.Ordinal);
        Assert.DoesNotContain("az group delete", script, StringComparison.Ordinal);
        Assert.DoesNotContain("terraform/bootstrap", script, StringComparison.Ordinal);
        Assert.Contains("protected-remote-state", script, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunScriptAsync(
        string relativePath,
        params (string Name, string Value)[] variables)
    {
        var root = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(Path.Combine(root, relativePath));
        foreach (var (name, value) in variables)
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start demo lifecycle script.");
        var output = await process.StandardOutput.ReadToEndAsync();
        output += await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output);
    }

    private static void AssertOrdered(string text, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{marker}' after the prior lifecycle step.");
            previous = current;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "AirlineDemo.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
