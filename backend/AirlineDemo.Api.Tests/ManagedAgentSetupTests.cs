using System.Text.Json.Nodes;
using AirlineDemo.Api.Foundry;
using AirlineDemo.Deployment;

namespace AirlineDemo.Api.Tests;

public sealed class ManagedAgentSetupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"managed-agents-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Specifications_use_the_production_prompts_and_fixed_function_declarations()
    {
        var specs = ManagedAgentSetup.Specifications(Configuration());
        Assert.Equal(["copilot", "investigator"], specs.Select(spec => spec.Workload));
        Assert.Equal(["airline-copilot", "airline-investigator"], specs.Select(spec => spec.Name));
        Assert.All(specs, spec =>
        {
            Assert.Equal("prompt", spec.Definition["kind"]!.GetValue<string>());
            Assert.Equal("runtime", spec.Definition["model"]!.GetValue<string>());
            Assert.Null(spec.Definition["harness"]);
            Assert.NotEmpty((JsonArray)spec.Definition["tools"]!);
            Assert.Matches("^[0-9a-f]{64}$", spec.DefinitionSha256);
            Assert.Matches("^[0-9a-f]{64}$", spec.ConfigurationId);
        });
        Assert.Equal("json_schema", specs[0].Definition["text"]!["format"]!["type"]!.GetValue<string>());
        Assert.Equal("object", specs[0].Definition["text"]!["format"]!["schema"]!["type"]!.GetValue<string>());
        Assert.Equal("{\"format\":{\"type\":\"text\"}}", specs[1].Definition["text"]!.ToJsonString());
        Assert.Equal(ManagedAgentDefinitionCatalog.InvestigatorInstructions,
            specs[1].Definition["instructions"]!.GetValue<string>());
        Assert.Equal(
            ["advance_stage", "get_approved_requirements", "get_case_inventory", "get_case_summary", "get_open_points",
                "get_processing_status", "get_source_page", "get_source_record", "list_my_cases", "list_source_records",
                "read_source", "search_within_basis"],
            specs.SelectMany(spec => ((JsonArray)spec.Definition["tools"]!).Select(tool => tool!["name"]!.GetValue<string>()))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(specs.SelectMany(spec => ((JsonArray)spec.Definition["tools"]!)
            .Select(tool => tool!["name"]!.GetValue<string>())), name => name.Contains("write", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repeated_publish_reuses_the_exact_versions_without_creating_duplicates()
    {
        var remote = new FakeRemote();
        var setup = Setup(remote);

        var first = await setup.PublishAsync(Configuration());
        var second = await setup.PublishAsync(Configuration());

        Assert.Equal(2, remote.CreateCalls);
        Assert.Equal(
            first["agents"]!.ToJsonString(),
            second["agents"]!.ToJsonString());
        Assert.All(((JsonArray)second["agents"]!).OfType<JsonObject>(),
            agent => Assert.Matches("^[0-9]+$", agent["version"]!.GetValue<string>()));
        Assert.Equal(
            ["advance_stage", "get_approved_requirements", "get_case_inventory", "get_case_summary", "get_open_points",
                "get_processing_status", "get_source_page", "get_source_record", "list_my_cases", "list_source_records",
                "read_source", "search_within_basis"],
            ((JsonArray)second["agents"]!).OfType<JsonObject>()
                .SelectMany(agent => ((JsonArray)agent["toolNames"]!).Select(tool => tool!.GetValue<string>()))
                .Order(StringComparer.Ordinal));
        Assert.All(((JsonArray)second["agents"]!).OfType<JsonObject>(), agent => Assert.Null(agent["tools"]));
        Assert.Equal("demo-medium-high",
            ((JsonArray)second["agents"]!)[0]!["filterPolicy"]!.GetValue<string>());
    }

    [Fact]
    public async Task Changed_definition_digest_fails_closed_without_creating_a_new_version()
    {
        var remote = new FakeRemote();
        var setup = Setup(remote);
        await setup.PublishAsync(Configuration());

        var error = await Assert.ThrowsAsync<ManagedAgentSetupException>(() =>
            setup.PublishAsync(Configuration() with { ModelDeployment = "runtime-next" }));

        Assert.Equal("agent_definition_mismatch", error.Code);
        Assert.Equal(2, remote.CreateCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_null_function_strict_reconciles_exact_versions_without_duplicate_posts(bool lostReply)
    {
        var remote = new FakeRemote
        {
            AddNullFunctionStrict = true,
            LoseFirstCreateReplyAfterAccepting = lostReply,
        };
        var setup = Setup(remote);
        if (lostReply)
        {
            var error = await Assert.ThrowsAsync<ManagedAgentSetupException>(() => setup.PublishAsync(Configuration()));
            Assert.Equal("agent_create_outcome_uncertain", error.Code);
        }

        var manifest = await setup.PublishAsync(Configuration());
        var repeated = await setup.PublishAsync(Configuration());

        Assert.Equal(2, remote.CreateCalls);
        Assert.Equal(manifest.ToJsonString(), repeated.ToJsonString());
        Assert.Equal(["1", "2"], manifest["agents"]!.AsArray()
            .Select(agent => agent!["version"]!.GetValue<string>()));
        Assert.All(ManagedAgentSetup.Specifications(Configuration()), spec =>
            Assert.Null(new FileManagedAgentPendingStore(Path.Combine(_directory, "state")).Read(spec.Name)));
    }

    [Theory]
    [InlineData("strict", "false")]
    [InlineData("strict", "true")]
    [InlineData("unknown", "null")]
    public async Task Non_null_strict_and_unknown_tool_fields_are_not_normalized(string field, string value)
    {
        var remote = new FakeRemote();
        var spec = ManagedAgentSetup.Specifications(Configuration())[0];
        var definition = spec.Definition.DeepClone().AsObject();
        definition["tools"]![0]![field] = JsonNode.Parse(value);
        remote.Seed(spec.Name, "1", definition);

        var error = await Assert.ThrowsAsync<ManagedAgentSetupException>(() =>
            Setup(remote).PublishAsync(Configuration()));

        Assert.Equal("agent_definition_mismatch", error.Code);
        Assert.Equal(0, remote.CreateCalls);
        Assert.False(File.Exists(Configuration().ManifestPath));
    }

    [Fact]
    public async Task Missing_filter_policy_refuses_publication_before_agent_creation()
    {
        var remote = new FakeRemote();
        var configuration = Configuration() with { FilterPolicy = "" };
        var error = await Assert.ThrowsAsync<ManagedAgentSetupException>(() =>
            Setup(remote).PublishAsync(configuration));

        Assert.Equal("agent_setup_configuration_invalid", error.Code);
        Assert.Equal(0, remote.CreateCalls);
        Assert.False(File.Exists(configuration.ManifestPath));
    }

    [Fact]
    public async Task Changed_remote_filter_policy_refuses_publication_without_replacing_the_agent()
    {
        var remote = new FakeRemote();
        var spec = ManagedAgentSetup.Specifications(Configuration())[0];
        var definition = spec.Definition.DeepClone().AsObject();
        definition["filterPolicy"] = "demo-low";
        remote.Seed(spec.Name, "1", definition);
        var configuration = Configuration();
        var error = await Assert.ThrowsAsync<ManagedAgentSetupException>(() =>
            Setup(remote).PublishAsync(configuration));

        Assert.Equal("agent_definition_mismatch", error.Code);
        Assert.Equal(0, remote.CreateCalls);
        Assert.False(File.Exists(configuration.ManifestPath));
    }

    [Fact]
    public async Task Lost_create_reply_is_reconciled_without_a_second_post()
    {
        var remote = new FakeRemote { LoseFirstCreateReplyAfterAccepting = true };
        var setup = Setup(remote);
        var first = ManagedAgentSetup.Specifications(Configuration())[0];

        var error = await Assert.ThrowsAsync<ManagedAgentSetupException>(() => setup.PublishAsync(Configuration()));
        Assert.Equal("agent_create_outcome_uncertain", error.Code);
        Assert.True(error.Uncertain);
        Assert.Equal(first.DefinitionSha256, new FileManagedAgentPendingStore(Path.Combine(_directory, "state"))
            .Read(first.Name));

        var manifest = await setup.PublishAsync(Configuration());

        Assert.Equal(2, remote.CreateCalls);
        Assert.Contains(((JsonArray)manifest["agents"]!).OfType<JsonObject>(),
            agent => agent["workload"]!.GetValue<string>() == first.Workload &&
                agent["version"]!.GetValue<string>() == "1");
        Assert.Null(new FileManagedAgentPendingStore(Path.Combine(_directory, "state")).Read(first.Name));
    }

    [Fact]
    public async Task Unresolved_create_reply_stays_pending_and_is_never_retried()
    {
        var remote = new FakeRemote { LoseFirstCreateReplyWithoutAccepting = true };
        var setup = Setup(remote);

        _ = await Assert.ThrowsAsync<ManagedAgentSetupException>(() => setup.PublishAsync(Configuration()));
        var error = await Assert.ThrowsAsync<ManagedAgentSetupException>(() => setup.PublishAsync(Configuration()));

        Assert.Equal("agent_create_unresolved", error.Code);
        Assert.Equal(1, remote.CreateCalls);
    }

    [Fact]
    public async Task Existing_different_agent_definition_is_not_overwritten_or_resolved_to_latest()
    {
        var remote = new FakeRemote();
        var spec = ManagedAgentSetup.Specifications(Configuration())[0];
        remote.Seed(spec.Name, "1", new JsonObject { ["kind"] = "prompt", ["model"] = "other" });
        var setup = Setup(remote);

        var error = await Assert.ThrowsAsync<ManagedAgentSetupException>(() => setup.PublishAsync(Configuration()));

        Assert.Equal("agent_definition_mismatch", error.Code);
        Assert.Equal(0, remote.CreateCalls);
    }

    [Fact]
    public void Remote_refuses_non_foundry_or_non_project_endpoints()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new ManagedAgentRemote(
            new Uri("https://example.invalid/api/projects/airline-demo"), http, new FakeCredential()));
        Assert.Throws<ArgumentException>(() => new ManagedAgentRemote(
            new Uri("https://demo.services.ai.azure.com/api/projects/other?x=1"), http, new FakeCredential()));
    }

    private ManagedAgentSetup Setup(FakeRemote remote) =>
        new(remote, new FileManagedAgentPendingStore(Path.Combine(_directory, "state")));

    private ManagedAgentSetupConfiguration Configuration() =>
        new(new Uri("https://demo.services.ai.azure.com/api/projects/airline-demo"), "airline-demo",
            "11111111-2222-3333-4444-555555555555", "runtime", "model-a", "2026-01-01",
            "demo-medium-high", Path.Combine(_directory, "release-manifest.json"),
            Path.Combine(_directory, "state"));

    private sealed class FakeRemote : IManagedAgentRemote
    {
        private readonly Dictionary<string, List<JsonObject>> _versions = new(StringComparer.Ordinal);
        public int CreateCalls { get; private set; }
        public bool LoseFirstCreateReplyAfterAccepting { get; init; }
        public bool LoseFirstCreateReplyWithoutAccepting { get; init; }
        public bool AddNullFunctionStrict { get; init; }

        public Task<JsonObject> ListVersionsAsync(string name, string? after, CancellationToken cancellationToken)
        {
            var data = new JsonArray();
            if (_versions.TryGetValue(name, out var versions))
            {
                foreach (var version in versions)
                    data.Add(version.DeepClone());
            }
            return Task.FromResult(new JsonObject { ["data"] = data, ["has_more"] = false });
        }

        public Task<JsonObject> CreateVersionAsync(string name, JsonObject request, CancellationToken cancellationToken)
        {
            CreateCalls++;
            if (CreateCalls == 1 && LoseFirstCreateReplyWithoutAccepting)
                throw new ManagedAgentSetupException("agent_create_outcome_uncertain", uncertain: true);

            var version = CreateCalls == 1 ? "1" : "2";
            var created = new JsonObject
            {
                ["object"] = "agent.version",
                ["id"] = $"agent-{name}-{version}",
                ["name"] = name,
                ["version"] = version,
                ["created_at"] = 1,
                ["definition"] = request["definition"]!.DeepClone(),
            };
            if (AddNullFunctionStrict)
            {
                foreach (var tool in created["definition"]!["tools"]!.AsArray().OfType<JsonObject>())
                    tool["strict"] = null;
            }
            if (!_versions.TryGetValue(name, out var existing))
                _versions[name] = existing = [];
            existing.Add(created.DeepClone().AsObject());

            if (CreateCalls == 1 && LoseFirstCreateReplyAfterAccepting)
                throw new ManagedAgentSetupException("agent_create_outcome_uncertain", uncertain: true);
            return Task.FromResult(created);
        }

        public void Seed(string name, string version, JsonObject definition)
        {
            _versions[name] = [new JsonObject
            {
                ["object"] = "agent.version",
                ["id"] = $"agent-{name}-{version}",
                ["name"] = name,
                ["version"] = version,
                ["created_at"] = 1,
                ["definition"] = definition,
            }];
        }
    }

    private sealed class FakeCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(
            Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
