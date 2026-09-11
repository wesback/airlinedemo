using System.Text;
using System.Text.Json;

namespace AirlineDemo.Generator;

public sealed record GeneratedFixture(
    GeneratorContractPackage ContractPackage,
    ReproducibilityReceipt Receipt,
    IReadOnlyList<string> GeneratedFiles)
{
    public BaselineFixture? Baseline => ContractPackage.Baseline;
}

public static class FixtureGenerator
{
    public static GeneratedFixture Generate(
        GeneratorConfiguration configuration,
        string templateVersion,
        IEnumerable<string>? intendedMutationIdentifiers = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateVersion);

        var mutations = (intendedMutationIdentifiers ??
                (configuration.Profile == "live"
                    ? WorkflowContract.LiveMutationIdentifiers
                    : []))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var outputDirectory = Path.GetFullPath(configuration.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        if (mutations.Length == 0)
        {
            var mutationMetadataPath = ResolvePath(
                outputDirectory,
                "evaluator-only/scenario-metadata.json");
            if (File.Exists(mutationMetadataPath))
            {
                File.Delete(mutationMetadataPath);
            }
        }
        var scenarioStart = new DateTimeOffset(
            configuration.ScenarioDate.ToDateTime(TimeOnly.MinValue),
            TimeSpan.Zero);
        var plannedReturnDate = configuration.ScenarioDate.AddMonths(9);
        var caseContext = new CaseContext(
            configuration.RunId,
            $"CASE-{configuration.RunId}",
            "AIRLINE-0001",
            "MOCK-AC-001",
            "LEASE-0001");
        var caseScope = ArtifactScope.FromCase(caseContext);
        var isolationScope = new ArtifactScope(
            configuration.RunId,
            "CASE-0002",
            "AIRLINE-0002",
            "MOCK-AC-002",
            "LEASE-0002");
        var asset = new Asset(
            caseContext.AircraftId,
            "MOCK-ENG-001",
            [
                new AssetComponent("COMP-0001", "SERIAL-0001"),
                new AssetComponent("COMP-0002", "SERIAL-0002")
            ],
            new AssetReference("asset-register", 1));
        var requirements = CreateRequirements(scenarioStart, plannedReturnDate);
        var baseline = CreateBaseline(scenarioStart, plannedReturnDate, requirements);
        var documentDefinitions = CreateDocumentDefinitions(
            configuration,
            scenarioStart,
            plannedReturnDate,
            baseline,
            mutations.ToHashSet(StringComparer.Ordinal));
        var documentPaths = new List<string>();

        DeleteIfPresent(outputDirectory, "application-inputs/package-001/004-component-a-removal-history.pdf");
        DeleteIfPresent(outputDirectory, "application-inputs/package-001/005-component-b-installation.pdf");
        DeleteIfPresent(outputDirectory, "application-inputs/package-001/005-component-b-identity-scan.pdf");
        DeleteIfPresent(
            outputDirectory,
            "evaluator-only/isolation/cross-scope-package/001-aircraft-record.pdf");
        DeleteIfPresent(
            outputDirectory,
            "evaluator-only/isolation/cross-scope-package/manifest.json");
        foreach (var definition in documentDefinitions)
        {
            WriteRenderedDocument(
                outputDirectory,
                definition.RelativePath,
                definition.Content,
                definition.PdfTitle,
                definition.PdfAlternateText);
            documentPaths.Add(definition.RelativePath);
        }

        var documents = documentDefinitions
            .Select((definition, index) => new Document(
                $"DOC-{index + 1:0000}",
                1,
                "fixture-generator",
                definition.SourceRecordId,
                Path.GetFileName(definition.RelativePath),
                "application/pdf",
                HashFile(outputDirectory, definition.RelativePath),
                definition.IssuedOn))
            .ToArray();
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
            documents);
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
        var selectedEntries = documents
            .Zip(documentPaths)
            .Select(pair => new ManifestEntry(
                pair.Second,
                pair.First.DocumentId,
                pair.First.Version,
                pair.First.Sha256,
                caseScope))
            .ToArray();
        var boundaries = PathBoundaryDeclaration.Default(
            new SelectedInitialInputManifest(selectedEntries));

