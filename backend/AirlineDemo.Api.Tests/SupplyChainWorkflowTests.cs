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

        Assert.Contains("dotnet-CycloneDX AirlineDemo.sln -F Json", step);
        Assert.DoesNotContain("dotnet CycloneDX", step);
        Assert.DoesNotContain(" -j ", step);
        Assert.Contains("test -s artifacts/sbom/dotnet-sbom.cdx.json", step);
    }

    // M-12: the CycloneDX tool used by the .NET SBOM step is pinned, so a change of version fails this test.
    [Fact]
    public void SBOM_step_installs_pinned_cyclonedx_tool()
    {
        var step = StepBody(ReadWorkflow(), "Install CycloneDX tool");

        Assert.Contains("dotnet tool install --global CycloneDX --version 6.3.1", step);
    }

    [Fact]
    public void SBOM_step_generates_npm_cyclonedx_output()
    {
        var step = StepBody(ReadWorkflow(), "SBOM (npm)");

        Assert.Contains("@cyclonedx/cyclonedx-npm", step);
        Assert.Contains("test -s ../artifacts/sbom/npm-sbom.cdx.json", step);
    }

    // M-12: dotnet list --vulnerable output is required; missing output fails the run.
    [Fact]
    // AC: M-12. The dotnet vulnerability listing runs and a missing output fails the run.
    public void Audit_step_runs_dotnet_vulnerable_listing_and_fails_on_missing_output()
    {
        var step = StepBody(ReadWorkflow(), "Audit (.NET)");

        Assert.Contains("dotnet list", step);
        Assert.Contains("package --vulnerable", step);
        Assert.Contains("test -s artifacts/audit/dotnet-vulnerable.txt", step);
    }

    // M-12: npm audit output is required; missing output fails the run.
    [Fact]
    // AC: M-12. npm audit runs and a missing output fails the run.
    public void Audit_step_runs_npm_audit_and_fails_on_missing_output()
    {
        var step = StepBody(ReadWorkflow(), "Audit (npm)");

        Assert.Contains("npm audit --json", step);
        Assert.Contains("test -s ../artifacts/audit/npm-audit.json", step);
    }

    // AC: the downstream supply-chain steps run in CI. Each shell script must parse, and each command uses the
    // options verified against the tools (dotnet-CycloneDX 6.3.1 help, cyclonedx-npm@4 help, dotnet list, npm audit).
    [Theory]
    [InlineData("SBOM (.NET)")]
    [InlineData("SBOM (npm)")]
    [InlineData("Audit (.NET)")]
    [InlineData("Audit (npm)")]
    [InlineData("Exception validation")]
    [InlineData("Severity gate")]
    public void Supply_chain_step_script_has_valid_shell_syntax(string stepName)
    {
        var script = RunBlock(StepBody(ReadWorkflow(), stepName));
        var path = Path.Combine(Path.GetTempPath(), $"supply-chain-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, script);
        try
        {
            var psi = new ProcessStartInfo("bash", new[] { "-n", path })
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("bash could not be started.");
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"Step '{stepName}' has a shell syntax error: {stderr}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // AC: the evidence upload runs on every outcome and uploads the artifacts directory the audit steps write.
    [Fact]
    public void Upload_step_publishes_artifacts_even_on_failure()
    {
        var step = StepBody(ReadWorkflow(), "Upload supply-chain evidence");

        Assert.Contains("if: always()", step);
        Assert.Contains("uses: actions/upload-artifact@v4", step);
        Assert.Contains("path: artifacts/", step);
    }

    // IMAGE-BUILD-01: publishing runs only after a successful gate, on pushes to main.
    [Fact]
    public void Publish_job_is_limited_to_successful_main_pushes()
    {
        var job = JobBody(ReadWorkflow(), "publish-image");

        Assert.Contains("\n    needs: build-and-gate\n", job);
        Assert.Contains("\n    if: github.event_name == 'push' && github.ref == 'refs/heads/main'\n", job);
        Assert.DoesNotContain("pull_request", job);
        Assert.DoesNotContain("workflow_dispatch", job);
        Assert.DoesNotContain("refs/heads/release", job);
    }

    // IMAGE-BUILD-01: the job runs on the private-network runner and refuses public ACR resolution before login.
    [Fact]
    public void Publish_job_runs_on_private_network_runner()
    {
        var workflow = ReadWorkflow();
        var job = JobBody(workflow, "publish-image");
        var check = StepBody(workflow, "Verify ACR resolves to a private address");

        Assert.Contains("\n    runs-on: [self-hosted, linux, x64, private-acr]\n", job);
        Assert.Contains("os.environ[\"ACR_LOGIN_SERVER\"]", check);
        Assert.Contains("socket.getaddrinfo(host", check);
        Assert.Contains("a.is_private", check);
        Assert.Contains("sys.exit(", check);
        Assert.True(job.IndexOf("Verify ACR resolves", StringComparison.Ordinal) < job.IndexOf("Log in to ACR", StringComparison.Ordinal),
            "The private-address check must run before registry login.");
    }

    // IMAGE-BUILD-01: Azure authentication uses OIDC with the existing variables and no stored credentials.
    [Fact]
    public void Publish_job_authenticates_with_oidc_and_no_stored_credentials()
    {
        var job = JobBody(ReadWorkflow(), "publish-image");

        Assert.Contains("\n    permissions:\n      contents: read\n      id-token: write\n", job);
        Assert.Contains("client-id: ${{ vars.AZURE_CLIENT_ID }}", job);
        Assert.Contains("tenant-id: ${{ vars.AZURE_TENANT_ID }}", job);
        Assert.Contains("subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}", job);
        Assert.DoesNotContain("creds:", job);
        Assert.DoesNotContain("AZURE_CLIENT_SECRET", job);
    }

    // IMAGE-BUILD-02: the root Dockerfile is pushed to the configured ACR repository and the digest is captured.
    [Fact]
    public void Publish_job_pushes_the_built_image_and_records_its_digest()
    {
        var workflow = ReadWorkflow();
        var job = JobBody(workflow, "publish-image");
        var build = StepBody(workflow, "Build and push image");
        var record = StepBody(workflow, "Record image digest");

        Assert.Contains("ACR_NAME: ${{ vars.ACR_NAME }}", job);
        Assert.Contains("ACR_LOGIN_SERVER: ${{ vars.ACR_LOGIN_SERVER }}", job);
        Assert.Contains("IMAGE_REPOSITORY: ${{ vars.IMAGE_REPOSITORY }}", job);
        Assert.Contains("az acr login --name \"$ACR_NAME\"", job);
        Assert.Contains("file: ./Dockerfile", build);
        Assert.Contains("push: true", build);
        Assert.Contains("${{ env.ACR_LOGIN_SERVER }}/${{ env.IMAGE_REPOSITORY }}", build);
        Assert.Contains("DIGEST: ${{ steps.build.outputs.digest }}", record);
        Assert.Contains("^sha256:[0-9a-f]{64}$", record);
        Assert.DoesNotContain("steps.build.outputs.metadata", record);
    }

    // IMAGE-BUILD-03: the digest is written and uploaded only after the push, with 90-day retention.
    [Fact]
    public void Image_digest_artifact_is_published_with_retention()
    {
        var workflow = ReadWorkflow();
        var job = JobBody(workflow, "publish-image");
        var record = StepBody(workflow, "Record image digest");
        var upload = StepBody(workflow, "Upload image digest");

        Assert.Contains("mkdir -p artifacts/image", record);
        Assert.Contains("> artifacts/image/digest.txt", record);
        Assert.Contains("name: image-digest", upload);
        Assert.Contains("path: artifacts/image/digest.txt", upload);
        Assert.Contains("retention-days: 90", upload);
        var download = StepBody(workflow, "Download supply-chain evidence");
        var evidence = StepBody(workflow, "Add digest to supply-chain evidence");
        Assert.Contains("name: supply-chain-evidence", download);
        Assert.Contains("path: artifacts/", download);
        Assert.Contains("name: supply-chain-evidence", evidence);
        Assert.Contains("path: artifacts/", evidence);
        Assert.Contains("overwrite: true", evidence);
        Assert.Contains("retention-days: 90", evidence);
        Assert.True(job.IndexOf("name: Build and push image", StringComparison.Ordinal) < job.IndexOf("name: Record image digest", StringComparison.Ordinal),
            "The digest must be recorded after the push.");
        Assert.True(job.IndexOf("name: Record image digest", StringComparison.Ordinal) < job.IndexOf("name: Download supply-chain evidence", StringComparison.Ordinal),
            "The digest must be recorded before the evidence artifact is updated.");
        Assert.True(job.IndexOf("name: Download supply-chain evidence", StringComparison.Ordinal) < job.IndexOf("name: Add digest to supply-chain evidence", StringComparison.Ordinal),
            "The evidence artifact must be downloaded before it is re-uploaded with the digest.");
        Assert.True(job.IndexOf("name: Add digest to supply-chain evidence", StringComparison.Ordinal) < job.IndexOf("name: Upload image digest", StringComparison.Ordinal),
            "The evidence artifact must carry the digest before the separate digest artifact is uploaded.");
    }

    // M-12: publication depends on the gate, and the SBOM and severity gate steps run before it.
    [Fact]
    public void Publish_job_waits_for_the_existing_supply_chain_gate()
    {
        var workflow = ReadWorkflow();
        var gateJob = JobBody(workflow, "build-and-gate");
        var publishStart = workflow.IndexOf("\n  publish-image:\n", StringComparison.Ordinal);

        Assert.Contains("\n    needs: build-and-gate\n", JobBody(workflow, "publish-image"));
        Assert.True(publishStart > 0);
        Assert.True(gateJob.IndexOf("- name: SBOM (.NET)", StringComparison.Ordinal) < gateJob.IndexOf("- name: SBOM (npm)", StringComparison.Ordinal));
        Assert.True(gateJob.IndexOf("- name: SBOM (npm)", StringComparison.Ordinal) < gateJob.IndexOf("- name: Severity gate", StringComparison.Ordinal));
        Assert.True(gateJob.IndexOf("- name: Severity gate", StringComparison.Ordinal) < gateJob.IndexOf("- name: Upload supply-chain evidence", StringComparison.Ordinal));
        Assert.DoesNotContain("publish-image", gateJob);
    }

    [Fact]
    public void Supply_chain_commands_use_verified_options()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("dotnet-CycloneDX AirlineDemo.sln -F Json -fn dotnet-sbom.cdx.json -o artifacts/sbom", StepBody(workflow, "SBOM (.NET)"));
        Assert.Contains("npx --yes @cyclonedx/cyclonedx-npm@4 --output-format JSON --output-file ../artifacts/sbom/npm-sbom.cdx.json", StepBody(workflow, "SBOM (npm)"));
        Assert.Contains("dotnet list AirlineDemo.sln package --vulnerable --include-transitive > artifacts/audit/dotnet-vulnerable.txt", StepBody(workflow, "Audit (.NET)"));
        Assert.Contains("npm audit --json > ../artifacts/audit/npm-audit.json || true", StepBody(workflow, "Audit (npm)"));
    }

    // M-14: source and secret guards run before any dependency is restored or installed.
    [Fact]
    public void Source_and_secret_guards_run_before_dependency_install()
    {
        var workflow = ReadWorkflow();
        var sourceGuard = workflow.IndexOf("- name: Source restriction", StringComparison.Ordinal);
        var secretGuard = workflow.IndexOf("- name: Secret scan", StringComparison.Ordinal);
        var dotnetRestore = workflow.IndexOf("dotnet restore", StringComparison.Ordinal);
        var npmCi = workflow.IndexOf("npm ci", StringComparison.Ordinal);

        Assert.True(sourceGuard >= 0 && secretGuard >= 0, "Source and secret guards must exist.");
        Assert.True(sourceGuard < dotnetRestore && sourceGuard < npmCi, "Source restriction must run before install.");
        Assert.True(secretGuard < dotnetRestore && secretGuard < npmCi, "Secret scan must run before install.");
    }

    // M-12: an unexcepted high/critical finding blocks the release candidate.
    // AC: criterion 2 (severity gate: unexcepted high/critical blocks, low passes).
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
    // AC: criterion 2 (exception validation: missing, expired, or incomplete exceptions fail).
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

        Assert.Contains("node backend/ci/check-sources.mjs", step);
        Assert.DoesNotContain("grep", step);
    }

    // M-14: approved sources pass; the gate exits 0 only when every enabled NuGet source is nuget.org.
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
    // AC: M-14. Query strings and fragments on an npmjs.org URL are rejected, since they can carry credentials.
    [InlineData("https://registry.npmjs.org/react/-/react-18.2.0.tgz?token=abc")]
    [InlineData("https://registry.npmjs.org/react/-/react-18.2.0.tgz#frag")]
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

    // AC: M-14. An installable lockfile entry with no resolved URL must fail, since npm could fetch it from any registry.
    [Fact]
    public void Source_restriction_rejects_installable_npm_package_without_resolved()
    {
        var lockfile = "{\"packages\":{\"node_modules/react\":{\"resolved\": \"https://registry.npmjs.org/react/-/react-18.2.0.tgz\"},"
            + "\"node_modules/unpinned\":{\"version\": \"1.0.0\"}}}\n";

        var (exitCode, output) = RunShellStep("Source restriction", repo => repo.Write(LockPath, lockfile), NugetOrgListing);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("no resolved URL", output);
    }

    [Fact]
    public void Source_restriction_rejects_lockfile_without_npmjs_packages()
    {
        var (exitCode, _) = RunShellStep("Source restriction", repo => repo.Write(LockPath, "{\"packages\":{}}\n"), NugetOrgListing);

        Assert.NotEqual(0, exitCode);
    }

    // M-14: NuGet sources that only resemble nuget.org, or use another scheme, are rejected.
    [Theory]
    [InlineData("https://api.nuget.org.example.test/v3/index.json")]
    [InlineData("http://api.nuget.org/v3/index.json")]
    public void Source_restriction_rejects_lookalike_nuget_sources(string url)
    {
        var listing = "Registered Sources:\n\n  1.  nuget.org [Enabled]\n      " + url + "\n";

        var (exitCode, output) = RunShellStep("Source restriction", repo => repo.Write(LockPath, NpmjsOnlyLock), listing);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("NuGet source outside nuget.org", output);
    }

    // M-14: tracked repository content is scanned for committed credentials.
    [Fact]
    public void Secret_scan_checks_tracked_content_and_fails_on_matches()
    {
        var step = StepBody(ReadWorkflow(), "Secret scan");

        Assert.Contains("gitleaks detect", step);
        Assert.Contains("--exit-code 1", step);
        Assert.DoesNotContain("pattern=", step);
    }

    // AC: M-14. The gitleaks binary is downloaded at a pinned version and its published SHA-256 is checked.
    [Fact]
    public void Install_gitleaks_verifies_pinned_sha256()
    {
        var step = StepBody(ReadWorkflow(), "Install gitleaks");

        Assert.Contains("version=8.18.4", step);
        Assert.Contains("ba6dbb656933921c775ee5a2d1c13a91046e7952e9d919f9bac4cec61d628e7d", step);
        Assert.Contains("sha256sum -c -", step);
    }

    // AC: M-14. The real secret scan step exits 0 on every tracked file of this repository.
    [Fact]
    public void Secret_scan_passes_on_repository_tracked_files()
    {
        var root = FindRepoRoot();
        var (exitCode, output) = RunShellStep("Secret scan", repo =>
        {
            foreach (var path in TrackedFiles(root))
            {
                var target = Path.Combine(repo.Root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(root, path), target);
            }

            GitTrackAll(repo.Root);
        });

        Assert.True(exitCode == 0, output);
    }

    // Clean baseline: the lockfile is tracked and holds no credential, so the scan passes.
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
    public void Secret_scan_fails_on_credential_in_tracked_source_file()
    {
        var (exitCode, output) = RunShellStep("Secret scan", repo =>
        {
            repo.Write("backend/appsettings.local.json", "{\"token\": \"" + "gh" + "p_" + "Q7xR2mK9pL4vN8sT1wY6zB3cD5fG0hJ2kE7a" + "\"}\n");
            GitTrackAll(repo.Root);
        });

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Committed credential", output);
    }

    // M-14: committed credentials that gitleaks' default rules detect fail the secret scan.
    // Fixtures are joined from pieces so this file never contains a full credential literal.
    // AC: M-14. Each fixture is committed to a temp repo, and the real workflow secret scan step must fail on it.
    // Split so no source line holds the key name, separator and value together; gitleaks would flag this file otherwise.
    private const string StorageKeyName = "Account" + "Key";
    private const string StorageSeparator = "=";
    private const string StorageValue = "abcd" + "efgh12345678";

    [Theory]
    [InlineData("gh" + "p_" + "Q7xR2mK9pL4vN8sT1wY6zB3cD5fG0hJ2kE7a")]
    [InlineData("AKIA" + "ABCDEFGHIJKLMNOP")]
    [InlineData("DefaultEndpointsProtocol=https;" + StorageKeyName + StorageSeparator + StorageValue)]
    public void M14_secret_scan_fails_on_committed_credentials(string content)
    {
        var (exitCode, output) = RunShellStep("Secret scan", repo =>
        {
            repo.Write("fixture.txt", content + "\n");
            GitTrackAll(repo.Root);
        });

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Committed credential", output);
    }

    // AC: M-14. Plain URLs and clone addresses are not credentials, so the secret scan passes on them.
    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json")]
    [InlineData("https://registry.npmjs.org/react/-/react-18.2.0.tgz")]
    [InlineData("git@github.com:owner/repo.git")]
    public void M14_secret_scan_ignores_plain_urls(string content)
    {
        var (exitCode, _) = RunShellStep("Secret scan", repo =>
        {
            repo.Write("fixture.txt", content + "\n");
            GitTrackAll(repo.Root);
        });

        Assert.Equal(0, exitCode);
    }

    // AC: M-14. Tracked files are scanned under neutral names, so a lockfile name cannot hide a credential.
    [Fact]
    public void M14_secret_scan_fails_on_credential_in_package_lock()
    {
        var (exitCode, output) = RunShellStep("Secret scan", repo =>
        {
            repo.Write(LockPath, "{\"resolved\": \"https://registry.npmjs.org/x/-/x-1.tgz?token=" + "gh" + "p_" + "Q7xR2mK9pL4vN8sT1wY6zB3cD5fG0hJ2kE7a" + "\"}\n");
            GitTrackAll(repo.Root);
        });

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Committed credential", output);
    }

    // AC: M-14. A tracked filename beginning with "-" is copied as a path, not read as a cp option.
    [Fact]
    public void M14_secret_scan_fails_on_credential_in_option_like_filename()
    {
        var (exitCode, output) = RunShellStep("Secret scan", repo =>
        {
            repo.Write("--help", "token = " + "gh" + "p_" + "Q7xR2mK9pL4vN8sT1wY6zB3cD5fG0hJ2kE7a" + "\n");
            GitTrackAll(repo.Root);
        });

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Committed credential", output);
    }

    // AC: M-14. An .npmrc that overrides the npm registry is rejected, because it can redirect npmjs.org lockfile URLs.
    [Fact]
    public void M14_source_restriction_rejects_npmrc_registry_override()
    {
        var (exitCode, output) = RunShellStep("Source restriction", repo =>
        {
            repo.Write(LockPath, NpmjsOnlyLock);
            repo.Write(".npmrc", "registry=https://evil.example/\n");
        }, NugetOrgListing);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("overrides the npm registry", output);
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

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

    // Runs a workflow shell step's run block in a temp repo. A fake `dotnet` on PATH prints the given NuGet source listing.
    private static (int ExitCode, string Output) RunShellStep(string stepName, Action<TempRepo> arrange, string? nugetListing = null)
    {
        var script = RunBlock(StepBody(ReadWorkflow(), stepName));
        using var repo = new TempRepo();
        using var bin = new TempRepo();
        if (stepName == "Source restriction")
        {
            // The step runs the real checker; copy it to the path the workflow uses, relative to the repo root.
            repo.Write("backend/ci/check-sources.mjs", File.ReadAllText(Path.Combine(FindRepoRoot(), "backend", "ci", "check-sources.mjs")));
        }

        if (stepName == "Secret scan")
        {
            // The step runs the installed gitleaks binary; expose it on the fake PATH directory.
            var gitleaks = FindOnPath("gitleaks") ?? throw new InvalidOperationException("gitleaks must be on PATH to run the secret scan tests.");
            File.CreateSymbolicLink(Path.Combine(bin.Root, "gitleaks"), gitleaks);
        }

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

    private static IEnumerable<string> TrackedFiles(string workDir)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("ls-files");
        psi.ArgumentList.Add("-z");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return stdout.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => File.Exists(Path.Combine(workDir, path)))
            .ToList();
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

    private static string JobBody(string workflow, string jobName)
    {
        var header = $"\n  {jobName}:\n";
        var start = workflow.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Workflow job '{jobName}' is missing.");

        var next = System.Text.RegularExpressions.Regex.Match(workflow[(start + header.Length)..], @"\n  [a-z][a-z-]*:\n");
        return next.Success ? workflow.Substring(start, header.Length + next.Index) : workflow[start..];
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
