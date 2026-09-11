using System.Text.Json;
using System.Security.Cryptography;
using AirlineDemo.Generator;

namespace AirlineDemo.Tests;

public sealed class GeneratorContractTests
{
    [Fact]
    public void GeneratorUsesEmbeddedSharedWorkflowAndOpenApiArtifacts()
    {
        using var schema = JsonDocument.Parse(SharedWorkflowArtifacts.WorkflowSchemaJson);
        using var openApi = JsonDocument.Parse(SharedWorkflowArtifacts.OpenApiJson);

        Assert.Equal("1.0", WorkflowContract.Version);
        Assert.Contains("package.submitted", WorkflowContract.EventTypes);
        Assert.Contains("partner.response.received", WorkflowContract.EventTypes);
        Assert.True(schema.RootElement.GetProperty("$defs").TryGetProperty("Asset", out _));
        Assert.True(schema.RootElement.GetProperty("$defs").TryGetProperty("Requirement", out _));
        Assert.True(openApi.RootElement.GetProperty("paths").TryGetProperty("/packages", out _));
    }

    [Fact]
    public void ContractProjectionMapsPrdObjectsAndEventEnvelopeFields()
    {
        var fixture = CreateFixture();
        var json = GeneratorContractJson.Serialize(fixture);
        using var document = JsonDocument.Parse(json);

        Assert.Equal("1.0", document.RootElement.GetProperty("contractVersion").GetString());
        Assert.Equal("CASE-0001", document.RootElement.GetProperty("case").GetProperty("caseId").GetString());
        Assert.Equal("ENG-0001", document.RootElement.GetProperty("asset").GetProperty("engineId").GetString());
        Assert.Equal(
            "COMP-0001",
            document.RootElement.GetProperty("asset").GetProperty("components")[0]
                .GetProperty("componentId").GetString());
        Assert.Equal(
            "REQ-0001",
            document.RootElement.GetProperty("requirements")[0].GetProperty("requirementId").GetString());
        Assert.Equal(
            "fixture-setup",
            document.RootElement.GetProperty("requirements")[0].GetProperty("approvedBy").GetString());
        Assert.Equal(
            "DOC-0001",
            document.RootElement.GetProperty("documents")[0].GetProperty("documentId").GetString());
        Assert.Equal(
            "PKG-0001",
            document.RootElement.GetProperty("submissionPackages")[0].GetProperty("packageId").GetString());
        Assert.Equal(
            "package.submitted",
            document.RootElement.GetProperty("events")[0].GetProperty("type").GetString());
        Assert.True(GeneratorContractValidator.Validate(fixture).IsValid);
    }