        WriteJson(outputDirectory, "application-inputs/reference-data/case-context.json", caseContext);
        WriteJson(outputDirectory, "application-inputs/reference-data/asset.json", asset);
        WriteJson(
            outputDirectory,
            "application-inputs/reference-data/requirements.json",
            requirements);
        WriteJson(
            outputDirectory,
            "application-inputs/reference-data/lessor.json",
            new
            {
                name = baseline.LessorName,
                referenceId = "LESSOR-0001",
                aircraftId = baseline.AircraftId,
                engineId = baseline.EngineId
            });
        WriteJson(
            outputDirectory,
            "application-inputs/reference-data/baseline-facts.json",
            baseline);
        WriteJson(outputDirectory, "application-inputs/package-001/manifest.json", submissionPackage);
        WriteJson(outputDirectory, "replay-only/events.json", new[] { eventEnvelope });
        WriteStagedResponsePackage(
            outputDirectory,
            caseContext,
            scenarioStart,
            baseline);
        if (StringComparer.Ordinal.Equals(configuration.Profile, "isolation"))
        {
            WriteIsolationPackage(outputDirectory, scenarioStart, isolationScope);
        }
        if (mutations.Length > 0)
        {
            WriteJson(
                outputDirectory,
                "evaluator-only/scenario-metadata.json",
                new
                {
                    contractVersion = WorkflowContract.Version,
                    fixtureVersion = configuration.FixtureVersion,
                    profile = configuration.Profile,
                    intendedMutations = mutations,
                    mutationDetails = mutations
                        .Select(CreateMutationMetadata)
                        .ToArray()
                });
        }

        var generatedFiles = documentPaths
            .Concat(
            [
                "application-inputs/reference-data/case-context.json",
                "application-inputs/reference-data/asset.json",
                "application-inputs/reference-data/requirements.json",
                "application-inputs/reference-data/lessor.json",
                "application-inputs/reference-data/baseline-facts.json",
                "application-inputs/package-001/manifest.json",
                "replay-only/events.json",
                "staged-responses/package-002/response.pdf",
                "staged-responses/package-002/manifest.json"
            ])
            .Concat(StringComparer.Ordinal.Equals(configuration.Profile, "isolation")
                ?
                [
                    "evaluator-only/isolation/cross-scope-package/001-aircraft-record.pdf",
                    "evaluator-only/isolation/cross-scope-package/manifest.json"
                ]
                : [])
            .Concat(mutations.Length > 0
                ? ["evaluator-only/scenario-metadata.json"]
                : [])
            .ToArray();
        var receiptArtifacts = CreateReceiptArtifacts(
            outputDirectory,
            generatedFiles,
            caseScope,
            StringComparer.Ordinal.Equals(configuration.Profile, "isolation")
                ? isolationScope
                : null);
        var receipt = ReproducibilityReceiptFactory.Create(
            configuration,
            templateVersion,
            generatedFiles,
            mutations,
            receiptArtifacts);
        var package = new GeneratorContractPackage(
            WorkflowContract.Version,
            configuration,
            caseContext,
            asset,
            requirements,
            documents,
            [submissionPackage],
            [eventEnvelope],
            boundaries,
            receipt)
        {
            Baseline = baseline
        };
        GeneratorContractValidator.ValidateOrThrow(package);
        WriteJson(outputDirectory, "generator-contract.json", package);
        WriteJson(outputDirectory, "reproducibility-receipt.json", receipt);

