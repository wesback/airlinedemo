using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using AirlineDemo.Api.Diagnostics;
using Microsoft.Extensions.Logging;

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
    string? ModelVersion,
    string? ApiVersion,
    string? Region,
    string? ContentFilter)
{
    public const string StubMarker = "STUB";
    public const string LiveArm = "investigation-live";
    public const string EnabledContentFilter = "enabled";

    private static readonly string[] SupportedArms = [LiveArm];

    private static readonly Regex PlainIdentifierPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,63}\z", RegexOptions.Compiled);

    // Per-field shapes. Values are copied into every trace, so each field accepts only its own narrow format.
    private static readonly Regex ManifestVersionPattern =
        new(@"^\d{1,4}\.\d{1,4}\.\d{1,4}\z", RegexOptions.Compiled);

    private static readonly Regex DeploymentNamePattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9\-]{0,63}(\.[0-9][A-Za-z0-9\-]{0,15})?\z", RegexOptions.Compiled);

    private static readonly Regex ApiVersionPattern =
        new(@"^v1\z", RegexOptions.Compiled);

    private static readonly Regex ModelVersionPattern =
        new(@"^\d{4}-\d{2}-\d{2}\z", RegexOptions.Compiled);

    // Region is fixed to the PRD's Sweden Central model boundary (DD-016, DD-025, DD-034); any other value is refused.
    private static readonly Regex RegionPattern =
        new(@"^swedencentral\z", RegexOptions.Compiled);

    // Credential shapes and markers, including SAS query parameters, AWS/GitHub/Slack/JWT prefixes.
    private static readonly Regex CredentialHintPattern =
        new(@"(?i)(secret|token|passw|pwd|bearer|key|cred|auth|sig=|sv=|sas=|sk-|akia|ghp_|xox|eyj)", RegexOptions.Compiled);

    // A 20+ character letter-and-digit run looks like a generated key, even without a credential marker.
    private static readonly Regex RandomLookingRunPattern =
        new(@"(?=[A-Za-z0-9]*\d)(?=[A-Za-z0-9]*[A-Za-z])[A-Za-z0-9]{20,}", RegexOptions.Compiled);

    private static bool HasValidFormat(Regex format, string value) =>
        format.IsMatch(value) && !CredentialHintPattern.IsMatch(value) && !RandomLookingRunPattern.IsMatch(value);

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
        else if (ManifestVersion.Contains(StubMarker, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("manifestVersion is still a stub value");
        }
        else if (!HasValidFormat(ManifestVersionPattern, ManifestVersion))
        {
            errors.Add("manifestVersion is not a plain identifier");
        }

        if (!Approved)
        {
            errors.Add("manifest is not approved");
        }

        foreach (var (name, value, format) in Values())
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{name} is missing");
            }
            else if (value.Contains(StubMarker, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{name} is still a stub value");
            }
            else if (!HasValidFormat(format, value) ||
                     (name == "modelVersion" &&
                      !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            {
                // Values are copied into every trace, so each must match its field's format (no secrets, names, URLs, or spaces).
                errors.Add($"{name} is not a plain identifier");
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

    public void RequireConfigurationMatches(string? deploymentName, string? model, string? modelVersion)
    {
        if (!Approved)
        {
            return;
        }

        RequireUsable();
        if (!string.Equals(DeploymentName, deploymentName, StringComparison.Ordinal) ||
            !string.Equals(Model, model, StringComparison.Ordinal) ||
            !string.Equals(ModelVersion, modelVersion, StringComparison.Ordinal))
        {
            throw new ModelGateException("model manifest does not match the configured model deployment");
        }
    }

    private IEnumerable<(string Name, string? Value, Regex Format)> Values() =>
    [
        ("arm", Arm, PlainIdentifierPattern),
        ("deploymentName", DeploymentName, DeploymentNamePattern),
        ("model", Model, DeploymentNamePattern),
        ("modelVersion", ModelVersion, ModelVersionPattern),
        ("apiVersion", ApiVersion, ApiVersionPattern),
        ("region", Region, RegionPattern),
        ("contentFilter", ContentFilter, PlainIdentifierPattern),
    ];
}

/// <summary>
/// Model input. Only versioned evidence is allowed; no raw prompts are stored in traces. Each evidence item is checked
/// and traced by its own SHA-256; one unregistered item refuses the whole request.
/// </summary>
public sealed record ModelRequest(string? EvidenceVersion, string? DataRegion, IReadOnlyList<string> Evidence)
{
    public ModelRequest(string? EvidenceVersion, string? DataRegion, string Content)
        : this(EvidenceVersion, DataRegion, [Content])
    {
    }
}

/// <summary>
/// Registry of SHA-256 hashes of synthetic evidence items. The only way to fill it is <see cref="FromCommittedFixtures"/>,
/// which reads files embedded at build time from Investigation/SyntheticFixtures. No caller can supply a path or content,
/// so registered content always comes from committed synthetic fixtures. Only content whose exact hash is registered
/// may be sent to a model.
/// </summary>
public sealed class SyntheticContentRegistry
{
    private const string FixturePrefix = "AirlineDemo.Api.Investigation.SyntheticFixtures.";

    private readonly HashSet<string> _hashes = new(StringComparer.Ordinal);

    /// <summary>Builds the registry from the committed synthetic fixtures compiled into this assembly.</summary>
    public static SyntheticContentRegistry FromCommittedFixtures()
    {
        var assembly = typeof(SyntheticContentRegistry).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(FixturePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0)
        {
            throw new ModelGateException("committed synthetic fixtures not found");
        }

        var registry = new SyntheticContentRegistry();
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new ModelGateException("committed synthetic fixture unreadable");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // Trailing newlines from the file are not part of the evidence item sent to the model.
            var content = reader.ReadToEnd().TrimEnd('\r', '\n');
            if (!ModelCallGate.IsMarkedSynthetic(content))
            {
                throw new ModelGateException("committed synthetic fixture is not marked as synthetic evidence");
            }

            registry.Register(content);
        }

        return registry;
    }

    private string Register(string content)
    {
        var hash = Sha256Hex(content);
        _hashes.Add(hash);
        return hash;
    }

    public bool IsRegistered(string content) => _hashes.Contains(Sha256Hex(content));

    public static string Sha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>Trace of an authorized call. Carries the manifest version and settings only, never prompt or document text.</summary>
public sealed record ModelCallTrace(
    string ManifestVersion,
    string Arm,
    string DeploymentName,
    string Model,
    string ModelVersion,
    string ApiVersion,
    string Region,
    string ContentFilter,
    IReadOnlyList<string> EvidenceSha256);

public static class ModelCallGate
{
    /// <summary>Prefix every synthetic evidence request must carry.</summary>
    public const string SyntheticMarker = "SYNTHETIC EVIDENCE";

    // Positive character allowlist on the request shape. It is not a personal-data scan and does not decide authorization.
    private static readonly Regex SyntheticBodyPattern =
        new(@"^SYNTHETIC EVIDENCE: [A-Za-z0-9 .,:;'()\-]*\z", RegexOptions.Compiled);

    // The evidence version is a short plain label. It is not recorded in the trace; the content SHA-256 is.
    private static readonly Regex EvidenceVersionPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,31}\z", RegexOptions.Compiled);

    /// <summary>True when content carries the synthetic marker. Used to vet fixtures; not an authorization decision.</summary>
    internal static bool IsMarkedSynthetic(string content) => content.StartsWith(SyntheticMarker, StringComparison.Ordinal);

    /// <summary>Throws unless the manifest is usable and the request is allowed; returns the trace to record.</summary>
    public static ModelCallTrace Authorize(
        ApprovedModelManifest manifest,
        ModelRequest request,
        SyntheticContentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        manifest.RequireUsable();

        if (string.IsNullOrWhiteSpace(request.EvidenceVersion))
        {
            throw new ModelGateException("model request refused: no versioned evidence");
        }

        if (!EvidenceVersionPattern.IsMatch(request.EvidenceVersion))
        {
            throw new ModelGateException("model request refused: evidence version is not a plain version identifier");
        }

        // Region applies to every request and must match the approved region exactly.
        if (!string.Equals(request.DataRegion, manifest.Region, StringComparison.Ordinal))
        {
            throw new ModelGateException("model request refused: content region is not the approved region");
        }

        if (request.Evidence is null || request.Evidence.Count == 0)
        {
            throw new ModelGateException("model request refused: no evidence items");
        }

        var hashes = new List<string>(request.Evidence.Count);
        foreach (var item in request.Evidence)
        {
            if (item is null)
            {
                throw new ModelGateException("model request refused: evidence item is missing");
            }

            if (!item.StartsWith(SyntheticMarker, StringComparison.Ordinal))
            {
                throw new ModelGateException("model request refused: content is not marked as synthetic evidence");
            }

            if (!SyntheticBodyPattern.IsMatch(item))
            {
                throw new ModelGateException("model request refused: content has characters outside the synthetic evidence allowlist");
            }

            // The registry is the only synthetic-data guard: each unregistered item refuses the whole request.
            if (!registry.IsRegistered(item))
            {
                throw new ModelGateException("model request refused: evidence SHA-256 is not registered as synthetic content");
            }

            hashes.Add(SyntheticContentRegistry.Sha256Hex(item));
        }

        return new ModelCallTrace(
            manifest.ManifestVersion!,
            manifest.Arm!,
            manifest.DeploymentName!,
            manifest.Model!,
            manifest.ModelVersion!,
            manifest.ApiVersion!,
            manifest.Region!,
            manifest.ContentFilter!,
            hashes.AsReadOnly());
    }

    /// <summary>
    /// Authorizes every request of a scored run before any is sent. One refusal refuses the whole run, and no traces
    /// are returned unless all requests pass.
    /// </summary>
    public static IReadOnlyList<ModelCallTrace> AuthorizeScoredRun(
        ApprovedModelManifest manifest,
        IReadOnlyList<ModelRequest> requests,
        SyntheticContentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        manifest.RequireUsable();

        if (requests.Count == 0)
        {
            throw new ModelGateException("scored run refused: no model requests");
        }

        return requests.Select(request => Authorize(manifest, request, registry)).ToList();
    }

    /// <summary>A scored run over the call budget emits the budget security event and is refused before any model call.</summary>
    public static IReadOnlyList<ModelCallTrace> AuthorizeScoredRunWithinBudget(
        ApprovedModelManifest manifest,
        IReadOnlyList<ModelRequest> requests,
        SyntheticContentRegistry registry,
        int maxModelCalls,
        ILogger logger,
        string serviceVersion,
        string environment)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (requests.Count > maxModelCalls)
        {
            TelemetryRedaction.EmitSecurityEvent(logger, TelemetryRedaction.SecurityEvents.BudgetExhausted, serviceVersion, environment);
            throw new ModelGateException("scored run refused: model call budget exhausted");
        }

        return AuthorizeScoredRun(manifest, requests, registry);
    }
}
