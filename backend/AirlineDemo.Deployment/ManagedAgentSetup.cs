using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Foundry;
using AirlineDemo.Api.Investigation;
using Azure.Core;
using Azure.Identity;

namespace AirlineDemo.Deployment;

public sealed record ManagedAgentSetupConfiguration(
    Uri ProjectEndpoint,
    string ProjectName,
    string PublisherClientId,
    string ModelDeployment,
    string ModelName,
    string ModelVersion,
    string FilterPolicy,
    string ManifestPath,
    string StateDirectory);

public sealed record ManagedAgentSpecification(
    string Workload,
    string Name,
    string Description,
    JsonObject Definition,
    string ModelDeployment,
    string ModelName,
    string ModelVersion,
    string FilterPolicy,
    string ApiVersion,
    string DotnetVersion,
    string AzureIdentityVersion)
{
    public string DefinitionSha256 => ManagedAgentSetup.Hash(Definition);

    public string ConfigurationId => ManagedAgentSetup.Hash(new JsonObject
    {
        ["workload"] = Workload,
        ["name"] = Name,
        ["definitionSha256"] = DefinitionSha256,
        ["modelDeployment"] = ModelDeployment,
        ["modelName"] = ModelName,
        ["modelVersion"] = ModelVersion,
        ["filterPolicy"] = FilterPolicy,
        ["apiVersion"] = ApiVersion,
        ["sdkVersions"] = new JsonObject
        {
            ["dotnet"] = DotnetVersion,
            ["azureIdentity"] = AzureIdentityVersion,
        },
    });
}

public interface IManagedAgentRemote
{
    Task<JsonObject> ListVersionsAsync(string name, string? after, CancellationToken cancellationToken);
    Task<JsonObject> CreateVersionAsync(string name, JsonObject request, CancellationToken cancellationToken);
}

public sealed class ManagedAgentSetupException(string code, bool uncertain = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool Uncertain { get; } = uncertain;
}

public interface IManagedAgentPendingStore
{
    string? Read(string name);
    void Write(string name, string definitionSha256);
    void Clear(string name);
}