        var fixture = new GeneratedFixture(package, receipt, generatedFiles);
        BaselineFixtureValidator.ValidateOrThrow(fixture);
        return fixture;
    }

    private static Requirement[] CreateRequirements(
        DateTimeOffset scenarioStart,
        DateOnly plannedReturnDate)
    {
        var requiredTo = new DateTimeOffset(
            plannedReturnDate.ToDateTime(TimeOnly.MinValue),
            TimeSpan.Zero);
        return
        [
            new Requirement(
                "REQ-0001",
                1,
                "COMP-0001",
                "installation-record",
                "Component A installation record",
                "return-checklist",
                "fixture-setup",
                scenarioStart.AddDays(-30),
                "DOC-0002",
                scenarioStart,
                requiredTo),
            new Requirement(
                "REQ-0002",
                1,
                "COMP-0001",
                "removal-history",
                "Component A removal history",
                "return-checklist",
                "fixture-setup",
                scenarioStart.AddDays(-30),
                "DOC-0002",
                scenarioStart,
                requiredTo),
            new Requirement(
                "REQ-0003",
                1,
                "COMP-0002",
                "installation-record",
                "Component B installation record",
                "return-checklist",
                "fixture-setup",
                scenarioStart.AddDays(-30),
                "DOC-0002",
                scenarioStart,
                requiredTo),
            new Requirement(
                "REQ-0004",
                1,
                "COMP-0002",
                "removal-history",
                "Component B removal history",
                "return-checklist",
                "fixture-setup",
                scenarioStart.AddDays(-30),
                "DOC-0002",
                scenarioStart,
                requiredTo)
        ];
    }

    private static BaselineFixture CreateBaseline(
        DateTimeOffset scenarioStart,
        DateOnly plannedReturnDate,
        IReadOnlyList<Requirement> requirements) =>
        new(
            "Altivane Aviation Capital",
            "MOCK-AC-001",
            "MOCK-ENG-001",
            plannedReturnDate,
            [
                new ComponentMovement(
                    "COMP-0001",
                    "SERIAL-0001",
                    "MOCK-AC-001",
                    "installed",
                    scenarioStart.AddDays(-600),
                    100,
                    20),
                new ComponentMovement(
                    "COMP-0001",
                    "SERIAL-0001",
                    "MOCK-AC-001",
                    "removed",
                    scenarioStart.AddDays(-420),
                    650,
                    140),
                new ComponentMovement(
                    "COMP-0001",
                    "SERIAL-0001",
                    "MOCK-AC-001",
                    "installed",
                    scenarioStart.AddDays(-419),
                    651,
                    140),
                new ComponentMovement(
                    "COMP-0002",
                    "SERIAL-0002",
                    "MOCK-AC-001",
                    "installed",
                    scenarioStart.AddDays(-580),
                    80,
                    15),
                new ComponentMovement(
                    "COMP-0002",
                    "SERIAL-0002",
                    "MOCK-AC-001",
                    "removed",
                    scenarioStart.AddDays(-300),
                    900,
                    190),
                new ComponentMovement(
                    "COMP-0002",
                    "SERIAL-0002",
                    "MOCK-AC-001",
                    "installed",
                    scenarioStart.AddDays(-299),
                    901,
                    190)
            ],
            [
                new UsageCounter("COMP-0001", scenarioStart.AddDays(-420), 650, 140),
                new UsageCounter("COMP-0001", scenarioStart, 2200, 400),
                new UsageCounter("COMP-0002", scenarioStart.AddDays(-300), 900, 190),
                new UsageCounter("COMP-0002", scenarioStart, 2100, 380)
            ],
            requirements);

    private static IReadOnlyList<DocumentDefinition> CreateDocumentDefinitions(
        GeneratorConfiguration configuration,
        DateTimeOffset scenarioStart,
        DateOnly plannedReturnDate,
        BaselineFixture baseline,
        IReadOnlySet<string> mutations)
    {
        var date = configuration.ScenarioDate.ToString("yyyy-MM-dd");
        var returnDate = plannedReturnDate.ToString("yyyy-MM-dd");
        var usage = baseline.UsageCounters
            .Where(counter => counter.AsOf == scenarioStart)
            .ToDictionary(counter => counter.ComponentId, StringComparer.Ordinal);
        var definitions = new List<DocumentDefinition>
        {
            new(
                "application-inputs/package-001/001-installed-components.pdf",
                "SOURCE-INVENTORY-0001",
                scenarioStart.AddDays(-1),
                string.Join(
                    "\n",
                    "Altivane Aviation Capital - installed component listing",
                    $"Aircraft: {baseline.AircraftId}",
                    $"Engine: {baseline.EngineId}",
                    "COMP-0001 | SERIAL-0001 | installed",
                    "COMP-0002 | SERIAL-0002 | installed")),
            new(
                "application-inputs/package-001/002-approved-return-checklist.pdf",
                "SOURCE-CHECKLIST-0001",
                scenarioStart.AddDays(-30),
                string.Join(
                    "\n",
                    "Altivane Aviation Capital - approved mock return checklist",
                    "Approval provenance: fixture-setup",
                    $"Applicable return date: {returnDate}",
                    "Requirements: REQ-0001, REQ-0002, REQ-0003, REQ-0004")),
            new(
                "application-inputs/package-001/003-component-a-installation.pdf",
                "SOURCE-INSTALL-0001",
                scenarioStart.AddDays(-419),
                $"Component: COMP-0001\nSerial: SERIAL-0001\nAction: installed\nAircraft: MOCK-AC-001\nInstallation date: {baseline.ComponentMovements.Single(movement => movement.ComponentId == "COMP-0001" && movement.Action == "installed" && movement.OccurredAt == scenarioStart.AddDays(-419)).OccurredAt:yyyy-MM-dd}"),
            new(
                "application-inputs/package-001/004-component-a-removal-history.pdf",
                "SOURCE-REMOVAL-0001",
                scenarioStart.AddDays(-420),
                $"Component: COMP-0001\nSerial: SERIAL-0001\nAction: removed\nAircraft: MOCK-AC-001\nRemoval date: {baseline.ComponentMovements.Single(movement => movement.ComponentId == "COMP-0001" && movement.Action == "removed").OccurredAt:yyyy-MM-dd}\nFlight hours: 650\nCycles: 140"),
            new(
                "application-inputs/package-001/005-component-b-installation.pdf",
                "SOURCE-INSTALL-0002",
                scenarioStart.AddDays(-299),
                $"Component: COMP-0002\nSerial: SERIAL-0002\nAction: installed\nAircraft: MOCK-AC-001\nInstallation date: {baseline.ComponentMovements.Single(movement => movement.ComponentId == "COMP-0002" && movement.Action == "installed" && movement.OccurredAt == scenarioStart.AddDays(-299)).OccurredAt:yyyy-MM-dd}"),
            new(
                "application-inputs/package-001/006-component-b-removal-history.pdf",
                "SOURCE-REMOVAL-0002",
                scenarioStart.AddDays(-300),
                $"Component: COMP-0002\nSerial: SERIAL-0002\nAction: removed\nAircraft: MOCK-AC-001\nRemoval date: {baseline.ComponentMovements.Single(movement => movement.ComponentId == "COMP-0002" && movement.Action == "removed").OccurredAt:yyyy-MM-dd}\nFlight hours: 900\nCycles: 190"),
            new(
                "application-inputs/package-001/007-maintenance-summary.pdf",
                "SOURCE-MAINT-0001",
                scenarioStart.AddDays(-7),
                "Altivane Aviation Capital - maintenance summary\nAircraft: MOCK-AC-001\nAll listed component movements are linked to the aircraft."),
            new(
                "application-inputs/package-001/008-periodic-usage-report.pdf",
                "SOURCE-USAGE-0001",
                scenarioStart,
                string.Join(
                    "\n",
                    "Altivane Aviation Capital - periodic usage report",
                    $"As of: {date}",
                    $"COMP-0001 flight hours: {usage["COMP-0001"].FlightHours}; cycles: {usage["COMP-0001"].Cycles}",
                    $"COMP-0002 flight hours: {usage["COMP-0002"].FlightHours}; cycles: {usage["COMP-0002"].Cycles}")),
            new(
                "application-inputs/package-001/009-return-planning-record.pdf",
                "SOURCE-RETURN-0001",
                scenarioStart.AddDays(-2),
                $"Aircraft: MOCK-AC-001\nLessor: Altivane Aviation Capital\nPlanned return date: {returnDate}\nScenario date: {date}"),
            new(
                "application-inputs/package-001/010-asset-reference-index.pdf",
                "SOURCE-ASSET-0001",
                scenarioStart.AddDays(-1),
                "Reference source: asset-register v1\nAircraft: MOCK-AC-001\nEngine: MOCK-ENG-001\nComponents: COMP-0001, COMP-0002")
        };

        if (mutations.Contains(WorkflowContract.LiveMissingHistoryMutation))
        {
            definitions.RemoveAll(definition =>
                definition.RelativePath.EndsWith(
                    "004-component-a-removal-history.pdf",
                    StringComparison.Ordinal));
        }

        if (mutations.Contains(WorkflowContract.LiveAmbiguousIdentityMutation))
        {
            var index = definitions.FindIndex(definition =>
                definition.RelativePath.EndsWith(
                    "005-component-b-installation.pdf",
                    StringComparison.Ordinal));
            if (index < 0)
            {
                throw new InvalidOperationException("The component B identity scan source document is missing.");
            }

            definitions[index] = new DocumentDefinition(
                "application-inputs/package-001/005-component-b-identity-scan.pdf",
                "SOURCE-SCAN-0002",
                scenarioStart.AddDays(-299),
                string.Join(
                    "\n",
                    "Component B identity scan",
                    "Aircraft: MOCK-AC-001",
                    "Candidate serial: SN-B-2041",
                    "Candidate serial: SN-B-2047",
                    "Scan interpretation: identity unresolved"),
                "Component B identity scan",
                "Component B identity scan: two plausible serial candidates; identity unresolved.");
        }

        return definitions;
    }

    private static object CreateMutationMetadata(string mutationIdentifier) =>
        mutationIdentifier switch
        {
            WorkflowContract.LiveMissingHistoryMutation => new
            {
                identifier = mutationIdentifier,
                componentId = "COMP-0001",
                mutation = "omitted-removal-history-record",
                sourceRecordId = "SOURCE-REMOVAL-0001"
            },
            WorkflowContract.LiveAmbiguousIdentityMutation => new
            {
                identifier = mutationIdentifier,
                componentId = "COMP-0002",
                mutation = "ambiguous-identity-scan",
                candidates = new[] { "SN-B-2041", "SN-B-2047" }
            },
            _ => throw new ArgumentException(
                $"Unsupported live mutation '{mutationIdentifier}'.",
                nameof(mutationIdentifier))
        };

    private static IReadOnlyList<ReceiptArtifact> CreateReceiptArtifacts(
        string outputDirectory,
        IEnumerable<string> generatedFiles,
        ArtifactScope caseScope,
        ArtifactScope? isolationScope)
    {
        const string isolationPrefix = "evaluator-only/isolation/cross-scope-package/";
        return generatedFiles
            .Select(path =>
            {
                var isIsolationArtifact = isolationScope is not null &&
                    path.StartsWith(isolationPrefix, StringComparison.Ordinal);
                var classification = path switch
                {
                    _ when path.StartsWith("application-inputs/", StringComparison.Ordinal) =>
                        WorkflowContract.ApplicationInputArtifactClassification,
                    _ when path.StartsWith("staged-responses/", StringComparison.Ordinal) =>
                        WorkflowContract.StagedResponseArtifactClassification,
                    _ when path.StartsWith("evaluator-only/", StringComparison.Ordinal) =>
                        WorkflowContract.EvaluatorOnlyArtifactClassification,
                    _ when path.StartsWith("replay-only/", StringComparison.Ordinal) =>
                        WorkflowContract.ReplayArtifactClassification,
                    _ => throw new InvalidOperationException(
                        $"Cannot classify generated artifact '{path}'.")
                };
                return new ReceiptArtifact(
                    path,
                    HashFile(outputDirectory, path),
                    classification,
                    isIsolationArtifact ? isolationScope! : caseScope);
            })
            .ToArray();
    }

    private static void WriteIsolationPackage(
        string outputDirectory,
        DateTimeOffset scenarioStart,
        ArtifactScope scope)
    {
        const string evidencePath =
            "evaluator-only/isolation/cross-scope-package/001-aircraft-record.pdf";
        const string manifestPath =
            "evaluator-only/isolation/cross-scope-package/manifest.json";
        WriteRenderedDocument(
            outputDirectory,
            evidencePath,
            string.Join(
                "\n",
                "Fictional Meridian Skies - aircraft return evidence",
                $"Airline: {scope.AirlineId}",
                $"Aircraft: {scope.AircraftId}",
                "Engine: MOCK-ENG-002",
                "Tempting cross-scope evidence; evaluator-only",
                $"Scenario date: {scenarioStart:yyyy-MM-dd}"));

        WriteJson(
            outputDirectory,
            manifestPath,
            new
            {
                contractVersion = WorkflowContract.Version,
                packageId = "PKG-ISOLATION-0001",
                classification = WorkflowContract.EvaluatorOnlyArtifactClassification,
                scope,
                documents = new[]
                {
                    new
                    {
                        documentId = "DOC-ISOLATION-0001",
                        version = 1,
                        fileName = Path.GetFileName(evidencePath),
                        mediaType = "application/pdf",
                        sha256 = HashFile(outputDirectory, evidencePath),
                        issuedOn = scenarioStart.AddDays(-1)
                    }
                }
            });
    }

    private static void WriteStagedResponsePackage(
        string outputDirectory,
        CaseContext caseContext,
        DateTimeOffset scenarioStart,
        BaselineFixture baseline)
    {
        const string responsePath = "staged-responses/package-002/response.pdf";
        var responseContent = string.Join(
            "\n",
            "Altivane Aviation Capital - staged partner response",
            $"Aircraft: {caseContext.AircraftId}",
            "Component: COMP-0001",
            "Removal history record supplied for later request replay",
            $"Record date: {baseline.ComponentMovements.Single(movement => movement.ComponentId == "COMP-0001" && movement.Action == "removed").OccurredAt:yyyy-MM-dd}",
            $"Scenario date: {scenarioStart:yyyy-MM-dd}");
        WriteRenderedDocument(outputDirectory, responsePath, responseContent);
        var responseHash = HashFile(outputDirectory, responsePath);
        var responseDocument = new Document(
            "DOC-RESP-0001",
            1,
            "mock-partner",
            "PARTNER-RESP-0001",
            "response.pdf",
            "application/pdf",
            responseHash,
            scenarioStart.AddDays(1));
        var responsePackage = new SubmissionPackage(
            WorkflowContract.Version,
            "PKG-0002",
            caseContext.RunId,
            caseContext.CaseId,
            caseContext.AirlineId,
            caseContext.AircraftId,
            caseContext.LeaseId,
            scenarioStart.AddDays(1),
            scenarioStart.AddDays(1),
            [responseDocument]);
        WriteJson(
            outputDirectory,
            "staged-responses/package-002/manifest.json",
            responsePackage);
    }

    private sealed record DocumentDefinition(
        string RelativePath,
        string SourceRecordId,
        DateTimeOffset IssuedOn,
        string Content,
        string? PdfTitle = null,
        string? PdfAlternateText = null);

    private static void WriteJson(string root, string relativePath, object value) =>
        WriteText(root, relativePath, JsonSerializer.Serialize(value, GeneratorContractJson.Options));

    private static void WriteText(string root, string relativePath, string content)
    {
        var fullPath = ResolvePath(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static void DeleteIfPresent(string root, string relativePath)
    {
        var fullPath = ResolvePath(root, relativePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    private static void WriteRenderedDocument(
        string root,
        string relativePath,
        string content,
        string? pdfTitle = null,
        string? pdfAlternateText = null)
    {
        var lines = content.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Select(EscapePdfString);
        var stream = new StringBuilder("BT\n/F1 10 Tf\n50 780 Td\n");
        foreach (var line in lines)
        {
            stream.Append('(').Append(line).Append(") Tj\n0 -14 Td\n");
        }

        stream.Append("ET");
        var pageAlternateText = pdfAlternateText is null
            ? null
            : $"/Alt ({EscapePdfString(pdfAlternateText)}) ";
        var metadata = new StringBuilder("<< /Producer (airline-demo fixture generator)");
        if (!string.IsNullOrWhiteSpace(pdfTitle))
        {
            metadata.Append(" /Title (").Append(EscapePdfString(pdfTitle)).Append(')');
        }

        if (!string.IsNullOrWhiteSpace(pdfAlternateText))
        {
            metadata.Append(" /Subject (").Append(EscapePdfString(pdfAlternateText)).Append(')');
        }

        metadata.Append(" >>");
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {pageAlternateText}/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream.ToString())} >>\nstream\n{stream}\nendstream",
            metadata.ToString()
        };
        var document = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        foreach (var (objectValue, index) in objects.Select((value, index) => (value, index)))
        {
            offsets.Add(Encoding.ASCII.GetByteCount(document.ToString()));
            document.Append(index + 1).Append(" 0 obj\n")
                .Append(objectValue)
                .Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(document.ToString());
        document.Append("xref\n0 ").Append(objects.Length + 1).Append('\n')
            .Append("0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            document.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        document.Append("trailer\n<< /Size ").Append(objects.Length + 1)
            .Append(" /Root 1 0 R /Info 6 0 R >>\nstartxref\n")
            .Append(xrefOffset)
            .Append("\n%%EOF\n");
        var fullPath = ResolvePath(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, Encoding.ASCII.GetBytes(document.ToString()));
    }

    private static string EscapePdfString(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

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
