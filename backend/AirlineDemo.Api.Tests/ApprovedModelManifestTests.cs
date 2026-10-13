using AirlineDemo.Api.Investigation;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AirlineDemo.Api.Tests;

/// <summary>Covers M-18 (manifest gating) and M-07 (synthetic-only, in-region model requests).</summary>
public class ApprovedModelManifestTests
{
    private const string ApprovedJson = """
        {
          "manifestVersion": "2026.10.1",
          "approved": true,
          "arm": "investigation-live",
          "deploymentName": "fixture-deployment",
          "model": "fixture-model",
          "apiVersion": "2025-01-01",
          "region": "eastus2",
          "contentFilter": "enabled"
        }
        """;

    private static ApprovedModelManifest Approved() => ApprovedModelManifest.Parse(ApprovedJson);

    private static ApprovedModelManifest Approved(string field, JsonNode? value)
    {
        var node = JsonNode.Parse(ApprovedJson)!.AsObject();
        node[field] = value;
        return ApprovedModelManifest.Parse(node.ToJsonString());
    }

    private static ModelRequest Synthetic(string content = "Synthetic aircraft evidence v1.") =>
        new("evidence-v1", IsSynthetic: true, DataRegion: null, content);

    private static string ShippedManifestPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config", "model-manifest.json")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new FileNotFoundException("config/model-manifest.json not found above the test binaries")
            : Path.Combine(dir.FullName, "config", "model-manifest.json");
    }

    [Fact]
    public void Shipped_manifest_is_stub_unapproved_and_refused()
    {
        var manifest = ApprovedModelManifest.Load(ShippedManifestPath());

        Assert.False(manifest.Approved);
        Assert.Equal("STUB", manifest.DeploymentName);
        var errors = manifest.Validate();
        Assert.Contains("manifest is not approved", errors);
        Assert.Contains("deploymentName is still a stub value", errors);
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(manifest, Synthetic()));
    }

    [Fact]
    public void Approved_complete_manifest_is_usable()
    {
        var manifest = Approved();

        Assert.Empty(manifest.Validate());
        Assert.Same(manifest, manifest.RequireUsable());
    }

    [Theory]
    [InlineData("deploymentName")]
    [InlineData("model")]
    [InlineData("apiVersion")]
    [InlineData("region")]
    [InlineData("contentFilter")]
    [InlineData("arm")]
    public void Missing_value_is_refused(string field)
    {
        var manifest = Approved(field, null);

        Assert.Contains($"{field} is missing", manifest.Validate());
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(manifest, Synthetic()));
    }

    [Fact]
    public void Unapproved_manifest_is_refused_even_with_complete_values()
    {
        var manifest = Approved("approved", (JsonNode?)false);

        Assert.Contains("manifest is not approved", manifest.Validate());
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(manifest, Synthetic()));
    }

    [Fact]
    public void Stub_value_is_refused_even_when_approved()
    {
        var manifest = Approved("model", "STUB-model");

        Assert.Contains("model is still a stub value", manifest.Validate());
        Assert.Throws<ModelGateException>(() => manifest.RequireUsable());
    }

    [Fact]
    public void Unsupported_arm_and_disabled_content_filter_are_refused()
    {
        Assert.Contains("arm 'shadow-run' is not supported", Approved("arm", "shadow-run").Validate());
        Assert.Contains("contentFilter must be 'enabled'", Approved("contentFilter", "disabled").Validate());
    }

    [Fact]
    public void Invalid_json_is_refused_explicitly()
    {
        Assert.Throws<ModelGateException>(() => ApprovedModelManifest.Parse("{ not json"));
    }

    [Fact]
    public void Authorized_call_trace_records_manifest_version_and_settings_but_not_content()
    {
        const string secretishContent = "Synthetic aircraft evidence v1 with reserve figures.";
        var trace = ModelCallGate.Authorize(Approved(), Synthetic(secretishContent));

        Assert.Equal("2026.10.1", trace.ManifestVersion);
        Assert.Equal("investigation-live", trace.Arm);
        Assert.Equal("fixture-deployment", trace.DeploymentName);
        Assert.Equal("fixture-model", trace.Model);
        Assert.Equal("2025-01-01", trace.ApiVersion);
        Assert.Equal("eastus2", trace.Region);
        Assert.Equal("enabled", trace.ContentFilter);
        Assert.Equal("evidence-v1", trace.EvidenceVersion);
        Assert.DoesNotContain("reserve figures", JsonSerializer.Serialize(trace));
    }

    [Fact]
    public void Request_without_versioned_evidence_is_refused()
    {
        var request = new ModelRequest(null, IsSynthetic: true, DataRegion: null, "Synthetic evidence.");

        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(Approved(), request));
    }

    [Theory]
    [InlineData("api_key=abc123")]
    [InlineData("password: hunter2")]
    [InlineData("Authorization Bearer eyJhbGciOi")]
    [InlineData("AccountKey=Zm9vYmFy")]
    [InlineData("passenger jane.doe@example.com booked seat 12A")]
    [InlineData("call the passenger on +447700900123")]
    [InlineData("passenger phone 555-867-5309 on file")]
    [InlineData("aircraft N123AB returned to gate")]
    public void Synthetic_request_containing_credentials_or_identifiers_is_refused(string content)
    {
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(Approved(), Synthetic(content)));
    }

    [Fact]
    public void Empty_manifest_document_is_refused_explicitly()
    {
        Assert.Throws<ModelGateException>(() => ApprovedModelManifest.Parse("null"));
    }

    [Fact]
    public void Non_synthetic_content_outside_approved_region_is_refused()
    {
        var request = new ModelRequest("evidence-v1", IsSynthetic: false, DataRegion: "westeurope", "Aircraft N123 record.");

        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(Approved(), request));
    }

    [Fact]
    public void Non_synthetic_content_in_approved_region_is_still_scanned_for_identifiers()
    {
        var inRegion = new ModelRequest("evidence-v1", IsSynthetic: false, DataRegion: "EASTUS2", "Aircraft record v1.");
        var withEmail = new ModelRequest("evidence-v1", IsSynthetic: false, DataRegion: "eastus2", "Contact jane.doe@example.com");
        var withTailNumber = new ModelRequest("evidence-v1", IsSynthetic: false, DataRegion: "eastus2", "Aircraft N123 record.");

        Assert.Equal("evidence-v1", ModelCallGate.Authorize(Approved(), inRegion).EvidenceVersion);
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(Approved(), withEmail));
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(Approved(), withTailNumber));
    }
}
