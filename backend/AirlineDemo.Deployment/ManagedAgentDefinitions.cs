using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static AirlineDemo.Deployment.ManagedAgentDefinitionBuilder;

namespace AirlineDemo.Deployment;

public interface IManagedAgentVersionEdge
{
    // Null means the service reports the version missing.
    Task<JsonObject?> ReadAsync(string agentName, string version, CancellationToken cancellationToken);

    // Pages are ascending by creation; `after` is the previous page's last_id.
    Task<JsonObject> ListPageAsync(string agentName, string? after, int limit, CancellationToken cancellationToken);

    Task<JsonObject> CreateAsync(string agentName, JsonObject request, CancellationToken cancellationToken);
}

public enum ManagedAgentAttemptState { Attempted, ReconciliationRequired, Completed }

public sealed record ManagedAgentAttempt(ManagedAgentAttemptState State, string ConfigurationId, string DefinitionDigest);

public interface IManagedAgentAttemptLedger
{
    Task<ManagedAgentAttempt?> LoadAsync(string agentName, CancellationToken cancellationToken);

    // Stores `next` only while the stored state equals `expected` (null when nothing is stored); otherwise throws.
    Task RecordAsync(string agentName, ManagedAgentAttempt? expected, ManagedAgentAttempt next, CancellationToken cancellationToken);
}

public sealed record ManagedAgentModel(string Name, string Deployment, string Version, string ContentFilter);

public sealed record ManagedAgentSdkPins(string Dotnet, string AzureIdentity);

public sealed record ManagedAgentDefinitionSpec(string Workload, string AgentName, string ProjectName, string Instructions,
    JsonArray Tools, JsonObject Text, ManagedAgentModel Model, ManagedAgentSdkPins Sdk, string? Description = null);

public sealed record ManagedAgentReleaseManifest(string Workload, string AgentName, string AgentVersion, string ConfigurationId,
    string DefinitionDigest, string Instructions, string Configuration, string ToolSchemas)
{
    public string ToJson() => new JsonObject
    {
        ["agentName"] = AgentName,
        ["agentVersion"] = AgentVersion,
        ["configuration"] = Configuration,
        ["configurationId"] = ConfigurationId,
        ["definitionDigest"] = DefinitionDigest,
        ["instructions"] = Instructions,
        ["toolSchemas"] = ToolSchemas,
        ["workload"] = Workload,
    }.ToJsonString();

    // Accepts only the exact bytes ToJson produces from the content the manifest itself carries.
    public static ManagedAgentReleaseManifest Verify(string json)
    {
        try
        {
            var fields = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("manifest");
            var configuration = JsonNode.Parse(RequiredString(fields, "configuration")) as JsonObject
                ?? throw new FormatException("configuration");
            var model = ObjectOf(configuration, "model");
            var sdk = ObjectOf(configuration, "sdkPins");
            var spec = new ManagedAgentDefinitionSpec(
                RequiredString(configuration, "workload"),
                RequiredString(configuration, "agentName"),
                RequiredString(configuration, "project"),
                RequiredString(fields, "instructions"),
                JsonNode.Parse(RequiredString(fields, "toolSchemas")) as JsonArray ?? throw new FormatException("toolSchemas"),
                ObjectOf(configuration, "text"),
                new ManagedAgentModel(RequiredString(model, "name"), RequiredString(model, "deployment"),
                    RequiredString(model, "version"), RequiredString(configuration, "contentFilter")),
                new ManagedAgentSdkPins(RequiredString(sdk, "dotnet"), RequiredString(sdk, "azureIdentity")));
            var build = Build(spec);
            var manifest = new ManagedAgentReleaseManifest(spec.Workload, spec.AgentName, RequiredString(fields, "agentVersion"),
                build.ConfigurationId, build.DefinitionDigest, spec.Instructions, build.Configuration, build.ToolSchemas);
            if (!IsVersion(manifest.AgentVersion) || manifest.ToJson() != json) throw new FormatException("bytes");
            return manifest;
        }
        catch (Exception exception)
        {
            throw new ManagedAgentReconciliationException("agent_manifest_invalid", exception);
        }
    }
}

