using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirlineDemo.Generator;

public static class WorkflowContract
{
    public static string Version => SharedWorkflowArtifacts.WorkflowSchemaVersion;

    public static IReadOnlySet<string> EventTypes => SharedWorkflowArtifacts.EventTypes;

    public static readonly IReadOnlySet<string> Profiles =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "baseline",
            "live",
            "processing-failure",
            "duplicate-events",
            "contradiction",
            "document-instructions",
            "isolation"
        };
}

public sealed record GeneratorConfiguration(
    long Seed,
    string FixtureVersion,
    DateOnly ScenarioDate,
    string RunId,
    string Profile,
    string OutputDirectory,
    IReadOnlyDictionary<string, string> BuildDependencies);

public sealed record CaseContext(
    string RunId,
    string CaseId,
    string AirlineId,
    string AircraftId,
    string LeaseId);

public sealed record AssetReference(string Identity, int Version);

public sealed record AssetComponent(string ComponentId, string SerialNumber);

public sealed record Asset(
    string AircraftId,
    string EngineId,
    IReadOnlyList<AssetComponent> Components,
    AssetReference ReferenceSource);

public sealed record Requirement(
    string RequirementId,
    int Version,
    string ComponentId,
    string EvidenceKind,
    string Description,
    string Applicability,
    string ApprovedBy,
    DateTimeOffset ApprovedAt,
    string SourceRef,
    DateTimeOffset? RequiredFrom = null,
    DateTimeOffset? RequiredTo = null);

public sealed record Document(
    string DocumentId,
    int Version,
    string SourceSystem,
    string SourceRecordId,
    string FileName,
    string MediaType,
    string Sha256,
    DateTimeOffset IssuedOn);

public sealed record SubmissionPackage(
    string SchemaVersion,
    string PackageId,
    string RunId,
    string CaseId,
    string AirlineId,
    string AircraftId,
    string LeaseId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset ScenarioEffectiveAt,
    IReadOnlyList<Document> Manifest);

public sealed record EventEnvelope(
    string SchemaVersion,
    string EventId,
    string Type,
    string RunId,
    string CaseId,
    string AirlineId,
    string AircraftId,
    string LeaseId,
    DateTimeOffset OccurredAt,
    DateTimeOffset ScenarioEffectiveAt,
    string CorrelationId,
    JsonElement Payload);

public sealed record ManifestEntry(
    string RelativePath,
    string DocumentId,
    int Version,
    string Sha256);

public sealed record SelectedInitialInputManifest(IReadOnlyList<ManifestEntry> Entries);

public sealed record PathBoundaryDeclaration(
    string ApplicationInputsRoot,
    string StagedResponsesRoot,
    string EvaluatorOnlyRoot,
    string ReplayOnlyRoot,
    SelectedInitialInputManifest SelectedInitialInputManifest)
{
    public static PathBoundaryDeclaration Default(
        SelectedInitialInputManifest selectedInitialInputManifest) =>
        new(
            "application-inputs",
            "staged-responses",
            "evaluator-only",
            "replay-only",
            selectedInitialInputManifest);
}

public sealed record GeneratedFileHash(string RelativePath, string Sha256);

public sealed record ReproducibilityReceipt(
    string ContractVersion,
    string TemplateVersion,
    long Seed,
    string FixtureVersion,
    DateOnly ScenarioDate,
    string RunId,
    string Profile,
    IReadOnlyDictionary<string, string> BuildDependencies,
    IReadOnlyList<GeneratedFileHash> GeneratedFiles,
    IReadOnlyList<string> IntendedMutationIdentifiers);

public sealed record GeneratorContractPackage(
    string ContractVersion,
    GeneratorConfiguration Configuration,
    CaseContext Case,
    Asset Asset,
    IReadOnlyList<Requirement> Requirements,
    IReadOnlyList<Document> Documents,
    IReadOnlyList<SubmissionPackage> SubmissionPackages,
    IReadOnlyList<EventEnvelope> Events,
    PathBoundaryDeclaration PathBoundaries,
    ReproducibilityReceipt Receipt);

