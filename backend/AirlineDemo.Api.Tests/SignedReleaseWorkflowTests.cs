namespace AirlineDemo.Api.Tests;

using System.Diagnostics;
using System.Text.RegularExpressions;

public sealed class SignedReleaseWorkflowTests
{
    private const string WorkflowPath = ".github/workflows/release.yml";

    // AC: M-12. Approval gate: the deploy job runs in a protected environment whose required reviewers
    // (product owner) must approve before any step executes.
    [Fact]
    public void Release_job_runs_in_protected_environment_requiring_approval()
    {
        var workflow = ReadWorkflow();

        Assert.Matches(@"environment:\s*production-release", workflow);
        Assert.DoesNotContain("environment: ''", workflow);

        // The environment must be shown to require reviewers: a preflight job fails the run otherwise,
        // and the release job cannot start before preflight passes.
        var preflight = JobBody(workflow, "preflight");
        Assert.Contains("repos/$REPO/environments/production-release", preflight);
        Assert.Contains("required_reviewers", preflight);
        Assert.Contains("PRODUCT_OWNER_LOGIN: ${{ vars.PRODUCT_OWNER_LOGIN }}", preflight);
        Assert.Contains("PRODUCT_OWNER_LOGIN variable is required", preflight);
        Assert.Contains(".reviewer.login == $owner", preflight);
        Assert.Contains("select(.type == \"User\"", preflight);
        Assert.Contains("must require exactly one reviewer", preflight);
        Assert.Contains("\"$owner_count\" -ne 1 || \"$total_count\" -ne 1", preflight);
        Assert.Contains("product-owner approval is not enforced", preflight);
        Assert.Contains("exit 1", preflight);
        Assert.DoesNotContain("environment:", preflight);

        var release = JobBody(workflow, "release");
        Assert.Contains("needs: preflight", release);
        Assert.Matches(@"environment:\s*production-release", release);
    }

    // AC: M-12. Deployment only follows a successful build of trusted CI: the build run must be a
    // successful push to main from .github/workflows/build.yml, checked via the Actions API before signing.
    [Fact]
    public void Release_verifies_build_run_is_trusted_ci_before_signing()
    {
        var workflow = ReadWorkflow();
        var provenance = StepBody(workflow, "Verify build run provenance");

        Assert.Contains("repos/$REPO/actions/runs/$BUILD_RUN_ID", provenance);
        Assert.Contains(".github/workflows/build.yml", provenance);
        Assert.Contains("\"$event\" != \"push\"", provenance);
        Assert.Contains("\"$branch\" != \"main\"", provenance);
        Assert.Contains("\"$conclusion\" != \"success\"", provenance);
        Assert.Contains("exit 1", provenance);
        Assert.True(workflow.IndexOf("- name: Verify build run provenance", StringComparison.Ordinal)
            < workflow.IndexOf("- name: Sign image digest", StringComparison.Ordinal),
            "Build run provenance must be verified before the digest is signed.");
    }

    [Theory]
    [InlineData(true, false, 0, "")]
    [InlineData(false, false, 0, "apt-get update\napt-get install -y gh\n")]
    [InlineData(false, true, 42, "apt-get update\napt-get install -y gh\n")]
    public void Release_installs_github_cli_only_when_missing_and_fails_closed(
        bool installed, bool installFails, int expectedExitCode, string expectedCalls)
    {
        var workflow = ReadWorkflow();
        var script = RunScript(workflow, "Ensure GitHub CLI is available");
        var result = RunGitHubCliSetup(script, installed, installFails);

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Equal(expectedCalls, result.Calls);
        Assert.Equal(expectedExitCode == 0 ? "gh version test\n" : "", result.Output);
        Assert.True(workflow.IndexOf("- name: Ensure GitHub CLI is available", StringComparison.Ordinal)
            < workflow.IndexOf("- name: Verify build run provenance", StringComparison.Ordinal));
    }

    // AC: M-12. OIDC: federated credentials only, no stored cloud secrets.
    [Fact]
    public void Release_authenticates_with_oidc_and_stores_no_cloud_secrets()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("id-token: write", workflow);
        Assert.Contains("azure/login@v2", workflow);
        Assert.DoesNotContain("creds:", workflow);
        Assert.DoesNotContain("client-secret", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AZURE_CREDENTIALS", workflow);
        Assert.DoesNotContain("secrets.AZURE", workflow, StringComparison.OrdinalIgnoreCase);
    }