public sealed class ManagedAgentReconciliationException(string code, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
}

public sealed class ManagedAgentDefinitionReconciler(IManagedAgentVersionEdge edge, IManagedAgentAttemptLedger ledger)
{
    private const int ListLimit = 100;
    private const int MaxListPages = 10;

    public async Task<ManagedAgentReleaseManifest> ReconcileAsync(ManagedAgentDefinitionSpec spec, string? expectedVersion,
        CancellationToken cancellationToken)
    {
        var build = Build(spec);
        return expectedVersion is null
            ? await DiscoverAsync(spec, build, cancellationToken)
            : await VerifyPinnedAsync(spec, build, expectedVersion, cancellationToken);
    }

    private async Task<ManagedAgentReleaseManifest> VerifyPinnedAsync(ManagedAgentDefinitionSpec spec, ManagedAgentBuild build,
        string version, CancellationToken cancellationToken)
    {
        if (!IsVersion(version)) throw Failure("agent_definition_invalid");
        var record = await ReadVersionAsync(spec.AgentName, version, cancellationToken) ?? throw Failure("agent_version_missing");
        if (!IsWellFormed(record, spec.AgentName, version)) throw Failure("agent_version_malformed");
        if (!IsExact(record, build)) throw Failure("agent_definition_mismatch");
        return Manifest(spec, build, version);
    }

    private async Task<ManagedAgentReleaseManifest> DiscoverAsync(ManagedAgentDefinitionSpec spec, ManagedAgentBuild build,
        CancellationToken cancellationToken)
    {
        ManagedAgentAttempt? loaded;
        try { loaded = await ledger.LoadAsync(spec.AgentName, cancellationToken); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("agent_attempt_persistence_failed", exception);
        }

        var exact = new List<string>();
        foreach (var listed in await ListVersionsAsync(spec.AgentName, cancellationToken))
        {
            var record = await ReadVersionAsync(spec.AgentName, listed, cancellationToken) ?? throw Failure("agent_enumeration_incomplete");
            if (!IsWellFormed(record, spec.AgentName, listed)) throw Failure("agent_version_malformed");
            if (StringOf(Metadata(record), "configuration_id") != build.ConfigurationId) continue;
            if (!IsExact(record, build)) throw Failure("agent_definition_mismatch");
            exact.Add(listed);
        }

        if (exact.Count > 1) throw Failure("agent_identity_ambiguous");
        if (exact.Count == 1)
        {
            if (loaded is { State: not ManagedAgentAttemptState.Completed } pending && pending.ConfigurationId == build.ConfigurationId)
                await RecordAsync(spec.AgentName, pending, Attempt(build, ManagedAgentAttemptState.Completed), cancellationToken);
            return Manifest(spec, build, exact[0]);
        }
        if (loaded is { State: not ManagedAgentAttemptState.Completed }) throw Failure("agent_reconciliation_required");

        await RecordAsync(spec.AgentName, loaded, Attempt(build, ManagedAgentAttemptState.Attempted), cancellationToken);
        return await CreateAsync(spec, build, cancellationToken);
    }

