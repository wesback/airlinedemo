using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirlineDemo.Generator;

public static class WorkflowContract
{
    public const string LiveMissingHistoryMutation = "live.missing-history";
    public const string LiveAmbiguousIdentityMutation = "live.ambiguous-identity";
    public const string ProcessingFailureCorruptDocumentMutation =
        "processing-failure.corrupt-document";
    public const string ApplicationInputArtifactClassification = "application-input";
    public const string StagedResponseArtifactClassification = "staged-response";
    public const string EvaluatorOnlyArtifactClassification = "evaluator-only";
    public const string ReplayArtifactClassification = "replay-only";

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

    public static readonly IReadOnlySet<string> LiveMutationIdentifiers =
        new HashSet<string>(StringComparer.Ordinal)
        {
            LiveMissingHistoryMutation,
            LiveAmbiguousIdentityMutation
        };

    public static readonly IReadOnlySet<string> ProcessingFailureMutationIdentifiers =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ProcessingFailureCorruptDocumentMutation
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

public sealed record ArtifactScope(
    string RunId,
    string CaseId,
    string AirlineId,
    string AircraftId,
    string LeaseId)
{
    public static ArtifactScope FromCase(CaseContext context) =>
        new(context.RunId, context.CaseId, context.AirlineId, context.AircraftId, context.LeaseId);
}

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
    string Sha256,
    ArtifactScope? Scope = null);

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

public sealed record ReceiptArtifact(
    string RelativePath,
    string Sha256,
    string Classification,
    ArtifactScope Scope);

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
    IReadOnlyList<string> IntendedMutationIdentifiers,
    IReadOnlyList<ReceiptArtifact>? ArtifactEvidence = null);

public sealed record ComponentMovement(
    string ComponentId,
    string SerialNumber,
    string AircraftId,
    string Action,
    DateTimeOffset OccurredAt,
    long FlightHours,
    long Cycles);

public sealed record UsageCounter(
    string ComponentId,
    DateTimeOffset AsOf,
    long FlightHours,
    long Cycles);