public sealed record GeneratorValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new GeneratorContractValidationException(Errors);
        }
    }
}

public sealed class GeneratorContractValidationException : ArgumentException
{
    public GeneratorContractValidationException(IReadOnlyList<string> errors)
        : base("The generator contract is invalid: " + string.Join("; ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

public static class GeneratorContractValidator
{
    public static GeneratorValidationResult Validate(GeneratorContractPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var errors = new List<string>();
        ValidateVersion(package.ContractVersion, "contractVersion", errors);
        ValidateConfiguration(package.Configuration, errors);
        ValidateContext(package.Case, errors);
        if (package.Configuration is not null &&
            package.Case is not null &&
            !StringComparer.Ordinal.Equals(package.Configuration.RunId, package.Case.RunId))
        {
            errors.Add("configuration.runId must match case.runId.");
        }

        ValidateAsset(package.Asset, package.Case, errors);

        foreach (var requirement in package.Requirements ?? [])
        {
            ValidateRequirement(requirement, package.Case, package.Asset, errors);
        }

        foreach (var document in package.Documents ?? [])
        {
            ValidateDocument(document, errors);
        }

        var packageById = new Dictionary<string, SubmissionPackage>(StringComparer.Ordinal);
        foreach (var submissionPackage in package.SubmissionPackages ?? [])
        {
            ValidateSubmissionPackage(submissionPackage, package.Case, errors);
            if (submissionPackage is not null &&
                !string.IsNullOrWhiteSpace(submissionPackage.PackageId))
            {
                packageById.TryAdd(submissionPackage.PackageId, submissionPackage);
            }
        }

        foreach (var envelope in package.Events ?? [])
        {
            ValidateEvent(envelope, package.Case, packageById, errors);
        }

        ValidateBoundaries(package.PathBoundaries, errors);
        ValidateReceipt(package.Receipt, package.Configuration, errors);
        return new GeneratorValidationResult(errors);
    }

    public static void ValidateOrThrow(GeneratorContractPackage package) =>
        Validate(package).ThrowIfInvalid();

    private static void ValidateConfiguration(
        GeneratorConfiguration? configuration,
        ICollection<string> errors)
    {
        if (configuration is null)
        {
            errors.Add("configuration is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration.FixtureVersion))
        {
            errors.Add("configuration.fixtureVersion is required.");
        }

        if (!WorkflowContract.Profiles.Contains(configuration.Profile))
        {
            errors.Add($"configuration.profile '{configuration.Profile}' is not a supported closed value.");
        }

        ValidateId(configuration.RunId, "configuration.runId", errors);
        if (string.IsNullOrWhiteSpace(configuration.OutputDirectory))
        {
            errors.Add("configuration.outputDirectory is required.");
        }

        if (configuration.BuildDependencies is null)
        {
            errors.Add("configuration.buildDependencies is required.");
        }
    }

    private static void ValidateContext(CaseContext? context, ICollection<string> errors)
    {
        if (context is null)
        {
            errors.Add("case is required.");
            return;
        }

        ValidateId(context.RunId, "case.runId", errors);
        ValidateId(context.CaseId, "case.caseId", errors);
        ValidateId(context.AirlineId, "case.airlineId", errors);
        ValidateId(context.AircraftId, "case.aircraftId", errors);
        ValidateId(context.LeaseId, "case.leaseId", errors);
    }

    private static void ValidateAsset(
        Asset? asset,
        CaseContext? context,
        ICollection<string> errors)
    {
        if (asset is null)
        {
            errors.Add("asset is required.");
            return;
        }

        ValidateId(asset.AircraftId, "asset.aircraftId", errors);
        if (context is not null && !StringComparer.Ordinal.Equals(asset.AircraftId, context.AircraftId))
        {
            errors.Add("asset.aircraftId must match case.aircraftId.");
        }

        ValidateId(asset.EngineId, "asset.engineId", errors);
        if (asset.Components is null || asset.Components.Count == 0)
        {
            errors.Add("asset.components must contain at least one component.");
        }
        else
        {
            foreach (var component in asset.Components)
            {
                if (component is null)
                {
                    errors.Add("asset.components cannot contain null values.");
                    continue;
                }

                ValidateId(component.ComponentId, "asset component.componentId", errors);
                if (string.IsNullOrWhiteSpace(component.SerialNumber))
                {
                    errors.Add("asset component.serialNumber is required.");
                }
            }
        }

        if (asset.ReferenceSource is null || string.IsNullOrWhiteSpace(asset.ReferenceSource.Identity))
        {
            errors.Add("asset.referenceSource.identity is required.");
        }
        else if (asset.ReferenceSource.Version < 1)
        {
            errors.Add("asset.referenceSource.version must be at least 1.");
        }
    }

    private static void ValidateRequirement(
        Requirement? requirement,
        CaseContext? context,
        Asset? asset,
        ICollection<string> errors)
    {
        if (requirement is null)
        {
            errors.Add("requirements cannot contain null values.");
            return;
        }

        ValidateId(requirement.RequirementId, "requirement.requirementId", errors);
        ValidateId(requirement.ComponentId, "requirement.componentId", errors);
        if (requirement.Version < 1)
        {
            errors.Add($"requirement '{requirement.RequirementId}' version must be at least 1.");
        }

        foreach (var (value, name) in new[]
        {
            (requirement.EvidenceKind, "evidenceKind"),
            (requirement.Description, "description"),
            (requirement.Applicability, "applicability"),
            (requirement.ApprovedBy, "approvedBy"),
            (requirement.SourceRef, "sourceRef")
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"requirement.{name} is required.");
            }
        }

        if (asset?.Components is not null &&
            !asset.Components.Any(component =>
                component is not null &&
                StringComparer.Ordinal.Equals(component.ComponentId, requirement.ComponentId)))
        {
            errors.Add($"requirement '{requirement.RequirementId}' references a component outside the asset.");
        }

        if (requirement.RequiredFrom is not null &&
            requirement.RequiredTo is not null &&
            requirement.RequiredFrom > requirement.RequiredTo)
        {
            errors.Add($"requirement '{requirement.RequirementId}' has an invalid required date interval.");
        }

        if (context is not null && requirement.ApprovedAt.Offset != TimeSpan.Zero)
        {
            errors.Add($"requirement '{requirement.RequirementId}' approvedAt must be UTC.");
        }
    }

    private static void ValidateDocument(Document? document, ICollection<string> errors)
    {
        if (document is null)
        {
            errors.Add("documents cannot contain null values.");
            return;
        }

        ValidateId(document.DocumentId, "document.documentId", errors);
        ValidateId(document.SourceRecordId, "document.sourceRecordId", errors);
        if (document.Version < 1)
        {
            errors.Add($"document '{document.DocumentId}' version must be at least 1.");
        }

        foreach (var (value, name) in new[]
        {
            (document.SourceSystem, "sourceSystem"),
            (document.FileName, "fileName"),
            (document.MediaType, "mediaType")
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"document.{name} is required.");
            }
        }

        ValidateSha256(document.Sha256, $"document '{document.DocumentId}'", errors);
        if (document.IssuedOn.Offset != TimeSpan.Zero)
        {
            errors.Add($"document '{document.DocumentId}' issuedOn must be UTC.");
        }
    }

    private static void ValidateSubmissionPackage(
        SubmissionPackage? submissionPackage,
        CaseContext? context,
        ICollection<string> errors)
    {
        if (submissionPackage is null)
        {
            errors.Add("submissionPackages cannot contain null values.");
            return;
        }

        ValidateVersion(submissionPackage.SchemaVersion, "submissionPackage.schemaVersion", errors);
        ValidateId(submissionPackage.PackageId, "submissionPackage.packageId", errors);
        ValidateScope(
            submissionPackage.RunId,
            submissionPackage.CaseId,
            submissionPackage.AirlineId,
            submissionPackage.AircraftId,
            submissionPackage.LeaseId,
            context,
            $"submissionPackage '{submissionPackage.PackageId}'",
            errors);

        if (submissionPackage.Manifest is null || submissionPackage.Manifest.Count == 0)
        {
            errors.Add($"submissionPackage '{submissionPackage.PackageId}' manifest must not be empty.");
            return;
        }

        foreach (var document in submissionPackage.Manifest)
        {
            ValidateDocument(document, errors);
        }
    }

    private static void ValidateEvent(
        EventEnvelope? envelope,
        CaseContext? context,
        IReadOnlyDictionary<string, SubmissionPackage> packages,
        ICollection<string> errors)
    {
        if (envelope is null)
        {
            errors.Add("events cannot contain null values.");
            return;
        }

        ValidateVersion(envelope.SchemaVersion, $"event '{envelope.EventId}'.schemaVersion", errors);
        ValidateId(envelope.EventId, "event.eventId", errors);
        ValidateId(envelope.CorrelationId, "event.correlationId", errors);
        if (!WorkflowContract.EventTypes.Contains(envelope.Type))
        {
            errors.Add($"event '{envelope.EventId}' type '{envelope.Type}' is not a supported closed value.");
        }

        ValidateScope(
            envelope.RunId,
            envelope.CaseId,
            envelope.AirlineId,
            envelope.AircraftId,
            envelope.LeaseId,
            context,
            $"event '{envelope.EventId}'",
            errors);

        if (envelope.OccurredAt.Offset != TimeSpan.Zero ||
            envelope.ScenarioEffectiveAt.Offset != TimeSpan.Zero)
        {
            errors.Add($"event '{envelope.EventId}' timestamps must be UTC.");
        }

        if (envelope.Payload.ValueKind != JsonValueKind.Object ||
            !envelope.Payload.TryGetProperty("packageId", out var packageIdValue) ||
            packageIdValue.ValueKind != JsonValueKind.String)
        {
            errors.Add($"event '{envelope.EventId}' payload must contain packageId.");
            return;
        }

        var packageId = packageIdValue.GetString();
        if (packageId is null || !packages.ContainsKey(packageId))
        {
            errors.Add($"event '{envelope.EventId}' references an unknown package.");
        }

        if (envelope.Type == "partner.response.received" &&
            (!envelope.Payload.TryGetProperty("requestId", out var requestId) ||
             requestId.ValueKind != JsonValueKind.String ||
             string.IsNullOrWhiteSpace(requestId.GetString())))
        {
            errors.Add($"event '{envelope.EventId}' partner response payload must contain requestId.");
        }
    }

    private static void ValidateBoundaries(
        PathBoundaryDeclaration? boundaries,
        ICollection<string> errors)
    {
        if (boundaries is null)
        {
            errors.Add("pathBoundaries is required.");
            return;
        }

        var roots = new[]
        {
            ("application-inputs", boundaries.ApplicationInputsRoot),
            ("staged-responses", boundaries.StagedResponsesRoot),
            ("evaluator-only", boundaries.EvaluatorOnlyRoot),
            ("replay-only", boundaries.ReplayOnlyRoot)
        };
        var normalizedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, root) in roots)
        {
            var normalized = NormalizeRelativePath(root);
            if (normalized is null || normalized.Contains('/'))
            {
                errors.Add($"pathBoundaries.{name} root must be a single relative directory.");
            }
            else if (!normalizedRoots.Add(normalized))
            {
                errors.Add($"pathBoundaries.{name} root duplicates another boundary.");
            }
        }

        var selected = boundaries.SelectedInitialInputManifest?.Entries;
        if (selected is null || selected.Count == 0)
        {
            errors.Add("pathBoundaries.selectedInitialInputManifest must contain entries.");
            return;
        }

        var appRoot = NormalizeRelativePath(boundaries.ApplicationInputsRoot);
        var stagedRoot = NormalizeRelativePath(boundaries.StagedResponsesRoot);
        var evaluatorRoot = NormalizeRelativePath(boundaries.EvaluatorOnlyRoot);
        var replayRoot = NormalizeRelativePath(boundaries.ReplayOnlyRoot);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in selected)
        {
            if (entry is null)
            {
                errors.Add("selectedInitialInputManifest cannot contain null entries.");
                continue;
            }

            ValidateSha256(entry.Sha256, $"manifest entry '{entry.RelativePath}'", errors);
            ValidateId(entry.DocumentId, $"manifest entry '{entry.RelativePath}'.documentId", errors);
            if (entry.Version < 1)
            {
                errors.Add($"manifest entry '{entry.RelativePath}' version must be at least 1.");
            }

            var path = NormalizeRelativePath(entry.RelativePath);
            if (path is null)
            {
                errors.Add($"manifest entry '{entry.RelativePath}' must be a safe relative path.");
                continue;
            }

            if (!paths.Add(path))
            {
                errors.Add($"selectedInitialInputManifest contains duplicate path '{path}'.");
            }

            if (appRoot is null || !IsUnderRoot(path, appRoot))
            {
                errors.Add($"application input manifest path '{path}' is outside application-inputs.");
            }

            if (stagedRoot is not null && IsUnderRoot(path, stagedRoot) ||
                evaluatorRoot is not null && IsUnderRoot(path, evaluatorRoot) ||
                replayRoot is not null && IsUnderRoot(path, replayRoot))
            {
                errors.Add($"application input manifest path '{path}' crosses a protected generator boundary.");
            }
        }
    }

