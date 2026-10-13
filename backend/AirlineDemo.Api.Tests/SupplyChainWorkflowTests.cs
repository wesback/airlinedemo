using System.Diagnostics;
using System.Globalization;
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

    // M-12: build and test both applications.
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

    // M-12: CycloneDX SBOM for each dependency set.
    [Fact]
    public void SBOM_step_generates_dotnet_cyclonedx_output()
    {
        var step = StepBody(ReadWorkflow(), "SBOM (.NET)");

        Assert.Contains("dotnet-CycloneDX AirlineDemo.sln", step);
        Assert.DoesNotContain("dotnet CycloneDX", step);
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

    // M-12: an unexcepted high/critical finding blocks the release candidate.
    [Fact]
    public void M12_gate_blocks_unexcepted_npm_high_finding()
    {
        var (exitCode, _) = RunSeverityGate(NoExceptions, NpmAudit("high"), DotnetListing(null));

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void M12_gate_blocks_unexcepted_npm_critical_finding()
    {
        var (exitCode, _) = RunSeverityGate(NoExceptions, NpmAudit("critical"), DotnetListing(null));

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void M12_gate_blocks_unexcepted_dotnet_high_finding()
    {
        var (exitCode, _) = RunSeverityGate(NoExceptions, NpmAudit(null), DotnetListing("High"));

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void M12_gate_allows_low_severity_findings_without_exception()
    {
        var (exitCode, _) = RunSeverityGate(NoExceptions, NpmAudit("low"), DotnetListing(null));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void M12_gate_fails_when_npm_audit_output_reports_an_error()
    {
        var (exitCode, _) = RunSeverityGate(NoExceptions, "{\"error\":{\"code\":\"ENOTFOUND\",\"summary\":\"offline\"}}", DotnetListing(null));

        Assert.NotEqual(0, exitCode);
    }

    // Exceptions: accepted only with a documented reason and a future expiry.
    [Fact]
    public void Exception_with_future_expiry_allows_matching_npm_high_finding()
    {
        var (exitCode, _) = RunSeverityGate(Exception("pkg-a", FutureDate), NpmAudit("high"), DotnetListing(null));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Expired_exception_does_not_allow_matching_finding()
    {
        var (exitCode, _) = RunSeverityGate(Exception("pkg-a", PastDate), NpmAudit("high"), DotnetListing(null));

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Exception_expiring_today_does_not_allow_matching_finding()
    {
        var (exitCode, _) = RunSeverityGate(Exception("pkg-a", TodayUtc), NpmAudit("high"), DotnetListing(null));

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Exception_for_other_package_does_not_allow_finding()
    {
        var (exitCode, _) = RunSeverityGate(Exception("other-pkg", FutureDate), NpmAudit("high"), DotnetListing(null));

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Exception_validation_accepts_complete_exception_with_future_expiry()
    {
        var (exitCode, _) = RunExceptionValidation(Exception("pkg-a", FutureDate));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Exception_validation_rejects_impossible_calendar_date()
    {
        var (exitCode, output) = RunExceptionValidation(Exception("pkg-a", "2999-02-30"));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("real calendar date", output);
    }

    [Fact]
    public void Exception_validation_rejects_non_date_expiry()
    {
        var (exitCode, _) = RunExceptionValidation(Exception("pkg-a", "next-year"));

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Exception_validation_rejects_expired_exception()
    {
        var (exitCode, output) = RunExceptionValidation(Exception("pkg-a", PastDate));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("expired on", output);
    }

    [Fact]
    public void Exception_validation_rejects_exception_expiring_today()
    {
        var (exitCode, output) = RunExceptionValidation(Exception("pkg-a", TodayUtc));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("expired on", output);
    }

    [Fact]
    public void Exception_validation_rejects_missing_reason()
    {
        var (exitCode, output) = RunExceptionValidation("{\"exceptions\":[{\"package\":\"pkg-a\",\"expires\":\"" + FutureDate + "\"}]}");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("missing reason", output);
    }

    [Fact]
    public void Exception_validation_rejects_missing_expiry()
    {
        var (exitCode, output) = RunExceptionValidation("{\"exceptions\":[{\"package\":\"pkg-a\",\"reason\":\"Tracked in ADR-7\"}]}");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("real calendar date", output);
    }

    [Fact]
    public void Exception_validation_rejects_non_array_exceptions()
    {
        var (exitCode, _) = RunExceptionValidation("{\"exceptions\":{}}");

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Exceptions_file_is_a_valid_exceptions_list()
    {
        var root = FindRepoRoot();

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ExceptionsPath)));

        Assert.True(document.RootElement.TryGetProperty("exceptions", out var exceptions));
        Assert.Equal(JsonValueKind.Array, exceptions.ValueKind);
    }

    // M-14: package sources restricted to nuget.org and npmjs.org.
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
    public void Source_restriction_accepts_nuget_org_and_npmjs_org_only()
    {
        var (exitCode, output) = RunShellStep("Source restriction", repo => repo.Write(LockPath, NpmjsOnlyLock), NugetOrgListing);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("outside", output);
    }

    [Fact]
    public void Source_restriction_rejects_unapproved_nuget_source()
    {
        var listing = NugetOrgListing + "  2.  internal [Enabled]\n      https://feeds.example.test/v3/index.json\n";

        var (exitCode, output) = RunShellStep("Source restriction", repo => repo.Write(LockPath, NpmjsOnlyLock), listing);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("NuGet source outside nuget.org", output);
    }

    [Fact]
    public void Source_restriction_rejects_enabled_local_folder_nuget_source()
    {
        var listing = NugetOrgListing + "  2.  local [Enabled]\n      /opt/feeds/local-packages\n";

        var (exitCode, output) = RunShellStep("Source restriction", repo => repo.Write(LockPath, NpmjsOnlyLock), listing);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("NuGet source outside nuget.org", output);
    }

    [Fact]
    public void Source_restriction_ignores_disabled_nuget_source()
    {
        var listing = NugetOrgListing + "  2.  local [Disabled]\n      /opt/feeds/local-packages\n";

        var (exitCode, _) = RunShellStep("Source restriction", repo => repo.Write(LockPath, NpmjsOnlyLock), listing);

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("git+ssh://git@github.com/example/evil.git#abc123")]
    [InlineData("file:../local-package")]
    [InlineData("http://feeds.example.test/evil-1.0.0.tgz")]
    public void Source_restriction_rejects_non_npmjs_npm_sources(string resolved)
    {
        var lockfile = "{\"packages\":{\"node_modules/evil\":{\"resolved\": \"" + resolved + "\"}}}\n";

        var (exitCode, output) = RunShellStep("Source restriction", repo => repo.Write(LockPath, lockfile), NugetOrgListing);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("npm package resolved outside npmjs.org", output);
    }

    [Fact]
    public void Source_restriction_rejects_npm_package_resolved_outside_npmjs()
    {
        var lockfile = "{\"packages\":{\"node_modules/react\":{\"resolved\": \"https://registry.npmjs.org/react/-/react-18.2.0.tgz\"},"
            + "\"node_modules/evil\":{\"resolved\": \"https://feeds.example.test/evil-1.0.0.tgz\"}}}\n";

        var (exitCode, output) = RunShellStep("Source restriction", repo => repo.Write(LockPath, lockfile), NugetOrgListing);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("npm package resolved outside npmjs.org", output);
    }

    [Fact]
    public void Source_restriction_rejects_lockfile_without_npmjs_packages()
    {
        var (exitCode, _) = RunShellStep("Source restriction", repo => repo.Write(LockPath, "{\"packages\":{}}\n"), NugetOrgListing);

        Assert.NotEqual(0, exitCode);
    }

    // M-14: tracked repository content is scanned for committed credentials.
    [Fact]
    public void Secret_scan_checks_tracked_content_and_fails_on_matches()
    {
        var step = StepBody(ReadWorkflow(), "Secret scan");

        Assert.Contains("git grep", step);
        Assert.Contains("AccountKey", step);
        Assert.Contains("Server=", step);
        Assert.Contains("exit 1", step);
    }

    // The scan must cover tracked lockfiles too, so credentials cannot hide in frontend/package-lock.json.
    [Fact]
    public void Secret_scan_passes_on_clean_tracked_content()
    {
        var (exitCode, _) = RunShellStep("Secret scan", repo =>
        {
            repo.Write(LockPath, NpmjsOnlyLock);
            GitTrackAll(repo.Root);
        });

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Secret_scan_fails_on_credential_in_tracked_package_lock()
    {
        var (exitCode, output) = RunShellStep("Secret scan", repo =>
        {
            repo.Write(LockPath, "{\"resolved\": \"https://" + "deploy:tokenvalue@feeds.example.test/x.tgz\"}\n");
            GitTrackAll(repo.Root);
        });

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Committed credential", output);
    }

    // M-14: committed credentials, including URI-form connection strings, fail the secret scan.
    // Credential fixtures are split so this file never contains a full credential that the secret scan would flag.
    [Theory]
    [InlineData("postgres://" + "admin:s3cret-pass@db.internal/app")]
    [InlineData("https://" + "deploy:tokenvalue@feeds.example.com/index.json")]
    [InlineData("Server=tcp:db.internal;Database=app;User=sa;" + "Pass" + "word=hunter2222")]
    [InlineData("DefaultEndpointsProtocol=https;" + "Account" + "Key=abcdefgh12345678")]
    public void M14_secret_scan_fails_on_committed_credentials(string content)
    {
        Assert.True(SecretPatternMatches(content));
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json")]
    [InlineData("https://registry.npmjs.org/react/-/react-18.2.0.tgz")]
    [InlineData("git@github.com:owner/repo.git")]
    public void M14_secret_scan_ignores_plain_urls(string content)
    {
        Assert.False(SecretPatternMatches(content));
    }

    private static string NoExceptions => "{\"exceptions\":[]}";

    private static string FutureDate => "2999-12-31";

    private static string PastDate => "2000-01-01";

    private static string TodayUtc => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private const string LockPath = "frontend/package-lock.json";

    private const string NpmjsOnlyLock = "{\"packages\":{\"node_modules/react\":{\"resolved\": \"https://registry.npmjs.org/react/-/react-18.2.0.tgz\"}}}\n";

    private const string NugetOrgListing = "Registered Sources:\n\n  1.  nuget.org [Enabled]\n      https://api.nuget.org/v3/index.json\n";

    private static string Exception(string package, string expires) =>
        "{\"exceptions\":[{\"package\":\"" + package + "\",\"reason\":\"Tracked in ADR-7\",\"expires\":\"" + expires + "\"}]}";

    private static string NpmAudit(string? severity) =>
        severity is null
            ? "{\"metadata\":{},\"vulnerabilities\":{}}"
            : "{\"metadata\":{},\"vulnerabilities\":{\"pkg-a\":{\"severity\":\"" + severity + "\"}}}";

    private static string DotnetListing(string? severity) =>
        severity is null
            ? "Project 'AirlineDemo.Api' has no vulnerable packages.\n"
            : "Project 'AirlineDemo.Api' has the following vulnerable packages\n   [net10.0]:\n   > Foo.Bar  1.0.0  1.0.0  " + severity + "  https://example.test/advisory\n";

    private static (int ExitCode, string Output) RunExceptionValidation(string exceptions) =>
        RunStep("Exception validation", repo => repo.Write(ExceptionsPath, exceptions));

    private static (int ExitCode, string Output) RunSeverityGate(string exceptions, string npmAudit, string dotnetListing) =>
        RunStep("Severity gate", repo =>
        {
            repo.Write(ExceptionsPath, exceptions);
            repo.Write("artifacts/audit/npm-audit.json", npmAudit);
            repo.Write("artifacts/audit/dotnet-vulnerable.txt", dotnetListing);
        });

    private static (int ExitCode, string Output) RunStep(string stepName, Action<TempRepo> arrange)
    {
        var script = ExtractHeredoc(StepBody(ReadWorkflow(), stepName));
        using var repo = new TempRepo();
        arrange(repo);
        return RunNode(script, repo.Root);
    }

    // Runs a workflow shell step's run block in a temp repo. A fake `dotnet` on PATH prints the given NuGet source listing.
    private static (int ExitCode, string Output) RunShellStep(string stepName, Action<TempRepo> arrange, string? nugetListing = null)
    {
        var script = RunBlock(StepBody(ReadWorkflow(), stepName));
        using var repo = new TempRepo();
        using var bin = new TempRepo();
        arrange(repo);
        if (nugetListing is not null)
        {
            var dotnet = Path.Combine(bin.Root, "dotnet");
            bin.Write("dotnet", "#!/bin/sh\ncat <<'LIST'\n" + nugetListing + "LIST\n");
            File.SetUnixFileMode(dotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var psi = new ProcessStartInfo("bash")
        {
            WorkingDirectory = repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.Environment["PATH"] = bin.Root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("bash could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr);
    }

    private static string RunBlock(string step)
    {
        var lines = step.Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == "run: |");
        Assert.True(start >= 0, "Step has no run block.");

        // Workflow run blocks are indented by 10 spaces; strip that to get the script.
        return string.Join('\n', lines[(start + 1)..].Select(l => l.Length >= 10 ? l[10..] : l.TrimStart()));
    }

    private static void GitTrackAll(string workDir)
    {
        RunGit(workDir, "init", "-q");
        RunGit(workDir, "add", "-A");
    }

    private static void RunGit(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static string ExtractHeredoc(string step)
    {
        var lines = step.Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains("node <<'EOF'", StringComparison.Ordinal));
        Assert.True(start >= 0, "Step has no node heredoc.");
        var end = Array.FindIndex(lines, start + 1, l => l.Trim() == "EOF");
        Assert.True(end > start, "Node heredoc is not terminated.");

        // Workflow run blocks are indented by 10 spaces; strip that to get the script.
        return string.Join('\n', lines[(start + 1)..end].Select(l => l.Length >= 10 ? l[10..] : l));
    }

    private static (int ExitCode, string Output) RunNode(string script, string workDir)
    {
        var scriptPath = Path.Combine(workDir, "check.js");
        File.WriteAllText(scriptPath, script);
        var psi = new ProcessStartInfo("node", scriptPath)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("node could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr);
    }

    private static bool SecretPatternMatches(string content)
    {
        var workflow = ReadWorkflow();
        var line = workflow.Split('\n').Single(l => l.TrimStart().StartsWith("pattern='", StringComparison.Ordinal)).Trim();
        var pattern = line[(line.IndexOf('\'') + 1)..line.LastIndexOf('\'')];

        using var repo = new TempRepo();
        repo.Write("probe.txt", content);
        var psi = new ProcessStartInfo("grep")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-E");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(pattern);
        psi.ArgumentList.Add(Path.Combine(repo.Root, "probe.txt"));
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("grep could not be started.");
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private sealed class TempRepo : IDisposable
    {
        public TempRepo()
        {
            Root = Path.Combine(Path.GetTempPath(), "supply-chain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
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
