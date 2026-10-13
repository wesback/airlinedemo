using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirlineDemo.Api.Investigation;

/// <summary>Raised when a model call or scored run is refused by the manifest gate. Nothing is sent to the model.</summary>
public sealed class ModelGateException : InvalidOperationException
{
    public ModelGateException(string message) : base(message) { }
}

public sealed record ModelApproval(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("approvedBy")] string ApprovedBy,
    [property: JsonPropertyName("approvedAt")] string ApprovedAt);

/// <summary>Non-secret, versioned model configuration. Holds no endpoint keys or credentials.</summary>
public sealed record ApprovedModelManifest(
    [property: JsonPropertyName("manifestVersion")] string ManifestVersion,
    [property: JsonPropertyName("arm")] string Arm,
    [property: JsonPropertyName("deploymentName")] string DeploymentName,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("apiVersion")] string ApiVersion,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("contentFilter")] string ContentFilter,
    [property: JsonPropertyName("approval")] ModelApproval? Approval,
    [property: JsonPropertyName("nonSyntheticContentApproved")] bool NonSyntheticContentApproved,
    [property: JsonPropertyName("realWorldActionsEnabled")] bool RealWorldActionsEnabled);

public static class ModelManifestGate
{
    public const string ManifestRelativePath = "config/model-manifest.json";

    // Arms the investigation can run. An arm outside this list is an unsupported manifest.
    private static readonly string[] SupportedArms = ["agent-framework", "direct-tool", "scripted", "baseline"];

    public static ApprovedModelManifest Load(string path) => Parse(File.ReadAllText(path));

    public static ApprovedModelManifest Parse(string json)
    {
        ApprovedModelManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ApprovedModelManifest>(json);
        }
        catch (JsonException ex)
        {
            throw new ModelGateException($"Model manifest is not valid JSON: {ex.Message}");
        }

        return manifest ?? throw new ModelGateException("Model manifest is empty.");
    }

    /// <summary>Lists every reason the manifest cannot be used. Empty means usable.</summary>
    public static IReadOnlyList<string> Problems(ApprovedModelManifest manifest)
    {
        var problems = new List<string>();
        RequireValue(problems, "manifestVersion", manifest.ManifestVersion);
        RequireValue(problems, "arm", manifest.Arm);
        RequireValue(problems, "deploymentName", manifest.DeploymentName);
        RequireValue(problems, "model", manifest.Model);
        RequireValue(problems, "apiVersion", manifest.ApiVersion);
        RequireValue(problems, "region", manifest.Region);
        RequireValue(problems, "contentFilter", manifest.ContentFilter);

        if (!string.IsNullOrWhiteSpace(manifest.Arm) && !IsPlaceholder(manifest.Arm) && !SupportedArms.Contains(manifest.Arm))
        {
            problems.Add("arm is not a supported investigation arm");
        }

        if (manifest.Approval is null || manifest.Approval.Status != "approved")
        {
            problems.Add("approval.status must be 'approved'");
        }
        else
        {
            RequireValue(problems, "approval.approvedBy", manifest.Approval.ApprovedBy);
            RequireValue(problems, "approval.approvedAt", manifest.Approval.ApprovedAt);
        }

        if (manifest.RealWorldActionsEnabled)
        {
            problems.Add("realWorldActionsEnabled must be false");
        }

        return problems;
    }

    /// <summary>Gate for every model call and scored evaluation run. Throws before any data is sent.</summary>
    public static void RequireUsable(ApprovedModelManifest manifest)
    {
        var problems = Problems(manifest);
        if (problems.Count > 0)
        {
            throw new ModelGateException("Model call refused: " + string.Join("; ", problems));
        }
    }

    /// <summary>Trace attributes binding a run to the exact approved configuration. Omits prompts, documents, and credentials.</summary>
    public static IReadOnlyDictionary<string, string> TraceAttributes(ApprovedModelManifest manifest)
    {
        RequireUsable(manifest);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model.manifest.version"] = manifest.ManifestVersion,
            ["model.arm"] = manifest.Arm,
            ["model.deployment"] = manifest.DeploymentName,
            ["model.name"] = manifest.Model,
            ["model.api_version"] = manifest.ApiVersion,
            ["model.region"] = manifest.Region,
            ["model.content_filter"] = manifest.ContentFilter,
        };
    }

    /// <summary>Synthetic evidence may go to the model. Non-synthetic content needs explicit approval and the approved region.</summary>
    public static void RequireRequestAllowed(ApprovedModelManifest manifest, bool evidenceIsSynthetic, string? requestRegion)
    {
        RequireUsable(manifest);
        if (evidenceIsSynthetic)
        {
            return;
        }

        if (!manifest.NonSyntheticContentApproved)
        {
            throw new ModelGateException("Non-synthetic content refused: the manifest does not approve it.");
        }

        if (!string.Equals(requestRegion, manifest.Region, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelGateException("Non-synthetic content refused: request is outside the approved region.");
        }
    }

    private static void RequireValue(List<string> problems, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{name} is missing");
        }
        else if (IsPlaceholder(value))
        {
            problems.Add($"{name} is an unresolved stub value");
        }
    }

    private static bool IsPlaceholder(string value) =>
        value.StartsWith("STUB", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("UNRESOLVED", StringComparison.OrdinalIgnoreCase)
        || value.Contains('<')
        || value.Contains('>');
}