    private static void ValidateReceipt(
        ReproducibilityReceipt? receipt,
        GeneratorConfiguration? configuration,
        ICollection<string> errors)
    {
        if (receipt is null)
        {
            errors.Add("receipt is required.");
            return;
        }

        ValidateVersion(receipt.ContractVersion, "receipt.contractVersion", errors);
        ValidateVersion(receipt.TemplateVersion, "receipt.templateVersion", errors, allowAnyNonEmpty: true);
        if (configuration is null)
        {
            return;
        }

        if (receipt.Seed != configuration.Seed ||
            !StringComparer.Ordinal.Equals(receipt.FixtureVersion, configuration.FixtureVersion) ||
            receipt.ScenarioDate != configuration.ScenarioDate ||
            !StringComparer.Ordinal.Equals(receipt.RunId, configuration.RunId) ||
            !StringComparer.Ordinal.Equals(receipt.Profile, configuration.Profile))
        {
            errors.Add("receipt reproducibility values must match configuration.");
        }

        if (receipt.BuildDependencies is null)
        {
            errors.Add("receipt.buildDependencies is required.");
        }

        if (receipt.GeneratedFiles is null)
        {
            errors.Add("receipt.generatedFiles is required.");
        }
        else
        {
            foreach (var file in receipt.GeneratedFiles)
            {
                if (file is null)
                {
                    errors.Add("receipt.generatedFiles cannot contain null values.");
                    continue;
                }

                if (NormalizeRelativePath(file.RelativePath) is null)
                {
                    errors.Add($"receipt generated file '{file.RelativePath}' must be a safe relative path.");
                }

                ValidateSha256(file.Sha256, $"receipt generated file '{file.RelativePath}'", errors);
            }
        }

        if (receipt.IntendedMutationIdentifiers is null)
        {
            errors.Add("receipt.intendedMutationIdentifiers is required.");
        }
    }

