using AirlineDemo.Api.Investigation;

namespace AirlineDemo.Api.Tests;

/// <summary>
/// Criterion-to-test map (issue #155):
/// M-18 manifest shape and stub refusal: Shipped_manifest_is_stub_and_refused, Missing_value_is_refused,
///     Stub_value_is_refused_even_with_approval.
/// Approval: Unapproved_manifest_is_refused, Approved_manifest_with_all_values_is_usable.
/// Explicit rejection: Unsupported_arm_is_refused, Real_world_action_flag_is_refused, Invalid_json_is_refused.
/// Trace: Trace_records_exact_manifest_version_and_configuration_without_secrets.
/// M-07 synthetic guard: Non_synthetic_content_is_refused_without_approval,
///     Non_synthetic_content_is_refused_outside_approved_region, Synthetic_evidence_is_allowed.
/// </summary>
public class ApprovedModelManifestTests
{
    private const string ValidJson = """
    {
      "manifestVersion": "2026-10-01.1",
      "arm": "agent-framework",
      "deploymentName": "inv-gpt-prod-east",
      "model": "gpt-4.1",
      "apiVersion": "2025-03-01-preview",
      "region": "eastus2",
      "contentFilter": "default-strict",
      "approval": { "status": "approved", "approvedBy": "model-owner", "approvedAt": "2026-10-01T09:00:00Z" },
      "nonSyntheticContentApproved": false,
      "realWorldActionsEnabled": false
    }
    """;

    private static ApprovedModelManifest Valid() => ModelManifestGate.Parse(ValidJson);

    private static ApprovedModelManifest With(string json, string from, string to) =>
        ModelManifestGate.Parse(json.Replace(from, to, StringComparison.Ordinal));

    [Fact]
    public void Shipped_manifest_is_stub_and_refused()
    {
        var manifest = ModelManifestGate.Load(FindRepoFile(ModelManifestGate.ManifestRelativePath));

        Assert.Equal("STUB", manifest.Arm);
        Assert.Equal("STUB", manifest.DeploymentName);
        Assert.Equal("STUB", manifest.Model);
        Assert.Equal("STUB", manifest.ApiVersion);
        Assert.Equal("STUB", manifest.Region);
        Assert.Equal("STUB", manifest.ContentFilter);
        Assert.Throws<ModelGateException>(() => ModelManifestGate.RequireUsable(manifest));
    }

    [Theory]
    [InlineData("\"deploymentName\": \"inv-gpt-prod-east\"", "\"deploymentName\": \"\"")]
    [InlineData("\"region\": \"eastus2\"", "\"region\": \"  \"")]
    [InlineData("\"apiVersion\": \"2025-03-01-preview\"", "\"apiVersion\": \"<api-version>\"")]
    [InlineData("\"contentFilter\": \"default-strict\"", "\"contentFilter\": \"UNRESOLVED\"")]
    [InlineData("\"model\": \"gpt-4.1\"", "\"model\": \"STUB-model\"")]
    public void Missing_or_stub_value_is_refused(string from, string to)
    {
        var manifest = With(ValidJson, from, to);

        Assert.NotEmpty(ModelManifestGate.Problems(manifest));
        Assert.Throws<ModelGateException>(() => ModelManifestGate.RequireUsable(manifest));
    }

    [Fact]
    public void Missing_value_is_refused_when_field_absent()
    {
        var json = ValidJson.Replace("\"region\": \"eastus2\",", string.Empty, StringComparison.Ordinal);
        var manifest = ModelManifestGate.Parse(json);

        Assert.Null(manifest.Region);
        Assert.Throws<ModelGateException>(() => ModelManifestGate.RequireUsable(manifest));
    }

    [Fact]
    public void Unapproved_manifest_is_refused()
    {
        var manifest = With(ValidJson, "\"status\": \"approved\"", "\"status\": \"pending\"");

        var ex = Assert.Throws<ModelGateException>(() => ModelManifestGate.RequireUsable(manifest));
        Assert.Contains("approval.status", ex.Message);
    }

    [Fact]
    public void Approval_without_approver_or_timestamp_is_refused()
    {
        var manifest = With(ValidJson, "\"approvedBy\": \"model-owner\"", "\"approvedBy\": \"\"");

        Assert.Throws<ModelGateException>(() => ModelManifestGate.RequireUsable(manifest));
    }

