using System.Text.Json;

namespace AirlineDemo.Generator;

public sealed record GeneratedFixture(
    GeneratorContractPackage ContractPackage,
    ReproducibilityReceipt Receipt,
    IReadOnlyList<string> GeneratedFiles);

public static class FixtureGenerator
{
    public static GeneratedFixture Generate(
        GeneratorConfiguration configuration,
        string templateVersion,
        IEnumerable<string>? intendedMutationIdentifiers = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateVersion);

        var mutations = (intendedMutationIdentifiers ?? [])
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var scenarioStart = new DateTimeOffset(
            configuration.ScenarioDate.ToDateTime(TimeOnly.MinValue),
            TimeSpan.Zero);
        var caseContext = new CaseContext(
            configuration.RunId,
            $"CASE-{configuration.RunId}",
            "AIRLINE-0001",
            "MOCK-AC-001",
            "LEASE-0001");
        var asset = new Asset(
            caseContext.AircraftId,
            "MOCK-ENG-001",
            [
                new AssetComponent("COMP-0001", "SERIAL-0001"),
                new AssetComponent("COMP-0002", "SERIAL-0002")
            ],
            new AssetReference("asset-register", 1));
        var requirement = new Requirement(
            "REQ-0001",
            1,
            "COMP-0001",
            "maintenance-record",
            "Installation record",
            "fixture-setup",
            "mock-setup",
            scenarioStart.AddHours(8),
            "CHECKLIST-0001");
        const string documentPath = "application-inputs/package-001/maintenance-record.txt";
        var documentContent = string.Join(
            Environment.NewLine,
            "Synthetic maintenance record",
            $"Run: {configuration.RunId}",
            $"Scenario date: {configuration.ScenarioDate:yyyy-MM-dd}",
            "Component: COMP-0001");
        var outputDirectory = Path.GetFullPath(configuration.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        WriteText(outputDirectory, documentPath, documentContent);
        var documentHash = HashFile(outputDirectory, documentPath);
        var document = new Document(
            "DOC-0001",
            1,
            "fixture-generator",
            "SOURCE-0001",
            "maintenance-record.txt",
            "text/plain",
            documentHash,
            scenarioStart.AddHours(7));
        var submissionPackage = new SubmissionPackage(
            WorkflowContract.Version,
            "PKG-0001",
            caseContext.RunId,
            caseContext.CaseId,
            caseContext.AirlineId,
            caseContext.AircraftId,
            caseContext.LeaseId,
            scenarioStart.AddHours(13),
            scenarioStart.AddHours(9),
            [document]);
        using var payload = JsonDocument.Parse("""{"packageId":"PKG-0001"}""");
        var eventEnvelope = new EventEnvelope(
            WorkflowContract.Version,
            "EVT-0001",
            "package.submitted",
            caseContext.RunId,
            caseContext.CaseId,
            caseContext.AirlineId,
            caseContext.AircraftId,
            caseContext.LeaseId,
            scenarioStart.AddHours(13),
            scenarioStart.AddHours(9),
            "CORR-0001",
            payload.RootElement.Clone());
        var boundaries = PathBoundaryDeclaration.Default(
            new SelectedInitialInputManifest(
            [
                new ManifestEntry(documentPath, document.DocumentId, document.Version, documentHash)
            ]));

        WriteJson(outputDirectory, "application-inputs/reference-data/case-context.json", caseContext);
        WriteJson(outputDirectory, "application-inputs/reference-data/asset.json", asset);
        WriteJson(
            outputDirectory,
            "application-inputs/reference-data/requirements.json",
            new[] { requirement });
        WriteJson(outputDirectory, "replay-only/events.json", new[] { eventEnvelope });
        WriteJson(
            outputDirectory,
            "evaluator-only/scenario-metadata.json",
            new
            {
                contractVersion = WorkflowContract.Version,
                fixtureVersion = configuration.FixtureVersion,
                profile = configuration.Profile,
                intendedMutations = mutations
            });

        var generatedFiles = new[]
        {
            documentPath,
            "application-inputs/reference-data/case-context.json",
            "application-inputs/reference-data/asset.json",
            "application-inputs/reference-data/requirements.json",
            "replay-only/events.json",
            "evaluator-only/scenario-metadata.json"
        };
        var receipt = ReproducibilityReceiptFactory.Create(
            configuration,
            templateVersion,
            generatedFiles,
            mutations);
        var package = new GeneratorContractPackage(
            WorkflowContract.Version,
            configuration,
            caseContext,
            asset,
            [requirement],
            [document],
            [submissionPackage],
            [eventEnvelope],
            boundaries,
            receipt);
        GeneratorContractValidator.ValidateOrThrow(package);
        WriteJson(outputDirectory, "generator-contract.json", package);
        WriteJson(outputDirectory, "reproducibility-receipt.json", receipt);

        return new GeneratedFixture(package, receipt, generatedFiles);
    }

    private static void WriteJson(string root, string relativePath, object value) =>
        WriteText(root, relativePath, JsonSerializer.Serialize(value, GeneratorContractJson.Options));

    private static void WriteText(string root, string relativePath, string content)
    {
        var fullPath = ResolvePath(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static string HashFile(string root, string relativePath)
    {
        using var stream = File.OpenRead(ResolvePath(root, relativePath));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string ResolvePath(string root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(relativePath) ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException($"Generated path '{relativePath}' must be relative and safe.");
        }

        var fullPath = Path.GetFullPath(
            Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Generated path '{relativePath}' escapes the output directory.");
        }

        return fullPath;
    }
}
