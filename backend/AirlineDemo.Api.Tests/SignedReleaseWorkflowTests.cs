namespace AirlineDemo.Api.Tests;

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
        Assert.Contains("does not list product owner", preflight);
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
    }

    // AC: deployment verifies the certificate and digest before updating the app.
    [Fact]
    public void Deploy_is_gated_by_signature_and_digest_verification()
    {
        var workflow = ReadWorkflow();
        var verify = StepBody(workflow, "Verify signature and digest");
        var deploy = StepBody(workflow, "Deploy signed digest");

        Assert.Contains("notation verify", verify);
        Assert.Contains("digest mismatch", verify);
        Assert.Contains("az containerapp update", deploy);
        Assert.Contains("@$IMAGE_DIGEST", deploy);
        Assert.True(workflow.IndexOf("- name: Verify signature and digest", StringComparison.Ordinal)
            < workflow.IndexOf("- name: Deploy signed digest", StringComparison.Ordinal),
            "Signature and digest verification must run before deployment.");
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

    // AC: the requested digest must match the digest recorded by the specified CI build run before signing.
    [Fact]
    public void Release_binds_requested_digest_to_build_run_before_signing()
    {
        var workflow = ReadWorkflow();
        var check = StepBody(workflow, "Verify build evidence present");

        Assert.Contains("test -s evidence/image/digest.txt", check);
        Assert.Contains("was not produced by build run", check);
        Assert.Contains("\"$built\" != \"$IMAGE_DIGEST\"", check);
        Assert.True(workflow.IndexOf("- name: Verify build evidence present", StringComparison.Ordinal)
            < workflow.IndexOf("- name: Sign image digest", StringComparison.Ordinal),
            "Provenance must be checked before the digest is signed.");
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
