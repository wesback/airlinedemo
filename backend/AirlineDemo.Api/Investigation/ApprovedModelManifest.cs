using System.Text.Json;
using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Investigation;

public sealed class ModelGateException(string message) : Exception(message);

/// <summary>
/// Non-secret, versioned model configuration. Ships with STUB values and approved=false, so it is refused until a
/// reviewer replaces every value and sets approved=true. No deployment identifiers or credentials are guessed.
/// </summary>
public sealed record ApprovedModelManifest(
    string? ManifestVersion,
    bool Approved,
    string? Arm,
    string? DeploymentName,
    string? Model,
    string? ApiVersion,
    string? Region,
    string? ContentFilter)
{
    public const string StubMarker = "STUB";
    public const string LiveArm = "investigation-live";
    public const string EnabledContentFilter = "enabled";

    private static readonly string[] SupportedArms = [LiveArm];

    public static ApprovedModelManifest Parse(string json)
    {
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<ApprovedModelManifest>(json, options)
                ?? throw new ModelGateException("model manifest is empty");
        }
        catch (JsonException ex)
        {
            throw new ModelGateException($"model manifest is not valid JSON: {ex.Message}");
        }
    }

    public static ApprovedModelManifest Load(string path) => Parse(File.ReadAllText(path));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ManifestVersion))
        {
            errors.Add("manifestVersion is missing");
        }

        if (!Approved)
        {
            errors.Add("manifest is not approved");
        }

        foreach (var (name, value) in Values())
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{name} is missing");
            }
            else if (value.Contains(StubMarker, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{name} is still a stub value");
            }
        }

        if (!string.IsNullOrWhiteSpace(Arm) && !SupportedArms.Contains(Arm))
        {
            errors.Add($"arm '{Arm}' is not supported");
        }

        if (!string.IsNullOrWhiteSpace(ContentFilter) && ContentFilter != EnabledContentFilter)
        {
            errors.Add($"contentFilter must be '{EnabledContentFilter}'");
        }

        return errors;
    }

    /// <summary>Returns this manifest when usable; otherwise throws, listing every refusal reason.</summary>
    public ApprovedModelManifest RequireUsable()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new ModelGateException("model manifest refused: " + string.Join("; ", errors));
        }

        return this;
    }

    private IEnumerable<(string Name, string? Value)> Values() =>
    [
        ("arm", Arm),
        ("deploymentName", DeploymentName),
        ("model", Model),
        ("apiVersion", ApiVersion),
        ("region", Region),
        ("contentFilter", ContentFilter),
    ];
}

/// <summary>Model input. Only versioned evidence is allowed; no raw prompts are stored in traces.</summary>
public sealed record ModelRequest(string? EvidenceVersion, bool IsSynthetic, string? DataRegion, string Content);

/// <summary>Trace of an authorized call. Carries the manifest version and settings only, never prompt or document text.</summary>
public sealed record ModelCallTrace(
    string ManifestVersion,
    string Arm,
    string DeploymentName,
    string Model,
    string ApiVersion,
    string Region,
    string ContentFilter,
    string EvidenceVersion);

public static class ModelCallGate
{
    /// <summary>Prefix every synthetic evidence request must carry. The caller's IsSynthetic flag alone is not trusted.</summary>
    public const string SyntheticMarker = "SYNTHETIC EVIDENCE";

    // Positive allowlist: synthetic evidence is plain text, so symbols common in real identifiers (@, +, /, #, =, _) are refused.
    private static readonly Regex SyntheticBodyPattern =
        new(@"^SYNTHETIC EVIDENCE: [A-Za-z0-9 .,:;'()\-]*\z", RegexOptions.Compiled);

    // Heuristic screen: it cannot prove content is synthetic, so the marker, allowlist and approved region are also required.
    private static readonly Regex[] CredentialOrIdentifierPatterns =
    [
        new(@"(?i)\b(api[-_]?key|password|secret|token|authorization)\s*[:=]", RegexOptions.Compiled),
        new(@"(?i)\bbearer\s+[A-Za-z0-9._\-]+", RegexOptions.Compiled),
        new(@"(?i)AccountKey=|SharedAccessSignature=|\bsk-[A-Za-z0-9]{8,}", RegexOptions.Compiled),
        new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled),
        new(@"(?<!\d)(\+\d{7,15}|\(?\d{3}\)?[-.\s]\d{3}[-.\s]\d{4})(?!\d)", RegexOptions.Compiled),
        new(@"\bN\d{1,5}[A-Z]{0,2}\b", RegexOptions.Compiled),
        new(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled),
        new(@"\b\d{4}([ -]?\d{4}){2,3}\b", RegexOptions.Compiled),
        new(@"\b\d{6,}\b", RegexOptions.Compiled),
        new(@"(?i)\b(passport|date of birth|dob)\b", RegexOptions.Compiled),
        new(@"\b(?i:passenger|crew|customer|pilot)\b\s+[A-Z][a-z]+", RegexOptions.Compiled),
        new(@"\b(Mr|Mrs|Ms|Miss|Dr)\.?\s+[A-Z][a-z]+", RegexOptions.Compiled),
    ];

    /// <summary>Throws unless the manifest is usable and the request is allowed; returns the trace to record.</summary>
    public static ModelCallTrace Authorize(ApprovedModelManifest manifest, ModelRequest request)
    {
        manifest.RequireUsable();

        if (string.IsNullOrWhiteSpace(request.EvidenceVersion))
        {
            throw new ModelGateException("model request refused: no versioned evidence");
        }

        if (!request.IsSynthetic)
        {
            throw new ModelGateException("model request refused: only synthetic evidence may be sent to the model");
        }

        if (!request.Content.StartsWith(SyntheticMarker, StringComparison.Ordinal))
        {
            throw new ModelGateException("model request refused: content is not marked as synthetic evidence");
        }

        if (!SyntheticBodyPattern.IsMatch(request.Content))
        {
            throw new ModelGateException("model request refused: content has characters outside the synthetic evidence allowlist");
        }

        // Region applies to every request: the synthetic flag is caller-supplied and cannot waive it.
        if (!string.Equals(request.DataRegion, manifest.Region, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelGateException("model request refused: content region is not the approved region");
        }

        if (CredentialOrIdentifierPatterns.Any(p => p.IsMatch(request.Content)))
        {
            throw new ModelGateException("model request refused: content contains a credential or personal identifier");
        }

        return new ModelCallTrace(
            manifest.ManifestVersion!,
            manifest.Arm!,
            manifest.DeploymentName!,
            manifest.Model!,
            manifest.ApiVersion!,
            manifest.Region!,
            manifest.ContentFilter!,
            request.EvidenceVersion);
    }

    /// <summary>
    /// Authorizes every request of a scored run before any is sent. One refusal refuses the whole run, and no traces
    /// are returned unless all requests pass.
    /// </summary>
    public static IReadOnlyList<ModelCallTrace> AuthorizeScoredRun(
        ApprovedModelManifest manifest,
        IReadOnlyList<ModelRequest> requests)
    {
        manifest.RequireUsable();

        if (requests.Count == 0)
        {
            throw new ModelGateException("scored run refused: no model requests");
        }

        return requests.Select(request => Authorize(manifest, request)).ToList();
    }
}