public sealed class FileManagedAgentPendingStore(string directory) : IManagedAgentPendingStore
{
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9-]{0,62}\\z", RegexOptions.CultureInvariant);

    public string? Read(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                root.GetProperty("name").GetString() != name ||
                !Regex.IsMatch(root.GetProperty("definitionSha256").GetString() ?? "", "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
                throw new JsonException();
            return root.GetProperty("definitionSha256").GetString();
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
            throw new ManagedAgentSetupException("agent_setup_state_invalid");
        }
    }

    public void Write(string name, string definitionSha256)
    {
        if (!Regex.IsMatch(definitionSha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            throw new ManagedAgentSetupException("agent_setup_state_invalid");
        Directory.CreateDirectory(directory);
        var path = PathFor(name);
        var temporary = Path.Combine(directory, $".{name}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(new { name, definitionSha256 }));
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Clear(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string name) =>
        NamePattern.IsMatch(name) ? Path.Combine(directory, name + ".pending.json")
            : throw new ManagedAgentSetupException("agent_setup_state_invalid");
}

public sealed class ManagedAgentRemote : IManagedAgentRemote
{
    private const string Scope = "https://ai.azure.com/.default";
    private readonly Uri _endpoint;
    private readonly HttpClient _client;
    private readonly TokenCredential _credential;

    public ManagedAgentRemote(Uri projectEndpoint, HttpClient client, TokenCredential credential)
    {
        if (projectEndpoint.Scheme != "https" || projectEndpoint.UserInfo.Length != 0 ||
            projectEndpoint.Query.Length != 0 || projectEndpoint.Fragment.Length != 0 ||
            !projectEndpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(projectEndpoint.AbsolutePath, "^/api/projects/[A-Za-z0-9_-]{1,128}/?$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Expected a direct HTTPS Foundry project endpoint.", nameof(projectEndpoint));
        _endpoint = new Uri(projectEndpoint.AbsoluteUri.TrimEnd('/'));
        _client = client;
        _credential = credential;
    }

    public Task<JsonObject> ListVersionsAsync(string name, string? after, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, AgentPath(name) + "?api-version=v1&limit=100&order=asc" +
            (after is null ? "" : "&after=" + Uri.EscapeDataString(Identifier(after))), null, false, cancellationToken);

    public Task<JsonObject> CreateVersionAsync(string name, JsonObject request, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, AgentPath(name) + "?api-version=v1", request, true, cancellationToken);

    private async Task<JsonObject> SendAsync(
        HttpMethod method, string path, JsonObject? body, bool write, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        AccessToken token;
        try
        {
            token = await _credential.GetTokenAsync(new TokenRequestContext([Scope]), timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is AuthenticationFailedException or InvalidOperationException or OperationCanceledException)
        {
            throw new ManagedAgentSetupException("agent_publish_not_authorized");
        }

        using var request = new HttpRequestMessage(method, _endpoint.AbsoluteUri.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ManagedAgentSetupException("agent_publish_not_authorized");
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 500 && write)
                    throw new ManagedAgentSetupException("agent_create_outcome_uncertain", uncertain: true);
                throw new ManagedAgentSetupException(write ? "agent_publish_rejected" : "agent_publish_unavailable");
            }
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject
                ?? throw new JsonException();
        }
        catch (ManagedAgentSetupException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            throw new ManagedAgentSetupException(write ? "agent_create_outcome_uncertain" : "agent_publish_unavailable", uncertain: write);
        }
    }

    private static string AgentPath(string name) =>
        "/agents/" + Uri.EscapeDataString(Identifier(name)) + "/versions";

    private static string Identifier(string value) =>
        Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$", RegexOptions.CultureInvariant)
            ? value
            : throw new ManagedAgentSetupException("agent_service_contract_invalid");
}

public sealed class ManagedAgentSetup(
    IManagedAgentRemote remote,
    IManagedAgentPendingStore pendingStore)
{
    private const string ApiVersion = "v1";
    private static readonly Regex VersionPattern = new("^[0-9]+\\z", RegexOptions.CultureInvariant);

    public static IReadOnlyList<ManagedAgentSpecification> Specifications(ManagedAgentSetupConfiguration config)
    {
        ValidateConfiguration(config);
        var framework = typeof(ManagedAgentSetup).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName
            ?? throw new ManagedAgentSetupException("agent_setup_configuration_invalid");
        var dotnetVersion = framework.Replace(".NETCoreApp,Version=v", "net", StringComparison.Ordinal);
        var identityVersion = typeof(ManagedIdentityCredential).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? throw new ManagedAgentSetupException("agent_setup_configuration_invalid");
        return
        [
            Specification(config, "copilot", "airline-copilot", "Read-only airline case copilot.",
                ManagedAgentDefinitionCatalog.CopilotInstructions, ManagedAgentDefinitionCatalog.CopilotTools(),
                CopilotTextFormat(), dotnetVersion, identityVersion),
            Specification(config, "investigator", "airline-investigator", "Evidence-bound airline case investigator.",
                ManagedAgentDefinitionCatalog.InvestigatorInstructions, ManagedAgentDefinitionCatalog.InvestigatorTools(),
                InvestigatorTextFormat(), dotnetVersion, identityVersion),
        ];
    }

    public static JsonObject Preview(ManagedAgentSetupConfiguration config)
    {
        var agents = new JsonArray();
        foreach (var spec in Specifications(config))
        {
            agents.Add(new JsonObject
            {
                ["workload"] = spec.Workload,
                ["name"] = spec.Name,
                ["definitionSha256"] = spec.DefinitionSha256,
                ["configurationId"] = spec.ConfigurationId,
                ["modelDeployment"] = spec.ModelDeployment,
                ["modelName"] = spec.ModelName,
                ["modelVersion"] = spec.ModelVersion,
                ["filterPolicy"] = spec.FilterPolicy,
            });
        }
        return new JsonObject
        {
            ["projectName"] = config.ProjectName,
            ["projectEndpoint"] = config.ProjectEndpoint.AbsoluteUri,
            ["agents"] = agents,
        };
    }

    public async Task<JsonObject> PublishAsync(ManagedAgentSetupConfiguration config, CancellationToken cancellationToken = default)
    {
        var specs = Specifications(config);
        var manifestPath = Path.GetFullPath(config.ManifestPath);
        var previousManifest = ReadExistingManifest(manifestPath, config, specs);
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var found = await FindVersionAsync(spec, cancellationToken);
            if (previousManifest is not null)
            {
                var previousVersion = PreviousVersion(previousManifest, spec.Workload);
                if (found.Version != previousVersion)
                    throw new ManagedAgentSetupException("agent_definition_mismatch");
            }
            versions.Add(spec.Workload, found.Version);
        }

        var manifest = BuildManifest(config, specs, versions);
        WriteManifest(manifestPath, manifest);
        return manifest;
    }

    private async Task<(string Version, JsonObject Agent)> FindVersionAsync(
        ManagedAgentSpecification spec, CancellationToken cancellationToken)
    {
        var pending = pendingStore.Read(spec.Name);
        if (pending is not null && pending != spec.DefinitionSha256)
            throw new ManagedAgentSetupException("agent_definition_mismatch");
        var versions = await ReadAllVersionsAsync(spec.Name, cancellationToken);
        var matches = versions.Where(agent => Hash(agent["definition"]!) == spec.DefinitionSha256).ToArray();
        if (pending is not null)
        {
            if (matches.Length != 1)
                throw new ManagedAgentSetupException("agent_create_unresolved");
            pendingStore.Clear(spec.Name);
            return (RequiredVersion(matches[0]), matches[0]);
        }
        if (matches.Length > 1)
            throw new ManagedAgentSetupException("agent_definition_ambiguous");
        if (matches.Length == 1)
            return (RequiredVersion(matches[0]), matches[0]);
        if (versions.Count > 0)
            throw new ManagedAgentSetupException("agent_definition_mismatch");

        pendingStore.Write(spec.Name, spec.DefinitionSha256);
        JsonObject created;
        try
        {
            created = await remote.CreateVersionAsync(spec.Name, CreateRequest(spec), cancellationToken);
        }
        catch (ManagedAgentSetupException exception)
        {
            if (!exception.Uncertain) pendingStore.Clear(spec.Name);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new ManagedAgentSetupException("agent_create_outcome_uncertain", uncertain: true);
        }
        if (!Matches(created, spec))
            throw new ManagedAgentSetupException("agent_create_outcome_uncertain", uncertain: true);
        pendingStore.Clear(spec.Name);
        return (RequiredVersion(created), created);
    }

    private async Task<List<JsonObject>> ReadAllVersionsAsync(string name, CancellationToken cancellationToken)
    {
        var result = new List<JsonObject>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var page = await remote.ListVersionsAsync(name, after, cancellationToken);
            if (page["data"] is not JsonArray data || data.Count > 100 ||
                page["has_more"] is not JsonValue moreValue || !moreValue.TryGetValue<bool>(out var more))
                throw new ManagedAgentSetupException("agent_service_contract_invalid");
            string? last = null;
            foreach (var node in data)
            {
                if (node is not JsonObject agent || !MatchesName(agent, name) || agent["definition"] is not JsonObject ||
                    !VersionPattern.IsMatch(agent["version"]?.GetValue<string>() ?? ""))
                    throw new ManagedAgentSetupException("agent_service_contract_invalid");
                result.Add(agent);
                last = agent["id"]?.GetValue<string>();
            }
            if (!more) return result;
            if (last is null || page["last_id"]?.GetValue<string>() != last || !cursors.Add(last))
                throw new ManagedAgentSetupException("agent_service_contract_invalid");
            after = last;
        }
        throw new ManagedAgentSetupException("agent_service_contract_invalid");
    }

    private JsonObject BuildManifest(
        ManagedAgentSetupConfiguration config,
        IReadOnlyList<ManagedAgentSpecification> specs,
        IReadOnlyDictionary<string, string> versions)
    {
        var agents = new JsonArray();
        foreach (var spec in specs)
        {
            var toolNames = new JsonArray();
            foreach (var tool in spec.Definition["tools"]!.AsArray())
                toolNames.Add(tool!["name"]!.GetValue<string>());
            agents.Add(new JsonObject
            {
                ["workload"] = spec.Workload,
                ["name"] = spec.Name,
                ["version"] = versions[spec.Workload],
                ["definitionSha256"] = spec.DefinitionSha256,
                ["configurationId"] = spec.ConfigurationId,
                ["modelDeployment"] = spec.ModelDeployment,
                ["modelName"] = spec.ModelName,
                ["modelVersion"] = spec.ModelVersion,
                ["filterPolicy"] = spec.FilterPolicy,
                ["apiVersion"] = spec.ApiVersion,
                ["sdkVersions"] = new JsonObject
                {
                    ["dotnet"] = spec.DotnetVersion,
                    ["azureIdentity"] = spec.AzureIdentityVersion,
                },
                ["toolNames"] = toolNames,
            });
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["projectName"] = config.ProjectName,
            ["projectEndpoint"] = config.ProjectEndpoint.AbsoluteUri,
            ["agents"] = agents,
        };
    }

    private static JsonObject CreateRequest(ManagedAgentSpecification spec) => new()
    {
        ["description"] = spec.Description,
        ["definition"] = spec.Definition.DeepClone(),
        ["metadata"] = new JsonObject
        {
            ["workload"] = spec.Workload,
            ["definition_sha256"] = spec.DefinitionSha256,
            ["configuration_id"] = spec.ConfigurationId,
        },
    };

    private static bool Matches(JsonObject agent, ManagedAgentSpecification spec) =>
        MatchesName(agent, spec.Name) && agent["definition"] is JsonObject definition &&
        Hash(definition) == spec.DefinitionSha256 && agent["object"]?.GetValue<string>() == "agent.version" &&
        VersionPattern.IsMatch(agent["version"]?.GetValue<string>() ?? "");

    private static bool MatchesName(JsonObject agent, string name) =>
        agent["name"]?.GetValue<string>() == name;

    private static string RequiredVersion(JsonObject agent) =>
        VersionPattern.IsMatch(agent["version"]?.GetValue<string>() ?? "")
            ? agent["version"]!.GetValue<string>()
            : throw new ManagedAgentSetupException("agent_service_contract_invalid");

    private static string PreviousVersion(JsonObject manifest, string workload)
    {
        if (manifest["agents"] is JsonArray agents && agents.Count == 2)
        {
            var entry = agents.OfType<JsonObject>().SingleOrDefault(agent => agent["workload"]?.GetValue<string>() == workload);
            if (entry?["version"] is JsonValue value && value.TryGetValue<string>(out var version) &&
                VersionPattern.IsMatch(version)) return version;
        }
        throw new ManagedAgentSetupException("agent_setup_state_invalid");
    }

    private static JsonObject? ReadExistingManifest(
        string path, ManagedAgentSetupConfiguration config, IReadOnlyList<ManagedAgentSpecification> specs)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject
                ?? throw new JsonException();
            if (root["projectName"]?.GetValue<string>() != config.ProjectName ||
                root["projectEndpoint"]?.GetValue<string>() != config.ProjectEndpoint.AbsoluteUri ||
                root["agents"] is not JsonArray agents || agents.Count != specs.Count)
                throw new ManagedAgentSetupException("agent_definition_mismatch");
            foreach (var spec in specs)
            {
                var entry = agents.OfType<JsonObject>().SingleOrDefault(item => item["workload"]?.GetValue<string>() == spec.Workload);
                if (entry?["name"]?.GetValue<string>() != spec.Name ||
                    entry["definitionSha256"]?.GetValue<string>() != spec.DefinitionSha256 ||
                    entry["configurationId"]?.GetValue<string>() != spec.ConfigurationId)
                    throw new ManagedAgentSetupException("agent_definition_mismatch");
            }
            return root;
        }
        catch (ManagedAgentSetupException) { throw; }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException or InvalidDataException)
        {
            throw new ManagedAgentSetupException("agent_setup_state_invalid");
        }
    }

    private static void WriteManifest(string path, JsonObject manifest)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ManagedAgentSetupException("agent_setup_state_invalid");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static ManagedAgentSpecification Specification(
        ManagedAgentSetupConfiguration config, string workload, string name, string description,
        string instructions, JsonArray tools, JsonObject text, string dotnetVersion, string identityVersion) =>
        new(workload, name, description, new JsonObject
        {
            ["kind"] = "prompt",
            ["model"] = config.ModelDeployment,
            ["instructions"] = instructions,
            ["tools"] = tools,
            ["text"] = text,
        }, config.ModelDeployment, config.ModelName, config.ModelVersion, config.FilterPolicy,
            ApiVersion, dotnetVersion, identityVersion);

    private static JsonObject CopilotTextFormat() => new()
    {
        ["format"] = new JsonObject
        {
            ["type"] = "json_schema",
            ["name"] = "copilot_answer",
            ["strict"] = true,
            ["schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["answer"] = new JsonObject { ["type"] = "string" },
                    ["citations"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["sourceId"] = new JsonObject { ["type"] = "string" },
                                ["recordId"] = new JsonObject { ["type"] = "string" },
                                ["recordVersion"] = new JsonObject { ["type"] = "integer" },
                            },
                            ["required"] = new JsonArray("sourceId", "recordId", "recordVersion"),
                            ["additionalProperties"] = false,
                        },
                    },
                },
                ["required"] = new JsonArray("answer", "citations"),
                ["additionalProperties"] = false,
            },
        },
    };

    private static JsonObject InvestigatorTextFormat() => new()
    {
        ["format"] = new JsonObject
        {
            ["type"] = "json_schema",
            ["name"] = "investigation_claims",
            ["strict"] = true,
            ["schema"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["text"] = new JsonObject { ["type"] = "string" },
                        ["witnesses"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["documentId"] = new JsonObject { ["type"] = "string" },
                                    ["page"] = new JsonObject { ["type"] = "integer" },
                                    ["quote"] = new JsonObject { ["type"] = "string" },
                                },
                                ["required"] = new JsonArray("documentId", "page", "quote"),
                                ["additionalProperties"] = false,
                            },
                        },
                        ["sourceWitnesses"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["sourceId"] = new JsonObject { ["type"] = "string" },
                                    ["recordId"] = new JsonObject { ["type"] = "string" },
                                    ["recordVersion"] = new JsonObject { ["type"] = "integer" },
                                },
                                ["required"] = new JsonArray("sourceId", "recordId", "recordVersion"),
                                ["additionalProperties"] = false,
                            },
                        },
                    },
                    ["required"] = new JsonArray("text", "witnesses", "sourceWitnesses"),
                    ["additionalProperties"] = false,
                },
            },
        },
    };

    private static void ValidateConfiguration(ManagedAgentSetupConfiguration config)
    {
        if (config.ProjectEndpoint.Scheme != "https" || config.ProjectEndpoint.UserInfo.Length != 0 ||
            config.ProjectEndpoint.Query.Length != 0 || config.ProjectEndpoint.Fragment.Length != 0 ||
            !config.ProjectEndpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(config.ProjectEndpoint.AbsolutePath, "^/api/projects/[A-Za-z0-9_-]{1,128}/?$", RegexOptions.CultureInvariant) ||
            config.ProjectEndpoint.Segments.LastOrDefault()?.TrimEnd('/') != config.ProjectName ||
            !Regex.IsMatch(config.ProjectName, "^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant) ||
            !Guid.TryParse(config.PublisherClientId, out var publisherClientId) || publisherClientId == Guid.Empty ||
            !Regex.IsMatch(config.ModelDeployment, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(config.ModelName, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(config.ModelVersion, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant) ||
            config.ModelVersion.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(config.FilterPolicy, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(config.ManifestPath) || string.IsNullOrWhiteSpace(config.StateDirectory))
            throw new ManagedAgentSetupException("agent_setup_configuration_invalid");
    }

    internal static string Hash(JsonNode node) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(node).ToJsonString())));

    private static JsonNode Canonical(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => KeyValuePair.Create<string, JsonNode?>(item.Key, item.Value is null ? null : Canonical(item.Value)))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : Canonical(item)).ToArray()),
        _ => node.DeepClone(),
    };

}