public sealed record BaselineFixture(
    string LessorName,
    string AircraftId,
    string EngineId,
    DateOnly PlannedReturnDate,
    IReadOnlyList<ComponentMovement> ComponentMovements,
    IReadOnlyList<UsageCounter> UsageCounters,
    IReadOnlyList<Requirement> ApprovedRequirements);

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
    ReproducibilityReceipt Receipt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BaselineFixture? Baseline { get; init; }
}

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

        ValidateBoundaries(
            package.PathBoundaries,
            package.Case,
            package.Configuration,
            errors);
        ValidateReceipt(package.Receipt, package.Configuration, package.Case, errors);
        ValidateIsolationReceipt(package, errors);
        BaselineFixtureValidator.ValidateLiveMutationDeclarations(package, errors);
        BaselineFixtureValidator.ValidateProcessingFailureMutationDeclarations(package, errors);
        BaselineFixtureValidator.ValidateDuplicateEventSequence(package, errors);
        if (package.Baseline is not null)
        {
            BaselineFixtureValidator.ValidateSemantic(package, errors);
        }
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
        CaseContext? context,
        GeneratorConfiguration? configuration,
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

            if (entry.Scope is null)
            {
                if (StringComparer.Ordinal.Equals(configuration?.Profile, "isolation"))
                {
                    errors.Add(
                        $"selectedInitialInputManifest entry '{path}' must record its isolation scope.");
                }
            }
            else
            {
                ValidateArtifactScope(entry.Scope, $"manifest entry '{path}'.scope", context, errors);
            }
        }
    }

    private static void ValidateReceipt(
        ReproducibilityReceipt? receipt,
        GeneratorConfiguration? configuration,
        CaseContext? context,
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

        foreach (var artifact in receipt.ArtifactEvidence ?? [])
        {
            if (artifact is null)
            {
                errors.Add("receipt.artifactEvidence cannot contain null values.");
                continue;
            }

            var path = NormalizeRelativePath(artifact.RelativePath);
            if (path is null)
            {
                errors.Add(
                    $"receipt artifact '{artifact.RelativePath}' must be a safe relative path.");
            }

            if (string.IsNullOrWhiteSpace(artifact.Classification))
            {
                errors.Add($"receipt artifact '{artifact.RelativePath}' classification is required.");
            }

            ValidateSha256(
                artifact.Sha256,
                $"receipt artifact '{artifact.RelativePath}'",
                errors);
            ValidateArtifactScope(
                artifact.Scope,
                $"receipt artifact '{artifact.RelativePath}'.scope",
                context,
                errors,
                allowDifferentScope: true);
        }
    }

    private static void ValidateArtifactScope(
        ArtifactScope? scope,
        string owner,
        CaseContext? context,
        ICollection<string> errors,
        bool allowDifferentScope = false)
    {
        if (scope is null)
        {
            errors.Add($"{owner} is required.");
            return;
        }

        ValidateId(scope.RunId, $"{owner}.runId", errors);
        ValidateId(scope.CaseId, $"{owner}.caseId", errors);
        ValidateId(scope.AirlineId, $"{owner}.airlineId", errors);
        ValidateId(scope.AircraftId, $"{owner}.aircraftId", errors);
        ValidateId(scope.LeaseId, $"{owner}.leaseId", errors);
        if (!allowDifferentScope &&
            context is not null &&
            (!StringComparer.Ordinal.Equals(scope.RunId, context.RunId) ||
             !StringComparer.Ordinal.Equals(scope.CaseId, context.CaseId) ||
             !StringComparer.Ordinal.Equals(scope.AirlineId, context.AirlineId) ||
             !StringComparer.Ordinal.Equals(scope.AircraftId, context.AircraftId) ||
             !StringComparer.Ordinal.Equals(scope.LeaseId, context.LeaseId)))
        {
            errors.Add($"{owner} scope IDs must match the selected initial case.");
        }
    }

    private static void ValidateIsolationReceipt(
        GeneratorContractPackage package,
        ICollection<string> errors)
    {
        if (!StringComparer.Ordinal.Equals(package.Configuration?.Profile, "isolation"))
        {
            return;
        }

        const string isolationRoot = "evaluator-only/isolation/cross-scope-package/";
        var caseContext = package.Case;
        var crossScopeArtifacts = (package.Receipt?.ArtifactEvidence ?? [])
            .Where(artifact => artifact.Scope is not null &&
                caseContext is not null &&
                (!StringComparer.Ordinal.Equals(artifact.Scope.RunId, caseContext.RunId) ||
                 !StringComparer.Ordinal.Equals(artifact.Scope.CaseId, caseContext.CaseId) ||
                 !StringComparer.Ordinal.Equals(artifact.Scope.AirlineId, caseContext.AirlineId) ||
                 !StringComparer.Ordinal.Equals(artifact.Scope.AircraftId, caseContext.AircraftId) ||
                 !StringComparer.Ordinal.Equals(artifact.Scope.LeaseId, caseContext.LeaseId)))
            .ToArray();
        if (crossScopeArtifacts.Length == 0)
        {
            errors.Add("isolation receipt must classify the cross-scope package artifacts.");
        }

        foreach (var artifact in crossScopeArtifacts)
        {
            var path = NormalizeRelativePath(artifact.RelativePath);
            if (path is null ||
                !path.StartsWith(isolationRoot, StringComparison.Ordinal) ||
                !StringComparer.Ordinal.Equals(
                    artifact.Classification,
                    WorkflowContract.EvaluatorOnlyArtifactClassification))
            {
                errors.Add(
                    $"isolation cross-scope artifact '{artifact.RelativePath}' must be evaluator-only.");
            }
        }

        var classifiedPaths = crossScopeArtifacts
            .Select(artifact => NormalizeRelativePath(artifact.RelativePath))
            .Where(path => path is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var generatedFile in package.Receipt?.GeneratedFiles ?? [])
        {
            var path = NormalizeRelativePath(generatedFile.RelativePath);
            if (path is not null &&
                path.Contains("cross-scope-package", StringComparison.Ordinal) &&
                !classifiedPaths.Contains(path))
            {
                errors.Add(
                    $"isolation cross-scope artifact '{path}' is omitted from evaluator-only artifact classification.");
            }
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

internal static class CanonicalJson
{
    public static string Serialize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}

public static class BaselineFixtureValidator
{
    public static GeneratorValidationResult Validate(
        GeneratedFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var errors = new List<string>();
        var package = fixture.ContractPackage;
        if (package.Baseline is null)
        {
            errors.Add("baseline fixture data is required.");
        }
        else
        {
            ValidateSemantic(package, errors);
            ValidateOutput(package, fixture.GeneratedFiles, package.Configuration.OutputDirectory, errors);
        }
        ValidateDuplicateEventSequence(package, errors);

        return new GeneratorValidationResult(errors);
    }

    public static GeneratorValidationResult ValidateFiles(
        GeneratorContractPackage package,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var errors = new List<string>();
        ValidateSemantic(package, errors);
        ValidateOutput(package, null, outputDirectory, errors);
        ValidateDuplicateEventSequence(package, errors);
        return new GeneratorValidationResult(errors);
    }

    public static void ValidateOrThrow(GeneratedFixture fixture) =>
        Validate(fixture).ThrowIfInvalid();

    internal static void ValidateSemantic(
        GeneratorContractPackage package,
        ICollection<string> errors)
    {
        var baseline = package.Baseline;
        if (baseline is null)
        {
            errors.Add("baseline fixture data is required.");
            return;
        }

        var scenarioStart = new DateTimeOffset(
            package.Configuration.ScenarioDate.ToDateTime(TimeOnly.MinValue),
            TimeSpan.Zero);
        if (!StringComparer.Ordinal.Equals(baseline.LessorName, "Altivane Aviation Capital"))
        {
            errors.Add("baseline lessor must be Altivane Aviation Capital.");
        }

        if (!StringComparer.Ordinal.Equals(baseline.AircraftId, "MOCK-AC-001") ||
            !StringComparer.Ordinal.Equals(baseline.EngineId, "MOCK-ENG-001"))
        {
            errors.Add("baseline aircraft and engine reference IDs are invalid.");
        }

        if (package.Asset is null ||
            !StringComparer.Ordinal.Equals(package.Asset.AircraftId, baseline.AircraftId) ||
            !StringComparer.Ordinal.Equals(package.Asset.EngineId, baseline.EngineId))
        {
            errors.Add("baseline asset relationships are inconsistent.");
        }

        var components = package.Asset?.Components ?? [];
        if (components.Count != 2 ||
            components.Any(component => component is null) ||
            components.Select(component => component.ComponentId).Distinct(StringComparer.Ordinal).Count() != 2)
        {
            errors.Add("baseline must contain exactly two distinct components.");
        }

        var componentIds = components
            .Where(component => component is not null)
            .Select(component => component.ComponentId)
            .ToHashSet(StringComparer.Ordinal);
        if (!componentIds.SetEquals(["COMP-0001", "COMP-0002"]) ||
            !components.Any(component =>
                component is not null &&
                component.ComponentId == "COMP-0001" &&
                component.SerialNumber == "SERIAL-0001") ||
            !components.Any(component =>
                component is not null &&
                component.ComponentId == "COMP-0002" &&
                component.SerialNumber == "SERIAL-0002"))
        {
            errors.Add("baseline component identities are invalid.");
        }
        var componentSerials = components
            .Where(component => component is not null)
            .GroupBy(component => component.ComponentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().SerialNumber, StringComparer.Ordinal);
        var movements = baseline.ComponentMovements ?? [];
        var counters = baseline.UsageCounters ?? [];
        foreach (var movement in movements)
        {
            if (movement is null ||
                !componentIds.Contains(movement.ComponentId) ||
                !StringComparer.Ordinal.Equals(movement.AircraftId, baseline.AircraftId) ||
                !componentSerials.TryGetValue(movement.ComponentId, out var serialNumber) ||
                !StringComparer.Ordinal.Equals(movement.SerialNumber, serialNumber) ||
                string.IsNullOrWhiteSpace(movement.SerialNumber) ||
                movement.FlightHours < 0 ||
                movement.Cycles < 0 ||
                movement.OccurredAt.Offset != TimeSpan.Zero ||
                movement.OccurredAt > new DateTimeOffset(
                    package.Configuration.ScenarioDate.ToDateTime(TimeOnly.MinValue),
                    TimeSpan.Zero) ||
                movement.Action is not ("installed" or "removed"))
            {
                errors.Add("baseline contains an invalid component movement relationship or value.");
            }
        }

        foreach (var componentId in componentIds)
        {
            var componentMovements = movements
                .Where(movement => movement is not null &&
                                   StringComparer.Ordinal.Equals(movement.ComponentId, componentId))
                .OrderBy(movement => movement.OccurredAt)
                .ToArray();
            for (var index = 1; index < componentMovements.Length; index++)
            {
                if (componentMovements[index].OccurredAt <= componentMovements[index - 1].OccurredAt ||
                    componentMovements[index].FlightHours < componentMovements[index - 1].FlightHours ||
                    componentMovements[index].Cycles < componentMovements[index - 1].Cycles ||
                    StringComparer.Ordinal.Equals(
                        componentMovements[index].Action,
                        componentMovements[index - 1].Action))
                {
                    errors.Add($"baseline movement chronology or usage is invalid for component '{componentId}'.");
                }
            }
            if (componentMovements.Length == 0 ||
                !StringComparer.Ordinal.Equals(componentMovements[0].Action, "installed"))
            {
                errors.Add($"baseline component movement history is invalid for component '{componentId}'.");
            }

            var componentCounters = counters
                .Where(counter => counter is not null &&
                                  StringComparer.Ordinal.Equals(counter.ComponentId, componentId))
                .OrderBy(counter => counter.AsOf)
                .ToArray();
            for (var index = 0; index < componentCounters.Length; index++)
            {
                var counter = componentCounters[index];
                if (counter.AsOf.Offset != TimeSpan.Zero ||
                    counter.FlightHours < 0 ||
                    counter.Cycles < 0 ||
                    (index > 0 &&
                     (counter.AsOf <= componentCounters[index - 1].AsOf ||
                      counter.FlightHours < componentCounters[index - 1].FlightHours ||
                      counter.Cycles < componentCounters[index - 1].Cycles)))
                {
                    errors.Add($"baseline usage counters are invalid for component '{componentId}'.");
                }
            }

            if (componentCounters.Length == 0)
            {
                errors.Add($"baseline has no usage counter for component '{componentId}'.");
            }
        }

        if (counters.Any(counter => counter is null || !componentIds.Contains(counter.ComponentId)))
        {
            errors.Add("baseline usage counters reference an unknown component.");
        }

        var approvedRequirements = baseline.ApprovedRequirements ?? [];
        if (approvedRequirements.Count == 0 ||
            approvedRequirements.Any(requirement => requirement is null ||
                !componentIds.Contains(requirement.ComponentId) ||
                !StringComparer.Ordinal.Equals(requirement.ApprovedBy, "fixture-setup") ||
                requirement.ApprovedAt.Offset != TimeSpan.Zero ||
                requirement.ApprovedAt > scenarioStart ||
                (requirement.RequiredFrom is not null &&
                 requirement.ApprovedAt > requirement.RequiredFrom)))
        {
            errors.Add("baseline approved mock requirements are invalid.");
        }

        var initialPackage = package.SubmissionPackages?
            .SingleOrDefault(submissionPackage =>
                submissionPackage is not null &&
                StringComparer.Ordinal.Equals(submissionPackage.PackageId, "PKG-0001"));
        var packageDocuments = initialPackage?.Manifest?
            .ToArray() ?? [];
        if (packageDocuments.Length is < 8 or > 12)
        {
            errors.Add("baseline submission package must contain between 8 and 12 documents.");
        }

        if (packageDocuments.Any(document =>
                document is null ||
                document.IssuedOn.Offset != TimeSpan.Zero ||
                document.IssuedOn > scenarioStart) ||
            package.SubmissionPackages?.Any(submissionPackage =>
                submissionPackage.SubmittedAt < submissionPackage.ScenarioEffectiveAt ||
                submissionPackage.ScenarioEffectiveAt < scenarioStart) == true)
        {
            errors.Add("baseline document or package chronology is invalid.");
        }

        var documentIds = package.Documents?
            .Select(document => document.DocumentId)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        if (packageDocuments.Any(document => document is null || !documentIds.Contains(document.DocumentId)))
        {
            errors.Add("baseline submission package contains an unresolved document reference.");
        }

        if (package.Configuration.Profile == "baseline" &&
            ((package.Receipt?.IntendedMutationIdentifiers?.Count > 0) ||
             package.Events?.Any(envelope =>
                 envelope.Payload.TryGetProperty("finding", out _) ||
                 envelope.Payload.TryGetProperty("policyDecision", out _) ||
                 envelope.Payload.TryGetProperty("approval", out _)) == true))
        {
            errors.Add("baseline must not contain intentional mutation metadata or generated workflow outcomes.");
        }

        var requirementDocumentIds = package.Documents?
            .Select(document => document.DocumentId)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        if (approvedRequirements.Any(requirement =>
                requirement is null ||
                !requirementDocumentIds.Contains(requirement.SourceRef)))
        {
            errors.Add("baseline requirement contains an unresolved document reference.");
        }
    }

    private static void ValidateOutput(
        GeneratorContractPackage package,
        IReadOnlyList<string>? generatedFiles,
        string outputDirectory,
        ICollection<string> errors)
    {
        var root = Path.GetFullPath(outputDirectory);
        var selected = package.PathBoundaries?.SelectedInitialInputManifest?.Entries ?? [];
        var initialPackage = package.SubmissionPackages?
            .SingleOrDefault(submissionPackage =>
                submissionPackage is not null &&
                StringComparer.Ordinal.Equals(submissionPackage.PackageId, "PKG-0001"));
        var documents = initialPackage?.Manifest?
            .Where(document => document is not null)
            .ToDictionary(document => (document.DocumentId, document.Version))
            ?? new Dictionary<(string DocumentId, int Version), Document>();

        foreach (var entry in selected)
        {
            var fullPath = ResolveOutputPath(root, entry.RelativePath);
            if (fullPath is null || !File.Exists(fullPath))
            {
                errors.Add($"baseline manifest file '{entry.RelativePath}' is missing or outside the output directory.");
                continue;
            }

            var hash = HashFile(fullPath);
            if (!StringComparer.OrdinalIgnoreCase.Equals(hash, entry.Sha256))
            {
                errors.Add($"baseline manifest hash does not match '{entry.RelativePath}'.");
            }

            if (!documents.TryGetValue((entry.DocumentId, entry.Version), out var document) ||
                !StringComparer.OrdinalIgnoreCase.Equals(document.Sha256, entry.Sha256) ||
                !StringComparer.Ordinal.Equals(document.FileName, Path.GetFileName(fullPath)))
            {
                errors.Add($"baseline manifest entry '{entry.RelativePath}' does not resolve to its document.");
            }
        }

        var manifestPath = ResolveOutputPath(root, "application-inputs/package-001/manifest.json");
        if (manifestPath is null || !File.Exists(manifestPath))
        {
            errors.Add("baseline submission-package manifest file is missing.");
        }
        else
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<SubmissionPackage>(
                    File.ReadAllText(manifestPath),
                    GeneratorContractJson.Options);
                if (manifest is null ||
                    !StringComparer.Ordinal.Equals(manifest.PackageId, "PKG-0001") ||
                    manifest.Manifest.Count != documents.Count ||
                    manifest.Manifest.Any(document =>
                        !documents.TryGetValue((document.DocumentId, document.Version), out var expected) ||
                        !StringComparer.Ordinal.Equals(document.Sha256, expected.Sha256)))
                {
                    errors.Add("baseline submission-package manifest does not match the generated documents.");
                }
            }
            catch (JsonException)
            {
                errors.Add("baseline submission-package manifest is not valid JSON.");
            }
        }

        if (generatedFiles is not null)
        {
            foreach (var generatedFile in generatedFiles)
            {
                if (ResolveOutputPath(root, generatedFile) is null)
                {
                    errors.Add($"generated file '{generatedFile}' is outside the output directory.");
                }
            }
        }

        ValidateLiveMutationFiles(package, root, generatedFiles, errors);
        ValidateProcessingFailureFiles(package, root, generatedFiles, errors);
        ValidateStagedResponse(package, root, errors);
        ValidateIsolationArtifacts(package, root, generatedFiles, errors);
    }

    internal static void ValidateLiveMutationDeclarations(
        GeneratorContractPackage package,
        ICollection<string> errors)
    {
        var mutations = package.Receipt?.IntendedMutationIdentifiers ?? [];
        if (mutations.Distinct(StringComparer.Ordinal).Count() != mutations.Count)
        {
            errors.Add("fixture mutation identifiers must be unique.");
        }

        if (package.Configuration?.Profile == "live")
        {
            foreach (var mutation in mutations)
            {
                if (!WorkflowContract.LiveMutationIdentifiers.Contains(mutation))
                {
                    errors.Add($"live fixture contains an undeclared mutation '{mutation}'.");
                }
            }
        }
        else if (mutations.Any(mutation => WorkflowContract.LiveMutationIdentifiers.Contains(mutation)))
        {
            errors.Add("live mutation identifiers require the live profile.");
        }
    }

    internal static void ValidateProcessingFailureMutationDeclarations(
        GeneratorContractPackage package,
        ICollection<string> errors)
    {
        var mutations = package.Receipt?.IntendedMutationIdentifiers ?? [];
        if (package.Configuration?.Profile == "processing-failure")
        {
            foreach (var mutation in mutations)
            {
                if (!WorkflowContract.ProcessingFailureMutationIdentifiers.Contains(mutation))
                {
                    errors.Add(
                        $"processing-failure fixture contains an undeclared mutation '{mutation}'.");
                }
            }
        }
        else if (mutations.Any(mutation =>
                     WorkflowContract.ProcessingFailureMutationIdentifiers.Contains(mutation)))
        {
            errors.Add(
                "processing-failure mutation identifiers require the processing-failure profile.");
        }
    }

    internal static void ValidateDuplicateEventSequence(
        GeneratorContractPackage package,
        ICollection<string> errors)
    {
        if (package.Configuration?.Profile != "duplicate-events")
        {
            return;
        }

        var events = package.Events ?? [];
        if (events.Count != 3)
        {
            errors.Add("duplicate-events profile must contain exactly three replay envelopes.");
            return;
        }

        var first = events[0];
        var duplicate = events[1];
        var conflict = events[2];
        var declaredPackageIds = (package.SubmissionPackages ?? [])
            .Where(submissionPackage => submissionPackage is not null)
            .Select(submissionPackage => submissionPackage.PackageId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var envelope in events)
        {
            if (string.IsNullOrWhiteSpace(envelope.RunId) ||
                string.IsNullOrWhiteSpace(envelope.CaseId) ||
                string.IsNullOrWhiteSpace(envelope.AirlineId) ||
                string.IsNullOrWhiteSpace(envelope.AircraftId) ||
                string.IsNullOrWhiteSpace(envelope.LeaseId))
            {
                errors.Add(
                    $"duplicate-events envelope '{envelope.EventId}' is missing required scope IDs.");
            }

            if (string.IsNullOrWhiteSpace(envelope.CorrelationId))
            {
                errors.Add(
                    $"duplicate-events envelope '{envelope.EventId}' is missing a correlation ID.");
            }

            if (envelope.Payload.ValueKind != JsonValueKind.Object ||
                !envelope.Payload.TryGetProperty("packageId", out var packageId) ||
                packageId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(packageId.GetString()) ||
                !declaredPackageIds.Contains(packageId.GetString()!))
            {
                errors.Add(
                    $"duplicate-events envelope '{envelope.EventId}' is missing a declared package reference.");
            }
        }

        var firstKey = (first.RunId, first.EventId);
        if ((duplicate.RunId, duplicate.EventId) != firstKey)
        {
            errors.Add(
                "duplicate-events replay envelopes must reuse the same (runId,eventId) key.");
        }

        if (!StringComparer.Ordinal.Equals(
                CanonicalJson.Serialize(first.Payload),
                CanonicalJson.Serialize(duplicate.Payload)))
        {
            errors.Add("duplicate-events identical pair must have identical canonical payloads.");
        }

        if ((conflict.RunId, conflict.EventId) != firstKey)
        {
            errors.Add(
                "duplicate-events conflicting envelope must reuse the same (runId,eventId) key.");
        }

        if (StringComparer.Ordinal.Equals(
                CanonicalJson.Serialize(first.Payload),
                CanonicalJson.Serialize(conflict.Payload)))
        {
            errors.Add(
                "duplicate-events conflicting envelope must have a different canonical payload.");
        }

        if (events.Select(envelope => envelope.OccurredAt).Distinct().Count() != events.Count)
        {
            errors.Add("duplicate-events envelopes must have distinct occurredAt timestamps.");
        }

        if (events.Select(envelope => envelope.ScenarioEffectiveAt).Distinct().Count() != events.Count)
        {
            errors.Add(
                "duplicate-events envelopes must have distinct scenarioEffectiveAt timestamps.");
        }
    }

    private static void ValidateLiveMutationFiles(
        GeneratorContractPackage package,
        string root,
        IReadOnlyList<string>? generatedFiles,
        ICollection<string> errors)
    {
        if (package.Configuration?.Profile != "live")
        {
            return;
        }

        var mutations = package.Receipt?.IntendedMutationIdentifiers ?? [];
        var missingHistoryPath = ResolveOutputPath(
            root,
            "application-inputs/package-001/004-component-a-removal-history.pdf");
        var hasMissingHistory = !File.Exists(missingHistoryPath);
        if (hasMissingHistory != mutations.Contains(WorkflowContract.LiveMissingHistoryMutation))
        {
            errors.Add(
                "live component A removal-history defect must match its declared mutation.");
        }

        var normalIdentityPath = ResolveOutputPath(
            root,
            "application-inputs/package-001/005-component-b-installation.pdf");
        var ambiguousIdentityPath = ResolveOutputPath(
            root,
            "application-inputs/package-001/005-component-b-identity-scan.pdf");
        var hasAmbiguousIdentity = File.Exists(ambiguousIdentityPath);
        if (hasAmbiguousIdentity != mutations.Contains(WorkflowContract.LiveAmbiguousIdentityMutation))
        {
            errors.Add(
                "live component B identity defect must match its declared mutation.");
        }

        if (!hasAmbiguousIdentity && !File.Exists(normalIdentityPath))
        {
            errors.Add("live component B identity record is missing without a declared mutation.");
        }

        if (hasAmbiguousIdentity)
        {
            var scanText = File.ReadAllText(ambiguousIdentityPath!, System.Text.Encoding.ASCII);
            if (scanText.Contains("SERIAL-0002", StringComparison.Ordinal))
            {
                errors.Add("live component B identity scan leaks the clean serial.");
            }

            var scanDocument = package.Documents?.SingleOrDefault(document =>
                document is not null &&
                StringComparer.Ordinal.Equals(document.SourceRecordId, "SOURCE-SCAN-0002"));
            if (scanDocument is null ||
                !StringComparer.Ordinal.Equals(
                    scanDocument.FileName,
                    "005-component-b-identity-scan.pdf") ||
                scanDocument.FileName.Contains("SERIAL-0002", StringComparison.Ordinal))
            {
                errors.Add("live component B identity scan metadata or filename is invalid.");
            }

            if (!scanText.Contains("/Title (Component B identity scan)", StringComparison.Ordinal) ||
                !scanText.Contains("/Subject (Component B identity scan:", StringComparison.Ordinal) ||
                !scanText.Contains("/Alt (Component B identity scan:", StringComparison.Ordinal))
            {
                errors.Add("live component B identity scan must provide safe metadata and alternate text.");
            }

            var candidateCount = scanText
                .Split("Candidate serial:", StringSplitOptions.None)
                .Length - 1;
            if (candidateCount < 2)
            {
                errors.Add("live component B identity scan must contain at least two candidates.");
            }
        }

        if (generatedFiles is not null)
        {
            foreach (var generatedFile in generatedFiles.Where(path =>
                         path.StartsWith("staged-responses/", StringComparison.Ordinal) ||
                         path.StartsWith("replay-only/", StringComparison.Ordinal)))
            {
                var path = ResolveOutputPath(root, generatedFile);
                if (path is not null &&
                    File.Exists(path) &&
                    File.ReadAllText(path).Contains("SERIAL-0002", StringComparison.Ordinal))
                {
                    errors.Add(
                        $"live generator output '{generatedFile}' leaks the clean component B serial.");
                }
            }
        }
    }

    private static void ValidateProcessingFailureFiles(
        GeneratorContractPackage package,
        string root,
        IReadOnlyList<string>? generatedFiles,
        ICollection<string> errors)
    {
        if (package.Configuration?.Profile != "processing-failure")
        {
            return;
        }

        const string mutation = WorkflowContract.ProcessingFailureCorruptDocumentMutation;
        const string corruptPath = "application-inputs/package-001/011-corrupt-document.pdf";
        var mutations = package.Receipt?.IntendedMutationIdentifiers ?? [];
        var corruptFile = ResolveOutputPath(root, corruptPath);
        if (!mutations.Contains(mutation))
        {
            errors.Add("processing-failure corrupt document mutation is not declared.");
        }

        if (corruptFile is null || !File.Exists(corruptFile))
        {
            errors.Add("processing-failure corrupt document artifact is missing.");
        }

        var corruptDocument = package.Documents?.SingleOrDefault(document =>
            document is not null &&
            StringComparer.Ordinal.Equals(document.SourceRecordId, "SOURCE-CORRUPT-0001"));
        var submittedManifest = package.SubmissionPackages?
            .SingleOrDefault(submissionPackage =>
                submissionPackage is not null &&
                StringComparer.Ordinal.Equals(submissionPackage.PackageId, "PKG-0001"))
            ?.Manifest?
            .Where(document => document is not null)
            .ToArray() ?? [];
        if (corruptDocument is null)
        {
            errors.Add("processing-failure corrupt document metadata is missing.");
        }
        else
        {
            var manifestDocument = submittedManifest.SingleOrDefault(document =>
                StringComparer.Ordinal.Equals(document.DocumentId, corruptDocument.DocumentId) &&
                document.Version == corruptDocument.Version);
            if (manifestDocument is null)
            {
                errors.Add(
                    "processing-failure corrupt document is absent from the submitted manifest.");
            }
            else if (!StringComparer.OrdinalIgnoreCase.Equals(
                         manifestDocument.Sha256,
                         corruptDocument.Sha256))
            {
                errors.Add("processing-failure corrupt document manifest hash does not match its document.");
            }

            if (corruptFile is not null && File.Exists(corruptFile))
            {
                var actualHash = HashFile(corruptFile);
                if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, corruptDocument.Sha256))
                {
                    errors.Add(
                        "processing-failure corrupt document hash does not match the rendered artifact.");
                }
            }
        }

        var metadataPath = ResolveOutputPath(root, "evaluator-only/scenario-metadata.json");
        if (metadataPath is null || !File.Exists(metadataPath))
        {
            errors.Add("processing-failure mutation must be declared in evaluator-only metadata.");
        }
        else
        {
            try
            {
                using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
                var rootElement = metadata.RootElement;
                var hasMutationDeclaration = rootElement.TryGetProperty(
                    "intendedMutations",
                    out var intendedMutations) &&
                    intendedMutations.ValueKind == JsonValueKind.Array &&
                    intendedMutations.EnumerateArray()
                        .Any(value => value.ValueKind == JsonValueKind.String &&
                                      value.GetString() == mutation);
                if (!hasMutationDeclaration)
                {
                    errors.Add(
                        "processing-failure mutation is not declared in evaluator-only metadata.");
                }

                var hasMatchingDetail = false;
                if (rootElement.TryGetProperty("mutationDetails", out var mutationDetails) &&
                    mutationDetails.ValueKind == JsonValueKind.Array)
                {
                    foreach (var detail in mutationDetails.EnumerateArray())
                    {
                        if (detail.ValueKind != JsonValueKind.Object ||
                            !detail.TryGetProperty("identifier", out var identifier) ||
                            identifier.GetString() != mutation)
                        {
                            continue;
                        }

                        hasMatchingDetail = corruptDocument is not null &&
                            detail.TryGetProperty("documentId", out var documentId) &&
                            detail.TryGetProperty("relativePath", out var relativePath) &&
                            detail.TryGetProperty("sha256", out var sha256) &&
                            documentId.GetString() == corruptDocument.DocumentId &&
                            relativePath.GetString() == corruptPath &&
                            StringComparer.OrdinalIgnoreCase.Equals(
                                sha256.GetString(),
                                corruptDocument.Sha256);
                        break;
                    }
                }

                if (!hasMatchingDetail)
                {
                    errors.Add(
                        "processing-failure evaluator-only metadata does not identify the corrupt document and hash.");
                }
            }
            catch (JsonException)
            {
                errors.Add("processing-failure evaluator-only metadata is not valid JSON.");
            }
        }

        if (generatedFiles is not null &&
            !generatedFiles.Contains(corruptPath, StringComparer.Ordinal))
        {
            errors.Add("processing-failure corrupt document is missing from generated files.");
        }
    }

    private static void ValidateStagedResponse(
        GeneratorContractPackage package,
        string root,
        ICollection<string> errors)
    {
        var manifestPath = ResolveOutputPath(
            root,
            "staged-responses/package-002/manifest.json");
        var responsePath = ResolveOutputPath(
            root,
            "staged-responses/package-002/response.pdf");
        if (manifestPath is null ||
            responsePath is null ||
            !File.Exists(manifestPath) ||
            !File.Exists(responsePath))
        {
            errors.Add("staged partner-response package is incomplete.");
            return;
        }

        try
        {
            var manifestJson = File.ReadAllText(manifestPath);
            var responseText = File.ReadAllText(responsePath);
            if (manifestJson.Contains("requestId", StringComparison.OrdinalIgnoreCase) ||
                responseText.Contains("requestId", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("staged partner-response package must not contain a fabricated request ID.");
            }

            if (manifestJson.Contains("evaluator-only", StringComparison.OrdinalIgnoreCase) ||
                responseText.Contains("evaluator-only", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("staged partner-response manifest must not reference evaluator-only data.");
            }
        }
        catch (IOException)
        {
            errors.Add("staged partner-response manifest could not be read.");
        }
    }

    private static void ValidateIsolationArtifacts(
        GeneratorContractPackage package,
        string root,
        IReadOnlyList<string>? generatedFiles,
        ICollection<string> errors)
    {
        if (!StringComparer.Ordinal.Equals(package.Configuration?.Profile, "isolation"))
        {
            return;
        }

        const string isolationRoot = "evaluator-only/isolation/cross-scope-package/";
        var receiptArtifacts = package.Receipt?.ArtifactEvidence ?? [];
        var crossScopeArtifacts = receiptArtifacts
            .Where(artifact => artifact.Scope is not null &&
                package.Case is not null &&
                (!StringComparer.Ordinal.Equals(artifact.Scope.RunId, package.Case.RunId) ||
                 !StringComparer.Ordinal.Equals(artifact.Scope.CaseId, package.Case.CaseId) ||
                 !StringComparer.Ordinal.Equals(artifact.Scope.AirlineId, package.Case.AirlineId) ||
                 !StringComparer.Ordinal.Equals(artifact.Scope.AircraftId, package.Case.AircraftId) ||
                 !StringComparer.Ordinal.Equals(artifact.Scope.LeaseId, package.Case.LeaseId)))
            .ToArray();

        if (crossScopeArtifacts.Length == 0)
        {
            errors.Add("isolation receipt must classify the cross-scope package artifacts.");
        }

        foreach (var artifact in crossScopeArtifacts)
        {
            var path = NormalizeIsolationPath(artifact.RelativePath);
            if (path is null ||
                !path.StartsWith(isolationRoot, StringComparison.Ordinal) ||
                !StringComparer.Ordinal.Equals(
                    artifact.Classification,
                    WorkflowContract.EvaluatorOnlyArtifactClassification))
            {
                errors.Add(
                    $"isolation cross-scope artifact '{artifact.RelativePath}' must be evaluator-only.");
                continue;
            }

            var fullPath = ResolveOutputPath(root, path);
            if (fullPath is null || !File.Exists(fullPath))
            {
                errors.Add($"isolation cross-scope artifact '{path}' is missing.");
                continue;
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(HashFile(fullPath), artifact.Sha256))
            {
                errors.Add($"isolation cross-scope artifact hash does not match '{path}'.");
            }
        }

        var generatedIsolationPaths = (generatedFiles ??
                package.Receipt?.GeneratedFiles?.Select(file => file.RelativePath).ToArray() ??
                [])
            .Select(NormalizeIsolationPath)
            .Where(path => path is not null &&
                path.Contains("cross-scope-package", StringComparison.Ordinal))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var classifiedIsolationPaths = crossScopeArtifacts
            .Select(artifact => NormalizeIsolationPath(artifact.RelativePath))
            .Where(path => path is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in generatedIsolationPaths)
        {
            if (!classifiedIsolationPaths.Contains(path))
            {
                errors.Add(
                    $"isolation cross-scope artifact '{path}' is omitted from evaluator-only artifact classification.");
            }
        }
    }

    private static string? NormalizeIsolationPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/');
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..")
            ? null
            : string.Join(
                '/',
                normalized.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? ResolveOutputPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or ".."))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(
            Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public static class ReproducibilityReceiptFactory
{
    public static ReproducibilityReceipt Create(
        GeneratorConfiguration configuration,
        string templateVersion,
        IEnumerable<string> generatedFiles,
        IEnumerable<string> intendedMutationIdentifiers,
        IEnumerable<ReceiptArtifact>? artifactEvidence = null)
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
        var hashesByPath = hashes.ToDictionary(
            file => file.RelativePath,
            file => file.Sha256,
            StringComparer.Ordinal);
        var evidence = artifactEvidence?
            .Select(artifact =>
            {
                var path = NormalizePath(artifact.RelativePath);
                if (!hashesByPath.TryGetValue(path, out var hash))
                {
                    throw new ArgumentException(
                        $"Artifact evidence path '{path}' is not a generated file.",
                        nameof(artifactEvidence));
                }

                return artifact with { RelativePath = path, Sha256 = hash };
            })
            .OrderBy(artifact => artifact.RelativePath, StringComparer.Ordinal)
            .ToArray()
            ?? [];

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
            intendedMutationIdentifiers.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            evidence);
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
