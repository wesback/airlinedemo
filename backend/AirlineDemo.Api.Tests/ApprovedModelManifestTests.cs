using AirlineDemo.Api.Investigation;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AirlineDemo.Api.Tests;

/// <summary>Covers M-18 (manifest gating) and M-07 (SHA-256 registered synthetic content, in-region model requests).</summary>
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

    private const string ApprovedRegion = "eastus2";
    private const string DefaultBody = "aircraft evidence v1.";

    private static ApprovedModelManifest Approved() => ApprovedModelManifest.Parse(ApprovedJson);

    private static ApprovedModelManifest Approved(string field, JsonNode? value)
    {
        var node = JsonNode.Parse(ApprovedJson)!.AsObject();
        node[field] = value;
        return ApprovedModelManifest.Parse(node.ToJsonString());
    }

    private static string Marked(string body) => ModelCallGate.SyntheticMarker + ": " + body;

    private static ModelRequest Synthetic(string body = DefaultBody, string region = ApprovedRegion) =>
        new("evidence-v1", region, Marked(body));

    /// <summary>Registry built only from the committed synthetic fixtures compiled into the API assembly.</summary>
    private static SyntheticContentRegistry Registry() => SyntheticContentRegistry.FromCommittedFixtures();

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
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(manifest, Synthetic(), Registry()));
    }

    [Fact]
    public void Approved_complete_manifest_is_usable()
    {
        var manifest = Approved();

        Assert.Empty(manifest.Validate());
        Assert.Same(manifest, manifest.RequireUsable());
    }

    [Theory]
    [InlineData("manifestVersion")]
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
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(manifest, Synthetic(), Registry()));
    }

    [Fact]
    public void Unapproved_manifest_is_refused_even_with_complete_values()
    {
        var manifest = Approved("approved", (JsonNode?)false);

        Assert.Contains("manifest is not approved", manifest.Validate());
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(manifest, Synthetic(), Registry()));
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
    public void Empty_manifest_document_is_refused_explicitly()
    {
        Assert.Throws<ModelGateException>(() => ApprovedModelManifest.Parse("null"));
    }

    [Fact]
    public void Authorized_call_trace_records_manifest_version_and_settings_but_not_content()
    {
        const string body = "Synthetic aircraft evidence v1 with reserve figures.";
        var trace = ModelCallGate.Authorize(Approved(), Synthetic(body), Registry());

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
        var request = new ModelRequest(null, DataRegion: null, Marked("evidence."));

        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(Approved(), request, Registry()));
    }

    [Fact]
    public void Registered_synthetic_content_is_authorized()
    {
        var trace = ModelCallGate.Authorize(Approved(), Synthetic(), Registry());

        Assert.Equal("evidence-v1", trace.EvidenceVersion);
    }

    [Theory]
    [InlineData("passenger Jane Smith in seat 12A")]
    [InlineData("date of birth 1990 recorded")]
    [InlineData("Mr Jones reviewed the file")]
    [InlineData("booking reference 0012345678")]
    [InlineData("crew member SSN 123 45 6789 on record")]
    [InlineData("aircraft N123AB returned to gate")]
    [InlineData("call the passenger on 07700 900123")]
    [InlineData("password hunter2 for the portal")]
    public void Unregistered_content_is_refused_whatever_it_looks_like(string body)
    {
        var ex = Assert.Throws<ModelGateException>(() =>
            ModelCallGate.Authorize(Approved(), Synthetic(body), Registry()));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void Unregistered_content_is_refused_when_registry_is_empty()
    {
        var ex = Assert.Throws<ModelGateException>(() =>
            ModelCallGate.Authorize(Approved(), Synthetic(), new SyntheticContentRegistry()));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void Content_differing_by_one_character_from_registered_content_is_refused()
    {
        var ex = Assert.Throws<ModelGateException>(() =>
            ModelCallGate.Authorize(Approved(), Synthetic("aircraft evidence v1 ."), Registry()));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void Registry_hashes_content_with_sha256()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            SyntheticContentRegistry.Sha256Hex("abc"));

        var registry = Registry();
        Assert.True(registry.IsRegistered(Marked("abc")));
        Assert.False(registry.IsRegistered(Marked("abd")));
        Assert.False(new SyntheticContentRegistry().IsRegistered(Marked("abc")));
    }

    [Fact]
    public void Registry_contains_only_committed_marked_fixtures()
    {
        var registry = Registry();

        Assert.True(registry.IsRegistered(Marked(DefaultBody)));
        Assert.True(registry.IsRegistered(Marked("Synthetic evidence A.")));
        Assert.False(registry.IsRegistered(Marked("passenger Jane Smith in seat 12A")));
        Assert.False(registry.IsRegistered("passenger Jane Smith in seat 12A"));
    }

    [Fact]
    public void Registry_cannot_be_filled_from_a_caller_supplied_directory()
    {
        var method = typeof(SyntheticContentRegistry).GetMethod("FromFixtureDirectory");

        Assert.Null(method);
    }

    [Theory]
    [InlineData("record Jane at Gate#12")]
    [InlineData("route ABC/DEF cancelled")]
    [InlineData("reference id_77 pending")]
    [InlineData("status=open")]
    [InlineData("name O'Brien\u00e9 on file")]
    public void Synthetic_content_outside_the_plain_text_allowlist_is_refused(string body)
    {
        var ex = Assert.Throws<ModelGateException>(() =>
            ModelCallGate.Authorize(Approved(), Synthetic(body), Registry()));
        Assert.Contains("allowlist", ex.Message);
    }

    [Fact]
    public void Synthetic_flag_without_synthetic_marker_is_refused()
    {
        var request = new ModelRequest("evidence-v1", ApprovedRegion, "aircraft record v1.");

        var ex = Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(Approved(), request, Registry()));
        Assert.Contains("not marked as synthetic", ex.Message);
    }

    [Theory]
    [InlineData("model", "STUB-model")]
    [InlineData("arm", "shadow-run")]
    [InlineData("contentFilter", "disabled")]
    [InlineData("approved", false)]
    [InlineData("deploymentName", null)]
    public void Every_unusable_manifest_refuses_model_call_before_data_is_sent(string field, object? value)
    {
        var manifest = Approved(field, value is null ? null : JsonValue.Create(value));

        Assert.NotEmpty(manifest.Validate());
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(manifest, Synthetic(), Registry()));
    }

    [Fact]
    public void Registered_content_does_not_waive_the_approved_region()
    {
        Assert.Throws<ModelGateException>(() =>
            ModelCallGate.Authorize(Approved(), Synthetic(region: "westeurope"), Registry()));
    }

    [Fact]
    public void Region_comparison_is_case_insensitive()
    {
        var trace = ModelCallGate.Authorize(Approved(), Synthetic(region: "EASTUS2"), Registry());

        Assert.Equal("evidence-v1", trace.EvidenceVersion);
    }

    [Fact]
    public void Scored_run_with_all_registered_requests_returns_one_trace_per_request()
    {
        var requests = new[] { Synthetic("Synthetic evidence A."), Synthetic("Synthetic evidence B.") };

        var traces = ModelCallGate.AuthorizeScoredRun(Approved(), requests, Registry());

        Assert.Equal(2, traces.Count);
        Assert.All(traces, t => Assert.Equal("2026.10.1", t.ManifestVersion));
    }

    [Fact]
    public void Scored_run_is_refused_when_any_request_is_unregistered()
    {
        var requests = new[] { Synthetic("Synthetic evidence A."), Synthetic("Synthetic evidence C.") };

        Assert.Throws<ModelGateException>(() =>
            ModelCallGate.AuthorizeScoredRun(Approved(), requests, Registry()));
    }

    [Fact]
    public void Scored_run_is_refused_when_any_request_lacks_synthetic_marker()
    {
        var unmarked = new ModelRequest("evidence-v1", ApprovedRegion, "aircraft record v1.");

        Assert.Throws<ModelGateException>(() =>
            ModelCallGate.AuthorizeScoredRun(Approved(), [Synthetic(), unmarked], Registry()));
    }

    [Fact]
    public void Scored_run_is_refused_for_unusable_manifest_or_empty_run()
    {
        Assert.Throws<ModelGateException>(() =>
            ModelCallGate.AuthorizeScoredRun(Approved("approved", (JsonNode?)false), [Synthetic()], Registry()));
        Assert.Throws<ModelGateException>(() =>
            ModelCallGate.AuthorizeScoredRun(Approved(), [], Registry()));
    }
}