public static class ManagedAgentSetupCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("plan" or "publish"))
            throw new ArgumentException("Usage: AirlineDemo.Deployment managed-agents <plan|publish> <protected configuration JSON>");
        var config = ReadConfiguration(args[1]);
        if (args[0] == "plan")
        {
            Console.WriteLine("MANAGED_AGENT_PREVIEW:" + ManagedAgentSetup.Preview(config).ToJsonString());
            return 0;
        }

        Directory.CreateDirectory(config.StateDirectory);
        var lockPath = Path.Combine(config.StateDirectory, "publisher.lock");
        FileStream publisherLock;
        try
        {
            publisherLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new ManagedAgentSetupException("agent_publish_already_running");
        }
        await using (publisherLock)
        using (var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        })
        {
            var credential = new ManagedIdentityCredential(
                ManagedIdentityId.FromUserAssignedClientId(config.PublisherClientId));
            var setup = new ManagedAgentSetup(new ManagedAgentRemote(config.ProjectEndpoint, http, credential),
                new FileManagedAgentPendingStore(config.StateDirectory));
            var manifest = await setup.PublishAsync(config);
            Console.WriteLine("MANAGED_AGENT_MANIFEST:" + manifest.ToJsonString());
        }
        return 0;
    }

    private static ManagedAgentSetupConfiguration ReadConfiguration(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            var fields = new HashSet<string>(StringComparer.Ordinal)
            {
                "projectEndpoint", "projectName", "publisherClientId", "modelDeployment", "modelName",
                "modelVersion", "filterPolicy", "manifestPath", "stateDirectory",
            };
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Any(property => !fields.Remove(property.Name)) || fields.Count != 0)
                throw new JsonException();
            var config = new ManagedAgentSetupConfiguration(
                new Uri(RequiredString(root, "projectEndpoint"), UriKind.Absolute),
                RequiredString(root, "projectName"),
                RequiredString(root, "publisherClientId"),
                RequiredString(root, "modelDeployment"),
                RequiredString(root, "modelName"),
                RequiredString(root, "modelVersion"),
                RequiredString(root, "filterPolicy"),
                RequiredString(root, "manifestPath"),
                RequiredString(root, "stateDirectory"));
            _ = ManagedAgentSetup.Specifications(config);
            return config;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException or UriFormatException)
        {
            throw new ManagedAgentSetupException("agent_setup_configuration_invalid");
        }
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new JsonException();
}