    private static void ValidateScope(
        string runId,
        string caseId,
        string airlineId,
        string aircraftId,
        string leaseId,
        CaseContext? context,
        string owner,
        ICollection<string> errors)
    {
        ValidateId(runId, $"{owner}.runId", errors);
        ValidateId(caseId, $"{owner}.caseId", errors);
        ValidateId(airlineId, $"{owner}.airlineId", errors);
        ValidateId(aircraftId, $"{owner}.aircraftId", errors);
        ValidateId(leaseId, $"{owner}.leaseId", errors);
        if (context is null)
        {
            return;
        }

        if (!StringComparer.Ordinal.Equals(runId, context.RunId) ||
            !StringComparer.Ordinal.Equals(caseId, context.CaseId) ||
            !StringComparer.Ordinal.Equals(airlineId, context.AirlineId) ||
            !StringComparer.Ordinal.Equals(aircraftId, context.AircraftId) ||
            !StringComparer.Ordinal.Equals(leaseId, context.LeaseId))
        {
            errors.Add($"{owner} scope IDs must match case.");
        }
    }

    private static void ValidateVersion(
        string? version,
        string name,
        ICollection<string> errors,
        bool allowAnyNonEmpty = false)
    {
        if (allowAnyNonEmpty)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                errors.Add($"{name} is required.");
            }