    // AC: M-12. Mutable tags are rejected: the input must be a sha256 digest.
    [Fact]
    public void Release_rejects_mutable_tags_and_requires_sha256_digest()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("^sha256:[0-9a-f]{64}$", workflow);
        Assert.Contains("tags and other references are rejected", workflow);
        Assert.Contains("@$IMAGE_DIGEST", workflow);
    }

    // AC: image is signed with Notation via the Azure Key Vault key, and verified before deploy.
    [Fact]
    public void Release_signs_with_notation_and_key_vault_key()
    {
        var step = StepBody(ReadWorkflow(), "Sign image digest");

        Assert.Contains("notation sign", step);
        Assert.Contains("--plugin azure-kv", step);
        Assert.Contains("--id \"$SIGNING_KEY_ID\"", step);
        Assert.Contains("--force-referrers-tag=false", step);
        Assert.DoesNotContain("--allow-referrers-api", step);
    }

    [Fact]
    public void Release_selects_cli_credentials_and_the_self_signed_certificate()
    {
        var workflow = ReadWorkflow();
        var sign = StepBody(workflow, "Sign image digest");

        Assert.Contains("--plugin-config self_signed=true", sign);
        Assert.Contains("--plugin-config credential_type=azurecli", sign);
        Assert.True(workflow.IndexOf("- name: Azure login (OIDC)", StringComparison.Ordinal)
            < workflow.IndexOf("- name: Sign image digest", StringComparison.Ordinal));
    }

    // AC: deployment verifies the certificate and digest before updating the app.
    [Fact]
    public void Deploy_is_gated_by_signature_and_digest_verification()
    {
        var workflow = ReadWorkflow();
        var verify = StepBody(workflow, "Verify signature and digest");
        var deploy = StepBody(workflow, "Deploy signed digest");

        Assert.Contains("notation verify \"$ACR_LOGIN_SERVER/$IMAGE_REPOSITORY@$IMAGE_DIGEST\"", verify);
        Assert.Contains("digest mismatch", verify);

        // Fail closed: the verify step runs with strict shell errors, no step in the workflow may ignore a failure,
        // and a failing verify stops the job before the deploy step.
        Assert.Contains("set -euo pipefail", verify);
        Assert.DoesNotContain("|| true", workflow);
        Assert.DoesNotContain("continue-on-error", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("if:", verify);
        Assert.Contains("if: ${{ !inputs.bootstrap_only }}", deploy);

        // Trust: the signing certificate is downloaded from Key Vault, added to Notation's CA trust store, and a strict
        // trust policy pins the certificate subject and the registry scope before `notation verify` runs.
        Assert.Contains("az keyvault certificate download", verify);
        Assert.Contains("notation cert add --type ca --store airlinedemo", verify);
        Assert.Contains("trustpolicy.json", verify);
        Assert.Contains("signatureVerification: {level: \"strict\"}", verify);
        Assert.Contains("x509.subject: \\($subject)", verify);
        Assert.Contains("AZURE_KV_SIGNING_CERT_SUBJECT", verify);
        Assert.True(verify.IndexOf("trustpolicy.json", StringComparison.Ordinal) < verify.IndexOf("notation verify", StringComparison.Ordinal),
            "The trust policy must be written before notation verify runs.");
        Assert.Contains("az containerapp update", deploy);
        Assert.Contains("@$IMAGE_DIGEST", deploy);
        Assert.True(workflow.IndexOf("- name: Verify signature and digest", StringComparison.Ordinal)
            < workflow.IndexOf("- name: Deploy signed digest", StringComparison.Ordinal),
            "Signature and digest verification must run before deployment.");
    }

    [Fact]
    public void Bootstrap_only_defaults_off_and_preserves_approval_and_signature_checks()
    {
        var workflow = ReadWorkflow();

        Assert.Matches(@"bootstrap_only:\s+description: [^\n]+\s+required: false\s+default: false\s+type: boolean", workflow);
        Assert.Contains("needs: preflight", JobBody(workflow, "release"));
        Assert.Matches(@"environment:\s*production-release", JobBody(workflow, "release"));
        Assert.DoesNotContain("bootstrap_only", JobBody(workflow, "preflight"));
        Assert.DoesNotContain("bootstrap_only", StepBody(workflow, "Verify build run provenance"));
        Assert.DoesNotContain("bootstrap_only", StepBody(workflow, "Verify build evidence present"));
        Assert.DoesNotContain("bootstrap_only", StepBody(workflow, "Sign image digest"));
        Assert.DoesNotContain("bootstrap_only", StepBody(workflow, "Verify signature and digest"));
        Assert.Equal(1, Regex.Matches(workflow, @"if:.*inputs\.bootstrap_only").Count);
        Assert.Contains("if: ${{ !inputs.bootstrap_only }}", StepBody(workflow, "Deploy signed digest"));
        Assert.Contains("if: always()", StepBody(workflow, "Retain release evidence"));
    }

    // AC: private registry use. The ACR must resolve to a private address; public resolution fails the release.
    [Fact]
    public void Release_requires_private_registry_endpoint()
    {
        var step = StepBody(ReadWorkflow(), "Private registry connectivity");

        Assert.Contains("getent hosts", step);
        Assert.Contains("private endpoint is required", step);
        Assert.Contains("resolves to public address", step);
        Assert.Contains("10\\.", step);
    }

    // AC: private registry use. The release job must run on a private-network runner, not a hosted runner.
    [Fact]
    public void Release_job_runs_on_private_network_runner()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("needs: preflight\n    # Hosted runners cannot reach the private-only ACR endpoint", workflow);
        Assert.Contains("runs-on: [self-hosted, linux, x64, private-acr]", workflow);
        Assert.DoesNotContain("runs-on: ubuntu-latest\n    timeout-minutes: 30", workflow);
    }

    // ACR refresh credentials must be exchanged before repository API reads.
    [Fact]
    public void Signature_export_exchanges_refresh_token_for_repository_scoped_access()
    {
        var step = StepBody(ReadWorkflow(), "Sign image digest");

        Assert.Contains("--data-urlencode \"grant_type=refresh_token\"", step);
        Assert.Contains("--data-urlencode \"service=$ACR_LOGIN_SERVER\"", step);
        Assert.Contains("--data-urlencode \"scope=repository:$IMAGE_REPOSITORY:pull\"", step);
        Assert.Contains("--data-urlencode \"refresh_token=$refresh_token\"", step);
        Assert.Contains("\"https://$ACR_LOGIN_SERVER/oauth2/token\"", step);
        Assert.Equal(3, Regex.Matches(step, Regex.Escape("-H \"Authorization: Bearer $token\"")).Count);
        Assert.DoesNotContain("-u \"00000000-0000-0000-0000-000000000000:", step);
    }

    [Theory]
    [InlineData("{\"access_token\":\"opaque-access\"}", 0, "opaque-access\n")]
    [InlineData("{}", 4, "")]
    [InlineData("{\"access_token\":\"\"}", 4, "")]
    [InlineData("{\"access_token\":null}", 4, "")]
    [InlineData("{\"access_token\":42}", 4, "")]
    [InlineData("{\"error\":\"invalid_grant\"}", 4, "")]
    public void Signature_export_requires_a_nonempty_access_token(
        string response, int expectedExitCode, string expectedOutput)
    {
        var step = StepBody(ReadWorkflow(), "Sign image digest");
        var filter = Regex.Match(step, @"\|\s*jq -er '([^']+)'");
        Assert.True(filter.Success, "The access-token exchange must fail closed when its response has no usable token.");
        var psi = new ProcessStartInfo("jq")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-er");
        psi.ArgumentList.Add(filter.Groups[1].Value);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("jq could not be started.");
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(response);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(expectedExitCode, process.ExitCode);
        Assert.Equal(expectedOutput, stdout);
        Assert.Equal("", stderr.GetAwaiter().GetResult());
    }

    // AC: SBOM, audit output, and signature retained for 90 days; missing artifacts fail the release.
    [Fact]
    public void Release_retains_sbom_audit_and_signature_for_90_days()
    {
        var workflow = ReadWorkflow();
        var retain = StepBody(workflow, "Retain release evidence");

        Assert.Contains("retention-days: 90", retain);
        Assert.Contains("if-no-files-found: error", retain);
        Assert.Contains("sbom/", retain);
        Assert.Contains("audit/", retain);
        Assert.Contains("artifacts/signature/", retain);
    }

    [Fact]
    public void Release_fails_when_build_evidence_is_missing()
    {
        var step = StepBody(ReadWorkflow(), "Verify build evidence present");

        Assert.Contains("test -s evidence/sbom/dotnet-sbom.cdx.json", step);
        Assert.Contains("test -s evidence/sbom/npm-sbom.cdx.json", step);
        Assert.Contains("test -s evidence/audit/dotnet-vulnerable.txt", step);
        Assert.Contains("test -s evidence/audit/npm-audit.json", step);
    }

    [Fact]
    public void Release_downloads_evidence_from_the_build_run()
    {
        var step = StepBody(ReadWorkflow(), "Download build evidence");

        Assert.Contains("name: supply-chain-evidence", step);
        Assert.Contains("run-id: ${{ inputs.build_run_id }}", step);
    }

    // AC: Notation and the Key Vault plugin are installed from pinned, checksum-verified releases before signing.
    [Fact]
    public void Release_installs_pinned_notation_and_key_vault_plugin_before_signing()
    {
        var workflow = ReadWorkflow();
        var install = StepBody(workflow, "Install Notation and Azure Key Vault plugin");

        Assert.Matches(@"NOTATION_SHA256:\s*""[0-9a-f]{64}""", install);
        Assert.Matches(@"AZURE_KV_PLUGIN_SHA256:\s*""[0-9a-f]{64}""", install);
        Assert.Contains("sha256sum -c -", install);
        Assert.Contains("/usr/local/bin/notation", install);
        Assert.Contains("notation-azure-kv", install);
        Assert.True(workflow.IndexOf("- name: Install Notation and Azure Key Vault plugin", StringComparison.Ordinal)
            < workflow.IndexOf("- name: Sign image digest", StringComparison.Ordinal),
            "Notation must be installed before the signing step runs.");
    }

    // AC: RELEASE-DIGEST-01. The digest is read from its own artifact of the specified build run, never the release run.
    [Fact]
    public void Release_downloads_digest_artifact_from_the_named_build_run()
    {
        var workflow = ReadWorkflow();
        var step = StepBody(workflow, "Download image digest");

        Assert.Contains("uses: actions/download-artifact@v4", step);
        Assert.Contains("name: image-digest", step);
        Assert.Contains("path: evidence/image", step);
        Assert.Contains("run-id: ${{ inputs.build_run_id }}", step);
        Assert.Contains("github-token: ${{ github.token }}", step);
        Assert.DoesNotContain("github.run_id", step);
    }

    // AC: RELEASE-DIGEST-02. The requested digest must be well formed and equal the image-digest artifact before signing.
    [Fact]
    public void Release_binds_requested_digest_to_separate_artifact_before_signing()
    {
        var workflow = ReadWorkflow();
        var script = RunScript(workflow, "Verify build evidence present");

        // A malformed value that also matches the artifact must be rejected by the format guard itself.
        var malformed = "sha256:" + new string('A', 64);
        var malformedRun = RunDigestCheck(script, malformed, malformed);
        Assert.Equal(1, malformedRun.ExitCode);
        Assert.Contains("IMAGE_DIGEST must be sha256:<64 lowercase hex>", malformedRun.Output);
        Assert.DoesNotContain("was not produced by build run", malformedRun.Output);

        // Covers every lowercase hex digit, so the guard must accept the full [0-9a-f] range.
        var valid = "sha256:" + string.Concat(Enumerable.Repeat("0123456789abcdef", 4));
        var matching = RunDigestCheck(script, valid, valid);
        Assert.Equal(0, matching.ExitCode);

        var missing = RunDigestCheck(script, valid, artifactDigest: null);
        Assert.Equal(1, missing.ExitCode);

        var other = "sha256:" + new string('b', 64);
        var mismatch = RunDigestCheck(script, valid, other);
        Assert.Equal(1, mismatch.ExitCode);
        Assert.Contains("image digest " + valid + " was not produced by build run 1", mismatch.Output);
        Assert.Contains("image-digest artifact recorded " + other, mismatch.Output);
        Assert.True(workflow.IndexOf("- name: Download image digest", StringComparison.Ordinal)
            < workflow.IndexOf("- name: Verify build evidence present", StringComparison.Ordinal),
            "The image-digest artifact must be downloaded before it is checked.");
        Assert.True(workflow.IndexOf("- name: Verify build evidence present", StringComparison.Ordinal)
            < workflow.IndexOf("- name: Sign image digest", StringComparison.Ordinal),
            "Digest binding must be checked before the digest is signed.");
    }

    // AC: the signature itself is exported and retained, not only an inspection report.
    [Fact]
    public void Release_exports_signature_artifact_before_retention()
    {
        var sign = StepBody(ReadWorkflow(), "Sign image digest");

        Assert.DoesNotContain("notation download", sign);
        Assert.Contains("/v2/$IMAGE_REPOSITORY/referrers/$IMAGE_DIGEST", sign);
        Assert.Contains("application/vnd.cncf.notary.signature", sign);
        Assert.Contains("artifacts/signature/signature.cose", sign);
        Assert.Contains("exported signature does not match its registry digest", sign);
        Assert.Contains("test -s artifacts/signature/signature.cose", sign);
    }

    private static string RunScript(string workflow, string stepName)
    {
        var lines = StepBody(workflow, stepName).Split('\n');
        var runIndex = Array.FindIndex(lines, line => line.Trim() == "run: |");
        Assert.True(runIndex >= 0, $"Workflow step '{stepName}' has no run script.");

        return string.Join('\n', lines.Skip(runIndex + 1).Select(line => line.Length >= 10 ? line[10..] : line.TrimStart()));
    }

    // Runs a release check script in a temporary directory with the evidence files present.
    // A null artifactDigest means evidence/image/digest.txt is absent.
    private static (int ExitCode, string Output) RunDigestCheck(string script, string requestedDigest, string? artifactDigest)
    {
        var dir = Path.Combine(Path.GetTempPath(), "release-digest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "evidence", "sbom"));
            Directory.CreateDirectory(Path.Combine(dir, "evidence", "audit"));
            Directory.CreateDirectory(Path.Combine(dir, "evidence", "image"));
            File.WriteAllText(Path.Combine(dir, "evidence", "sbom", "dotnet-sbom.cdx.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "evidence", "sbom", "npm-sbom.cdx.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "evidence", "audit", "dotnet-vulnerable.txt"), "none");
            File.WriteAllText(Path.Combine(dir, "evidence", "audit", "npm-audit.json"), "{}");
            if (artifactDigest is not null)
            {
                File.WriteAllText(Path.Combine(dir, "evidence", "image", "digest.txt"), artifactDigest + "\n");
            }

            var scriptPath = Path.Combine(dir, "check.sh");
            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo("bash", scriptPath)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["IMAGE_DIGEST"] = requestedDigest;
            psi.Environment["BUILD_RUN_ID"] = "1";

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("bash could not be started.");
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout + stderr.Result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static (int ExitCode, string Output, string Calls) RunGitHubCliSetup(
        string script, bool installed, bool installFails)
    {
        var dir = Path.Combine(Path.GetTempPath(), "release-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bin = Path.Combine(dir, "bin");
            Directory.CreateDirectory(bin);
            var gh = Path.Combine(dir, "gh");
            var sudo = Path.Combine(bin, "sudo");
            var calls = Path.Combine(dir, "calls");
            File.WriteAllText(gh, "#!/bin/bash\nprintf 'gh version test\\n'\n");
            File.WriteAllText(sudo, """
                #!/bin/bash
                set -euo pipefail
                printf '%s\n' "$*" >> "$APT_CALLS"
                if [[ "$*" == "apt-get install -y gh" ]]; then
                  if [[ "$INSTALL_FAILS" == true ]]; then exit 42; fi
                  /bin/cp "$GH_STUB" "$TOOLS/gh"
                fi
                """);
            if (!OperatingSystem.IsWindows())
            {
                var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                File.SetUnixFileMode(gh, mode);
                File.SetUnixFileMode(sudo, mode);
            }
            if (installed)
            {
                File.Copy(gh, Path.Combine(bin, "gh"));
            }

            var scriptPath = Path.Combine(dir, "setup.sh");
            File.WriteAllText(scriptPath, script);
            var psi = new ProcessStartInfo("/bin/bash", scriptPath)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["PATH"] = bin;
            psi.Environment["APT_CALLS"] = calls;
            psi.Environment["TOOLS"] = bin;
            psi.Environment["GH_STUB"] = gh;
            psi.Environment["INSTALL_FAILS"] = installFails ? "true" : "false";

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("bash could not be started.");
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout + stderr.Result, File.Exists(calls) ? File.ReadAllText(calls) : "");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string ReadWorkflow() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), WorkflowPath));

    private static string JobBody(string workflow, string jobName)
    {
        var header = $"\n  {jobName}:\n";
        var start = workflow.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Workflow job '{jobName}' is missing.");

        var next = Regex.Match(workflow[(start + header.Length)..], @"\n  [A-Za-z][\w-]*:\n");
        return next.Success ? workflow.Substring(start, header.Length + next.Index) : workflow[start..];
    }

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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, WorkflowPath)))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root with .github/workflows/release.yml not found.");
    }
}
