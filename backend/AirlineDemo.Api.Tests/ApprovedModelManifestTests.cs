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
          "modelVersion": "2026-09-22",
          "apiVersion": "v1",
          "region": "swedencentral",
          "contentFilter": "enabled"
        }
        """;

    private const string ApprovedRegion = "swedencentral";
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
        var path = Path.Combine(AppContext.BaseDirectory, "config", "model-manifest.json");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("config/model-manifest.json was not copied beside the test binaries", path);
    }

    [Fact]
    public void Shipped_manifest_is_approved_candidate_and_usable()
    {
        var manifest = ApprovedModelManifest.Load(ShippedManifestPath());

        Assert.True(manifest.Approved);
        Assert.Equal("gpt-6-luna", manifest.DeploymentName);
        var errors = manifest.Validate();
        Assert.Empty(errors);
        manifest.RequireUsable();
    }

    [Fact]
    public void Approved_complete_manifest_is_usable()
    {
        var manifest = Approved();

        Assert.Empty(manifest.Validate());
        Assert.Same(manifest, manifest.RequireUsable());
    }

    [Theory]
    [InlineData("deploymentName", "sk-live-abc123def456")]
    [InlineData("model", "https://user:secret@example.test/model")]
    [InlineData("region", "east us 2;token=abc")]
    [InlineData("manifestVersion", "2026.10.1 Bearer xyz")]
    [InlineData("apiVersion", "myApiKeyValue")]
    [InlineData("deploymentName", "Qz7mR2pL9xVb4kTn8WcF")]
    [InlineData("model", "fixture-model-0a1b2c3d4e5f6g7h8i9j")]
    [InlineData("modelVersion", "2026-99-99")]
    [InlineData("modelVersion", "2026-09-22-preview")]
    [InlineData("region", "janesmith")]
    [InlineData("region", "Jane Smith")]
    [InlineData("apiVersion", "2025-01-01-Jane")]
    public void Credential_or_non_identifier_values_are_refused(string field, string value)
    {
        var manifest = Approved(field, value);

        Assert.Contains($"{field} is not a plain identifier", manifest.Validate());
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(manifest, Synthetic(), Registry()));
    }

    [Theory]
    [InlineData("manifestVersion")]
    [InlineData("deploymentName")]
    [InlineData("model")]
    [InlineData("modelVersion")]
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
    public void Stub_manifest_version_is_refused_even_when_approved()
    {
        var manifest = Approved("manifestVersion", "STUB-1.0.0");

        Assert.Contains("manifestVersion is still a stub value", manifest.Validate());
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(manifest, Synthetic(), Registry()));
    }

    [Theory]
    [InlineData("jane.smith@example.com")]
    [InlineData("evidence v1")]
    [InlineData("name O'Brien")]
    public void Evidence_version_that_is_not_a_plain_identifier_is_refused(string evidenceVersion)
    {
        var request = new ModelRequest(evidenceVersion, ApprovedRegion, Marked(DefaultBody));

        var ex = Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(Approved(), request, Registry()));
        Assert.Contains("plain version identifier", ex.Message);
    }

    [Fact]
    public void Unsupported_arm_and_disabled_content_filter_are_refused()
    {
        Assert.Contains("arm 'shadow-run' is not supported", Approved("arm", "shadow-run").Validate());
        Assert.Contains("contentFilter must be 'enabled'", Approved("contentFilter", "disabled").Validate());
    }

    [Fact]
    public void Dated_api_version_is_refused()
    {
        var manifest = Approved("apiVersion", "2024-10-21");

        Assert.Contains("apiVersion is not a plain identifier", manifest.Validate());
        Assert.Throws<ModelGateException>(() => manifest.RequireUsable());
    }

    [Fact]
    public void Approved_manifest_requires_exact_configured_model_settings()
    {
        var manifest = Approved();

        manifest.RequireConfigurationMatches("fixture-deployment", "fixture-model", "2026-09-22");
        Assert.Throws<ModelGateException>(() =>
            manifest.RequireConfigurationMatches("other-deployment", "fixture-model", "2026-09-22"));
        Assert.Throws<ModelGateException>(() =>
            manifest.RequireConfigurationMatches("fixture-deployment", "other-model", "2026-09-22"));
        Assert.Throws<ModelGateException>(() =>
            manifest.RequireConfigurationMatches("fixture-deployment", "fixture-model", "2026-09-23"));
        Assert.Throws<ModelGateException>(() =>
            manifest.RequireConfigurationMatches("fixture-deployment", "fixture-model", null));
    }

    [Fact]
    public void Unapproved_manifest_does_not_require_deployment_settings()
    {
        Approved("approved", (JsonNode?)false)
            .RequireConfigurationMatches(null, null, null);
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
        Assert.Equal("2026-09-22", trace.ModelVersion);
        Assert.Equal("v1", trace.ApiVersion);
        Assert.Equal("swedencentral", trace.Region);
        Assert.Equal("enabled", trace.ContentFilter);
        Assert.Equal(SyntheticContentRegistry.Sha256Hex(Marked(body)), Assert.Single(trace.EvidenceSha256));
        Assert.DoesNotContain("reserve figures", JsonSerializer.Serialize(trace));
    }

    [Fact]
    public void Caller_supplied_evidence_version_is_not_recorded_in_the_trace()
    {
        var request = Synthetic() with { EvidenceVersion = "api-key-9f8e7d" };
        var trace = ModelCallGate.Authorize(Approved(), request, Registry());

        Assert.DoesNotContain("api-key-9f8e7d", JsonSerializer.Serialize(trace));
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

        Assert.All(trace.EvidenceSha256, h => Assert.Matches("^[0-9a-f]{64}$", h));
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
    public void Region_is_fixed_to_sweden_central()
    {
        Assert.Empty(Approved().Validate());

        foreach (var region in new[]
        {
            "eastus2", "northeurope", "westeurope", "southcentralus", "eastus", "westus", "westus2", "westus3",
            "centralus", "uksouth", "ukwest", "", " ", "SwedenCentral", "swedencentral ", "unknown-region-1"
        })
        {
            Assert.NotEmpty(Approved("region", region).Validate());
        }
    }

    [Fact]
    public void Model_request_must_match_sweden_central_manifest_region()
    {
        var manifest = Approved();

        var trace = ModelCallGate.Authorize(manifest, Synthetic(region: "swedencentral"), Registry());
        Assert.Equal("swedencentral", trace.Region);

        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(manifest, Synthetic(region: "westeurope"), Registry()));
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(manifest, Synthetic(region: "eastus2"), Registry()));
        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(manifest, Synthetic(region: ""), Registry()));
        Assert.Throws<ModelGateException>(() =>
            ModelCallGate.Authorize(manifest, new ModelRequest("evidence-v1", null, Marked(DefaultBody)), Registry()));
    }

    [Fact]
    public void Shipped_manifest_matches_approved_candidate_values()
    {
        var manifest = ApprovedModelManifest.Load(ShippedManifestPath());

        Assert.True(manifest.Approved);
        Assert.Equal("investigation-live", manifest.Arm);
        Assert.Equal("gpt-6-luna", manifest.DeploymentName);
        Assert.Equal("gpt-6-luna", manifest.Model);
        Assert.Equal("2026-09-22", manifest.ModelVersion);
        Assert.Equal("v1", manifest.ApiVersion);
        Assert.Equal("swedencentral", manifest.Region);
        Assert.Equal("enabled", manifest.ContentFilter);
        Assert.Empty(manifest.Validate());
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

    [Fact]
    public void Multi_item_request_traces_each_registered_evidence_hash_in_order()
    {
        var first = Marked("Synthetic evidence A.");
        var second = Marked("Synthetic evidence B.");
        var request = new ModelRequest("evidence-v1", ApprovedRegion, [first, second]);

        var trace = ModelCallGate.Authorize(Approved(), request, Registry());

        Assert.Equal(
            new[] { SyntheticContentRegistry.Sha256Hex(first), SyntheticContentRegistry.Sha256Hex(second) },
            trace.EvidenceSha256.ToArray());
    }

    [Fact]
    public void Multi_item_request_is_refused_when_any_single_item_is_unregistered()
    {
        var request = new ModelRequest("evidence-v1", ApprovedRegion,
            [Marked("Synthetic evidence A."), Marked("Synthetic evidence C.")]);

        var ex = Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(Approved(), request, Registry()));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void Multi_item_request_is_refused_when_any_single_item_lacks_synthetic_marker()
    {
        var request = new ModelRequest("evidence-v1", ApprovedRegion,
            [Marked(DefaultBody), "aircraft record v1."]);

        var ex = Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(Approved(), request, Registry()));
        Assert.Contains("not marked as synthetic", ex.Message);
    }

    [Fact]
    public void Request_with_no_evidence_items_is_refused()
    {
        var request = new ModelRequest("evidence-v1", ApprovedRegion, []);

        Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(Approved(), request, Registry()));
    }
}