            return;
        }

        if (!StringComparer.Ordinal.Equals(version, WorkflowContract.Version))
        {
            errors.Add($"{name} '{version}' is unsupported; expected {WorkflowContract.Version}.");
        }
    }

    private static void ValidateId(string? value, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            !value.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-'))
        {
            errors.Add($"{name} must be a non-empty opaque identifier.");
        }
    }

    private static void ValidateSha256(string? value, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != 64 ||
            !value.All(Uri.IsHexDigit))
        {
            errors.Add($"{name} must contain a SHA-256 hash.");
        }
    }

    private static string? NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return null;
        }

        return string.Join('/', segments);
    }

    private static bool IsUnderRoot(string path, string root) =>
        StringComparer.OrdinalIgnoreCase.Equals(path, root) ||
        path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}

public static class ReproducibilityReceiptFactory
{
    public static ReproducibilityReceipt Create(
        GeneratorConfiguration configuration,
        string templateVersion,
        IEnumerable<string> generatedFiles,
        IEnumerable<string> intendedMutationIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateVersion);
        ArgumentNullException.ThrowIfNull(generatedFiles);
        ArgumentNullException.ThrowIfNull(intendedMutationIdentifiers);

        var root = Path.GetFullPath(configuration.OutputDirectory);
        var hashes = generatedFiles
            .Select(path => CreateFileHash(root, path))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        return new ReproducibilityReceipt(
            WorkflowContract.Version,
            templateVersion,
            configuration.Seed,
            configuration.FixtureVersion,
            configuration.ScenarioDate,
            configuration.RunId,
            configuration.Profile,
            new Dictionary<string, string>(configuration.BuildDependencies, StringComparer.Ordinal),
            hashes,
            intendedMutationIdentifiers.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static GeneratedFileHash CreateFileHash(string root, string path)
    {
        var relativePath = NormalizePath(path);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Generated file '{relativePath}' does not exist below the output directory.",
                fullPath);
        }