    private async Task<List<string>> ListVersionsAsync(string agentName, CancellationToken cancellationToken)
    {
        var versions = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        for (var page = 0; page < MaxListPages; page++)
        {
            JsonObject response;
            try { response = await edge.ListPageAsync(agentName, after, ListLimit, cancellationToken); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw Failure("agent_enumeration_incomplete", exception);
            }
            if (response["data"] is not JsonArray data || data.Count > ListLimit ||
                response["has_more"] is not JsonValue moreValue || moreValue.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                throw Failure("agent_enumeration_incomplete");
            var hasMore = moreValue.GetValueKind() == JsonValueKind.True;
            string? last = null;
            foreach (var node in data)
            {
                if (node is not JsonObject item || StringOf(item, "id") is not string id || id.Length == 0 || !ids.Add(id) ||
                    StringOf(item, "version") is not string version || !IsVersion(version))
                    throw Failure("agent_enumeration_incomplete");
                versions.Add(version);
                last = id;
            }
            if (!hasMore) return versions;
            if (last is null || StringOf(response, "last_id") != last) throw Failure("agent_enumeration_incomplete");
            after = last;
        }
        throw Failure("agent_enumeration_incomplete");
    }

    private async Task<JsonObject?> ReadVersionAsync(string agentName, string version, CancellationToken cancellationToken)
    {
        try { return await edge.ReadAsync(agentName, version, cancellationToken); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("agent_enumeration_incomplete", exception);
        }
    }

    private async Task<ManagedAgentReleaseManifest> CreateAsync(ManagedAgentDefinitionSpec spec, ManagedAgentBuild build,
        CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["definition"] = build.Definition.DeepClone(),
            ["metadata"] = new JsonObject
            {
                ["workload"] = spec.Workload,
                ["definition_sha256"] = build.DefinitionDigest,
                ["configuration_id"] = build.ConfigurationId,
            },
        };
        if (spec.Description is not null) request["description"] = spec.Description;
        JsonObject? reply;
        try { reply = await edge.CreateAsync(spec.AgentName, request, cancellationToken); }
        catch (Exception exception)
        {
            await MarkReconciliationRequiredAsync(spec.AgentName, build);
            throw Failure("agent_reconciliation_required", exception);
        }
        if (reply is null || !IsWellFormed(reply, spec.AgentName, null) || !IsExact(reply, build))
        {
            await MarkReconciliationRequiredAsync(spec.AgentName, build);
            throw Failure("agent_reconciliation_required");
        }
        await RecordAsync(spec.AgentName, Attempt(build, ManagedAgentAttemptState.Attempted),
            Attempt(build, ManagedAgentAttemptState.Completed), cancellationToken);
        return Manifest(spec, build, StringOf(reply, "version")!);
    }

