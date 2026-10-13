namespace AirlineDemo.Api.Tests;

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
        Assert.DoesNotContain("secrets.", workflow);
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

    private static string ReadWorkflow() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), WorkflowPath));

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