        using var stream = File.OpenRead(fullPath);
        return new GeneratedFileHash(relativePath, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(path) || segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException($"Generated file path '{path}' must be relative and safe.", nameof(path));
        }

        return string.Join('/', segments);
    }
}

public static class ReproducibilityReceiptValidator
{
    public static GeneratorValidationResult ValidateFiles(
        ReproducibilityReceipt receipt,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var errors = new List<string>();
        var root = Path.GetFullPath(outputDirectory);
        foreach (var generatedFile in receipt.GeneratedFiles ?? [])
        {
            var relativePath = generatedFile.RelativePath.Replace('\\', '/');
            var fullPath = Path.GetFullPath(
                Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath))
            {
                errors.Add($"receipt generated file '{generatedFile.RelativePath}' is missing.");
                continue;
            }

            using var stream = File.OpenRead(fullPath);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, generatedFile.Sha256))
            {
                errors.Add($"receipt hash does not match generated file '{generatedFile.RelativePath}'.");
            }
        }

        return new GeneratorValidationResult(errors);
    }

    public static void ValidateFilesOrThrow(
        ReproducibilityReceipt receipt,
        string outputDirectory) =>
        ValidateFiles(receipt, outputDirectory).ThrowIfInvalid();
}

public static class GeneratorContractJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(GeneratorContractPackage package) =>
        JsonSerializer.Serialize(package, Options);

    public static GeneratorContractPackage Deserialize(string json) =>
        JsonSerializer.Deserialize<GeneratorContractPackage>(json, Options)
        ?? throw new JsonException("The generator contract package was empty.");
}