    [Theory]
    [InlineData("0.9")]
    [InlineData("2.0")]
    public void ContractValidationRejectsUnsupportedSchemaVersion(string unsupportedVersion)
    {
        var fixture = CreateFixture() with
        {
            SubmissionPackages =
            [
                CreatePackage(schemaVersion: unsupportedVersion)
            ]
        };

        var result = GeneratorContractValidator.Validate(fixture);

        Assert.Contains(result.Errors, error => error.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ContractValidationRejectsUnknownClosedEnumValue()
    {
        var fixture = CreateFixture() with
        {
            Events =
            [
                CreateEvent(type: "package.unknown")
            ]
        };

        var result = GeneratorContractValidator.Validate(fixture);

        Assert.Contains(result.Errors, error => error.Contains("closed value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ContractValidationRejectsManifestEntryWithoutSha256()
    {
        var fixture = CreateFixture() with
        {
            PathBoundaries = PathBoundaryDeclaration.Default(
                new SelectedInitialInputManifest(
                [
                    new ManifestEntry(
                        "application-inputs/reference-data/asset.json",
                        "DOC-0001",
                        1,
                        string.Empty)
                ]))
        };

        var result = GeneratorContractValidator.Validate(fixture);

        Assert.Contains(result.Errors, error => error.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ContractValidationRejectsInconsistentCaseScopeIds()
    {
        var fixture = CreateFixture() with
        {
            Events =
            [
                CreateEvent(caseId: "CASE-OTHER")
            ]
        };

        var result = GeneratorContractValidator.Validate(fixture);

        Assert.Contains(result.Errors, error => error.Contains("scope IDs", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("staged-responses/package-002/response.pdf")]
    [InlineData("evaluator-only/expected-outcomes.json")]
    [InlineData("replay-only/events.json")]
    public void InitialApplicationManifestCannotContainProtectedBoundaryPaths(string protectedPath)
    {
        var fixture = CreateFixture() with
        {
            PathBoundaries = PathBoundaryDeclaration.Default(
                new SelectedInitialInputManifest(
                [
                    new ManifestEntry(
                        protectedPath,
                        "DOC-0001",
                        1,
                        Hash)
                ]))
        };

        var result = GeneratorContractValidator.Validate(fixture);

        Assert.Contains(result.Errors, error =>
            error.Contains("protected generator boundary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReceiptContainsReproducibilityValuesHashesMutationsAndVersions()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var filePath = Path.Combine(outputDirectory, "application-inputs", "package-001", "records.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, "fixture content");
            var configuration = CreateConfiguration(outputDirectory);

            var receipt = ReproducibilityReceiptFactory.Create(
                configuration,
                "template-2026-09-10",
                ["application-inputs/package-001/records.txt"],
                ["live.missing-history"]);

            Assert.Equal("1.0", receipt.ContractVersion);
            Assert.Equal("template-2026-09-10", receipt.TemplateVersion);
            Assert.Equal(configuration.Seed, receipt.Seed);
            Assert.Equal(configuration.FixtureVersion, receipt.FixtureVersion);
            Assert.Equal(configuration.ScenarioDate, receipt.ScenarioDate);
            Assert.Equal(configuration.RunId, receipt.RunId);
            Assert.Equal(configuration.Profile, receipt.Profile);
            var expectedHash = Convert.ToHexString(
                SHA256.HashData("fixture content"u8.ToArray()));
            Assert.Equal(expectedHash, receipt.GeneratedFiles.Single().Sha256);
            Assert.Equal("live.missing-history", receipt.IntendedMutationIdentifiers.Single());
            Assert.True(ReproducibilityReceiptValidator.ValidateFiles(receipt, outputDirectory).IsValid);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ReceiptValidationRejectsAHashThatDoesNotMatchItsGeneratedFile()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var filePath = Path.Combine(outputDirectory, "application-inputs", "asset.json");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, "original");
            var receipt = ReproducibilityReceiptFactory.Create(
                CreateConfiguration(outputDirectory),
                "template-1",
                ["application-inputs/asset.json"],
                []);
            await File.WriteAllTextAsync(filePath, "changed");

            var result = ReproducibilityReceiptValidator.ValidateFiles(receipt, outputDirectory);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error =>
                error.Contains("does not match", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void GenerationEmitsACompleteReceiptForFixedConfigurationAndDependencies()
    {
        var firstDirectory = CreateTemporaryDirectory();
        var secondDirectory = CreateTemporaryDirectory();
        try
        {
            var first = FixtureGenerator.Generate(
                CreateConfiguration(firstDirectory) with { Profile = "live" },
                "template-2026-09-10",
                ["live.missing-history"]);
            var second = FixtureGenerator.Generate(
                CreateConfiguration(secondDirectory) with { Profile = "live" },
                "template-2026-09-10",
                ["live.missing-history"]);

            Assert.Equal(
                GeneratorContractJson.Serialize(first.ContractPackage),
                GeneratorContractJson.Serialize(second.ContractPackage)
                    .Replace(secondDirectory, firstDirectory, StringComparison.Ordinal));
            Assert.Equal(first.Receipt.ContractVersion, second.Receipt.ContractVersion);
            Assert.Equal(first.Receipt.TemplateVersion, second.Receipt.TemplateVersion);
            Assert.Equal(first.Receipt.Seed, second.Receipt.Seed);
            Assert.Equal(first.Receipt.FixtureVersion, second.Receipt.FixtureVersion);
            Assert.Equal(first.Receipt.ScenarioDate, second.Receipt.ScenarioDate);
            Assert.Equal(first.Receipt.RunId, second.Receipt.RunId);
            Assert.Equal(first.Receipt.Profile, second.Receipt.Profile);
            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["dotnet"] = "10.0.401",
                    ["generator"] = "1.0.0"
                },
                first.Receipt.BuildDependencies);
            Assert.Equal("template-2026-09-10", first.Receipt.TemplateVersion);
            Assert.Equal(first.Receipt.BuildDependencies, second.Receipt.BuildDependencies);
            Assert.Equal(
                first.Receipt.GeneratedFiles.Select(file => (file.RelativePath, file.Sha256)),
                second.Receipt.GeneratedFiles.Select(file => (file.RelativePath, file.Sha256)));
            Assert.Equal(
                first.Receipt.IntendedMutationIdentifiers,
                second.Receipt.IntendedMutationIdentifiers);
            Assert.Equal(["live.missing-history"], first.Receipt.IntendedMutationIdentifiers);
            Assert.Equal(
                first.GeneratedFiles.OrderBy(path => path, StringComparer.Ordinal),
                first.Receipt.GeneratedFiles.Select(file => file.RelativePath)
                    .OrderBy(path => path, StringComparer.Ordinal));
            Assert.True(ReproducibilityReceiptValidator
                .ValidateFiles(first.Receipt, firstDirectory).IsValid);
            Assert.True(File.Exists(Path.Combine(firstDirectory, "generator-contract.json")));
            Assert.True(File.Exists(Path.Combine(firstDirectory, "reproducibility-receipt.json")));
        }
        finally
        {
            Directory.Delete(firstDirectory, recursive: true);
            Directory.Delete(secondDirectory, recursive: true);
        }
    }

    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static GeneratorContractPackage CreateFixture()
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "airlinedemo-generator-tests",
            Guid.NewGuid().ToString("N"));
        var configuration = CreateConfiguration(outputDirectory);
        var context = new CaseContext("RUN-0001", "CASE-0001", "AIRLINE-0001", "AC-0001", "LEASE-0001");
        var document = CreateDocument();
        var package = CreatePackage();
        var receipt = new ReproducibilityReceipt(
            "1.0",
            "template-1",
            configuration.Seed,
            configuration.FixtureVersion,
            configuration.ScenarioDate,
            configuration.RunId,
            configuration.Profile,
            configuration.BuildDependencies,
            [],
            []);

        return new GeneratorContractPackage(
            "1.0",
            configuration,
            context,
            new Asset(
                "AC-0001",
                "ENG-0001",
                [new AssetComponent("COMP-0001", "SERIAL-0001")],
                new AssetReference("asset-register", 1)),
            [
                new Requirement(
                    "REQ-0001",
                    1,
                    "COMP-0001",
                    "maintenance-record",
                    "Installation record",
                    "required",
                    "fixture-setup",
                    DateTimeOffset.Parse("2026-09-10T08:00:00Z"),
                    "CHECKLIST-0001")
            ],
            [document],
            [package],
            [CreateEvent()],
            PathBoundaryDeclaration.Default(
                new SelectedInitialInputManifest(
                [
                    new ManifestEntry(
                        "application-inputs/package-001/maintenance.pdf",
                        document.DocumentId,
                        document.Version,
                        Hash)
                ])),
            receipt);
    }

    private static GeneratorConfiguration CreateConfiguration(string outputDirectory) =>
        new(
            42,
            "fixture-1",
            new DateOnly(2026, 9, 10),
            "RUN-0001",
            "baseline",
            outputDirectory,
            new Dictionary<string, string>
            {
                ["dotnet"] = "10.0.401",
                ["generator"] = "1.0.0"
            });

    private static Document CreateDocument() =>
        new(
            "DOC-0001",
            1,
            "fixture-generator",
            "SOURCE-0001",
            "maintenance.pdf",
            "application/pdf",
            Hash,
            DateTimeOffset.Parse("2026-09-10T07:00:00Z"));

    private static SubmissionPackage CreatePackage(string schemaVersion = "1.0") =>
        new(
            schemaVersion,
            "PKG-0001",
            "RUN-0001",
            "CASE-0001",
            "AIRLINE-0001",
            "AC-0001",
            "LEASE-0001",
            DateTimeOffset.Parse("2026-09-10T13:00:00Z"),
            DateTimeOffset.Parse("2026-09-10T09:00:00Z"),
            [CreateDocument()]);

    private static EventEnvelope CreateEvent(
        string type = "package.submitted",
        string caseId = "CASE-0001")
    {
        using var payload = JsonDocument.Parse("""{"packageId":"PKG-0001"}""");
        return new EventEnvelope(
            "1.0",
            "EVT-0001",
            type,
            "RUN-0001",
            caseId,
            "AIRLINE-0001",
            "AC-0001",
            "LEASE-0001",
            DateTimeOffset.Parse("2026-09-10T13:00:00Z"),
            DateTimeOffset.Parse("2026-09-10T09:00:00Z"),
            "CORR-0001",
            payload.RootElement.Clone());
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "airlinedemo-generator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