    private async Task MarkReconciliationRequiredAsync(string agentName, ManagedAgentBuild build)
    {
        // If this write fails, the stored Attempted state still blocks the next run, so no second create happens.
        try
        {
            await ledger.RecordAsync(agentName, Attempt(build, ManagedAgentAttemptState.Attempted),
                Attempt(build, ManagedAgentAttemptState.ReconciliationRequired), CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    private async Task RecordAsync(string agentName, ManagedAgentAttempt? expected, ManagedAgentAttempt next,
        CancellationToken cancellationToken)
    {
        try { await ledger.RecordAsync(agentName, expected, next, cancellationToken); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("agent_attempt_persistence_failed", exception);
        }
    }

    private static ManagedAgentAttempt Attempt(ManagedAgentBuild build, ManagedAgentAttemptState state) =>
        new(state, build.ConfigurationId, build.DefinitionDigest);

    private static ManagedAgentReleaseManifest Manifest(ManagedAgentDefinitionSpec spec, ManagedAgentBuild build, string version) =>
        new(spec.Workload, spec.AgentName, version, build.ConfigurationId, build.DefinitionDigest, spec.Instructions,
            build.Configuration, build.ToolSchemas);

    private static ManagedAgentReconciliationException Failure(string code, Exception? inner = null) => new(code, inner);

    private static JsonObject Metadata(JsonObject record) => (JsonObject)record["metadata"]!;

    private static bool IsWellFormed(JsonObject record, string agentName, string? version)
    {
        var recordVersion = StringOf(record, "version");
        return StringOf(record, "object") == "agent.version" && StringOf(record, "name") == agentName &&
            recordVersion is not null && IsVersion(recordVersion) && (version is null || recordVersion == version) &&
            StringOf(record, "id") is { Length: > 0 } && record["created_at"] is JsonValue created &&
            created.GetValueKind() == JsonValueKind.Number && created.TryGetValue<long>(out _) &&
            record["definition"] is JsonObject && record["metadata"] is JsonObject metadata &&
            StringOf(metadata, "configuration_id") is not null && StringOf(metadata, "definition_sha256") is not null;
    }

    private static bool IsExact(JsonObject record, ManagedAgentBuild build) =>
        record["definition"] is JsonObject definition && Hash(definition) == build.DefinitionDigest &&
        StringOf(Metadata(record), "configuration_id") == build.ConfigurationId &&
        StringOf(Metadata(record), "definition_sha256") == build.DefinitionDigest;
}

internal sealed record ManagedAgentBuild(JsonObject Definition, string DefinitionDigest, string ConfigurationId,
    string Configuration, string ToolSchemas);

internal static class ManagedAgentDefinitionBuilder
{
    private const string ApiVersion = "v1";
    private static readonly Regex AgentNamePattern = new("^[A-Za-z][A-Za-z0-9_-]{0,62}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex ProjectPattern = new("^[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex ComponentPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex ToolNamePattern = new("^[A-Za-z0-9_-]{1,64}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new("^[1-9][0-9]{0,9}\\z", RegexOptions.CultureInvariant);

    internal static bool IsVersion(string value) => VersionPattern.IsMatch(value);

    internal static string? StringOf(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    internal static string RequiredString(JsonObject obj, string name) => StringOf(obj, name) ?? throw new FormatException(name);

    internal static JsonObject ObjectOf(JsonObject obj, string name) => obj[name] as JsonObject ?? throw new FormatException(name);

    internal static string Hash(JsonNode node) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(node).ToJsonString())));

    internal static ManagedAgentBuild Build(ManagedAgentDefinitionSpec spec)
    {
        if (spec.Workload is not ("copilot" or "investigator") || !AgentNamePattern.IsMatch(spec.AgentName) ||
            !ProjectPattern.IsMatch(spec.ProjectName) || string.IsNullOrWhiteSpace(spec.Instructions) || spec.Text.Count == 0 ||
            !ComponentPattern.IsMatch(spec.Model.Name) || !ComponentPattern.IsMatch(spec.Model.Deployment) ||
            !ComponentPattern.IsMatch(spec.Model.Version) || spec.Model.Version.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
            !ComponentPattern.IsMatch(spec.Model.ContentFilter) || !ComponentPattern.IsMatch(spec.Sdk.Dotnet) ||
            !ComponentPattern.IsMatch(spec.Sdk.AzureIdentity))
            throw new ManagedAgentReconciliationException("agent_definition_invalid");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tools = new JsonArray();
        foreach (var node in spec.Tools)
        {
            if (node is not JsonObject tool || tool.Count != 4 || StringOf(tool, "type") != "function" ||
                StringOf(tool, "name") is not string name || !ToolNamePattern.IsMatch(name) || !names.Add(name) ||
                StringOf(tool, "description") is null || tool["parameters"] is not JsonObject)
                throw new ManagedAgentReconciliationException("agent_definition_unsupported");
            tools.Add(tool.DeepClone());
        }

        var text = (JsonObject)spec.Text.DeepClone();
        var definition = new JsonObject
        {
            ["kind"] = "prompt",
            ["model"] = spec.Model.Deployment,
            ["instructions"] = spec.Instructions,
            ["tools"] = tools,
            ["text"] = text,
        };
        var canonicalTools = Canonical(tools);
        var configuration = Canonical(new JsonObject
        {
            ["agentName"] = spec.AgentName,
            ["workload"] = spec.Workload,
            ["project"] = spec.ProjectName,
            ["apiVersion"] = ApiVersion,
            ["contentFilter"] = spec.Model.ContentFilter,
            ["model"] = new JsonObject { ["name"] = spec.Model.Name, ["deployment"] = spec.Model.Deployment, ["version"] = spec.Model.Version },
            ["sdkPins"] = new JsonObject { ["dotnet"] = spec.Sdk.Dotnet, ["azureIdentity"] = spec.Sdk.AzureIdentity },
            ["text"] = text.DeepClone(),
        });
        var configurationId = Hash(new JsonObject
        {
            ["configuration"] = configuration,
            ["instructions"] = spec.Instructions,
            ["toolSchemas"] = canonicalTools,
        });
        return new ManagedAgentBuild(definition, Hash(definition), configurationId, configuration.ToJsonString(),
            canonicalTools.ToJsonString());
    }

    private static JsonNode Canonical(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => KeyValuePair.Create<string, JsonNode?>(item.Key, item.Value is null ? null : Canonical(item.Value)))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : Canonical(item)).ToArray()),
        _ => node.DeepClone(),
    };
}