    [Fact]
    public void Stub_value_is_refused_even_with_approval()
    {
        var manifest = With(ValidJson, "\"deploymentName\": \"inv-gpt-prod-east\"", "\"deploymentName\": \"STUB\"");

        var ex = Assert.Throws<ModelGateException>(() => ModelManifestGate.RequireUsable(manifest));
        Assert.Contains("deploymentName", ex.Message);
    }

    [Fact]
    public void Approved_manifest_with_all_values_is_usable()
    {
        var manifest = Valid();

        Assert.Empty(ModelManifestGate.Problems(manifest));
        ModelManifestGate.RequireUsable(manifest);
    }

    [Fact]
    public void Unsupported_arm_is_refused()
    {
        var manifest = With(ValidJson, "\"arm\": \"agent-framework\"", "\"arm\": \"free-form-agent\"");

        var ex = Assert.Throws<ModelGateException>(() => ModelManifestGate.RequireUsable(manifest));
        Assert.Contains("supported investigation arm", ex.Message);
    }

    [Fact]
    public void Real_world_action_flag_is_refused()
    {
        var manifest = With(ValidJson, "\"realWorldActionsEnabled\": false", "\"realWorldActionsEnabled\": true");

        var ex = Assert.Throws<ModelGateException>(() => ModelManifestGate.RequireUsable(manifest));
        Assert.Contains("realWorldActionsEnabled", ex.Message);
    }

    [Fact]
    public void Invalid_json_is_refused()
    {
        Assert.Throws<ModelGateException>(() => ModelManifestGate.Parse("{ not json"));
    }

    [Fact]
    public void Trace_records_exact_manifest_version_and_configuration_without_secrets()
    {
        var trace = ModelManifestGate.TraceAttributes(Valid());

        Assert.Equal("2026-10-01.1", trace["model.manifest.version"]);
        Assert.Equal("agent-framework", trace["model.arm"]);
        Assert.Equal("inv-gpt-prod-east", trace["model.deployment"]);
        Assert.Equal("gpt-4.1", trace["model.name"]);
        Assert.Equal("2025-03-01-preview", trace["model.api_version"]);
        Assert.Equal("eastus2", trace["model.region"]);
        Assert.Equal("default-strict", trace["model.content_filter"]);
        Assert.Equal(7, trace.Count);
        Assert.DoesNotContain(trace.Keys, key => key.Contains("prompt", StringComparison.OrdinalIgnoreCase)
            || key.Contains("document", StringComparison.OrdinalIgnoreCase)
            || key.Contains("key", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Trace_is_refused_for_unusable_manifest()
    {
        var manifest = Valid() with { ManifestVersion = "STUB-0" };

        Assert.Throws<ModelGateException>(() => ModelManifestGate.TraceAttributes(manifest));
    }

    [Fact]
    public void Synthetic_evidence_is_allowed()
    {
        ModelManifestGate.RequireRequestAllowed(Valid(), evidenceIsSynthetic: true, requestRegion: "eastus2");
    }

    [Fact]
    public void Non_synthetic_content_is_refused_without_approval()
    {
        var manifest = Valid();

        var ex = Assert.Throws<ModelGateException>(() =>
            ModelManifestGate.RequireRequestAllowed(manifest, evidenceIsSynthetic: false, requestRegion: "eastus2"));
        Assert.Contains("does not approve", ex.Message);
    }

    [Fact]
    public void Non_synthetic_content_is_refused_outside_approved_region()
    {
        var manifest = Valid() with { NonSyntheticContentApproved = true };

        var ex = Assert.Throws<ModelGateException>(() =>
            ModelManifestGate.RequireRequestAllowed(manifest, evidenceIsSynthetic: false, requestRegion: "westeurope"));
        Assert.Contains("outside the approved region", ex.Message);
    }

    [Fact]
    public void Non_synthetic_content_is_allowed_in_approved_region_when_approved()
    {
        var manifest = Valid() with { NonSyntheticContentApproved = true };

        ModelManifestGate.RequireRequestAllowed(manifest, evidenceIsSynthetic: false, requestRegion: "eastus2");
    }

    [Fact]
    public void Gate_refuses_synthetic_request_when_manifest_is_unusable()
    {
        var manifest = Valid() with { Region = "STUB" };

        Assert.Throws<ModelGateException>(() =>
            ModelManifestGate.RequireRequestAllowed(manifest, evidenceIsSynthetic: true, requestRegion: "eastus2"));
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relativePath)))
        {
            dir = dir.Parent;
        }

        return dir is null ? relativePath : Path.Combine(dir.FullName, relativePath);
    }
}
