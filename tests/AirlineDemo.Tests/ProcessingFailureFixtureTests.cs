using System.Security.Cryptography;
using System.Text.Json;
using AirlineDemo.Generator;

namespace AirlineDemo.Tests;

public sealed class ProcessingFailureFixtureTests
{
    [Fact]
    public void ProcessingFailureEmitsADeclaredCorruptDocumentAndEvaluatorMetadata()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-processing-failure-1");
            var corruptDocument = fixture.ContractPackage.Documents.Single(document =>
                document.SourceRecordId == "SOURCE-CORRUPT-0001");
            var manifestDocument = fixture.ContractPackage.SubmissionPackages
                .Single()
                .Manifest
                .Single(document => document.DocumentId == corruptDocument.DocumentId);
            var corruptPath = Path.Combine(
                outputDirectory,
                "application-inputs",
                "package-001",
                "011-corrupt-document.pdf");
            var corruptBytes = File.ReadAllBytes(corruptPath);

            Assert.Equal(
                [WorkflowContract.ProcessingFailureCorruptDocumentMutation],
                fixture.Receipt.IntendedMutationIdentifiers);
            Assert.Equal(corruptDocument.Sha256, manifestDocument.Sha256);
            Assert.DoesNotContain("%PDF-", System.Text.Encoding.ASCII.GetString(corruptBytes));
            Assert.Equal(
                corruptDocument.Sha256,
                Convert.ToHexString(SHA256.HashData(corruptBytes)));
            Assert.Contains(
                fixture.ContractPackage.PathBoundaries.SelectedInitialInputManifest.Entries,
                entry => entry.DocumentId == corruptDocument.DocumentId &&
                         entry.Sha256 == corruptDocument.Sha256 &&
                         entry.RelativePath == "application-inputs/package-001/011-corrupt-document.pdf");

            using var metadata = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(outputDirectory, "evaluator-only", "scenario-metadata.json")));
            var detail = metadata.RootElement
                .GetProperty("mutationDetails")
                .EnumerateArray()
                .Single();
            Assert.Contains(
                WorkflowContract.ProcessingFailureCorruptDocumentMutation,
                metadata.RootElement.GetProperty("intendedMutations")
                    .EnumerateArray()
                    .Select(value => value.GetString()));
            Assert.Equal(corruptDocument.DocumentId, detail.GetProperty("documentId").GetString());
            Assert.Equal(corruptDocument.Sha256, detail.GetProperty("sha256").GetString());
            Assert.Equal(
                "application-inputs/package-001/011-corrupt-document.pdf",
                detail.GetProperty("relativePath").GetString());
            Assert.True(BaselineFixtureValidator.Validate(fixture).IsValid);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void ProcessingFailureValidationRejectsAnUndeclaredManifestDocument()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-processing-failure-1");
            var package = fixture.ContractPackage.SubmissionPackages.Single();
            var corruptDocument = package.Manifest.Single(document =>
                document.SourceRecordId == "SOURCE-CORRUPT-0001");
            var invalid = fixture with
            {
                ContractPackage = fixture.ContractPackage with
                {
                    SubmissionPackages =
                    [
                        package with
                        {
                            Manifest = package.Manifest
                                .Where(document => document.DocumentId != corruptDocument.DocumentId)
                                .ToArray()
                        }
                    ]
                }
            };

            var result = BaselineFixtureValidator.Validate(invalid);

            Assert.False(result.IsValid);
            Assert.Contains(
                result.Errors,
                error => error.Contains(
                    "absent from the submitted manifest",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void ProcessingFailureValidationRejectsAHashThatDiffersFromTheRenderedArtifact()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-processing-failure-1");
            var corruptPath = Path.Combine(
                outputDirectory,
                "application-inputs",
                "package-001",
                "011-corrupt-document.pdf");
            File.AppendAllText(corruptPath, "tampered");

            var result = BaselineFixtureValidator.Validate(fixture);

            Assert.False(result.IsValid);
            Assert.Contains(
                result.Errors,
                error => error.Contains(
                    "does not match the rendered artifact",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void ProcessingFailureValidationRequiresTheEvaluatorOnlyMutationDeclaration()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-processing-failure-1");
            var metadataPath = Path.Combine(
                outputDirectory,
                "evaluator-only",
                "scenario-metadata.json");
            var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var withoutMutation = new
            {
                contractVersion = metadata.RootElement.GetProperty("contractVersion").GetString(),
                fixtureVersion = metadata.RootElement.GetProperty("fixtureVersion").GetString(),
                profile = metadata.RootElement.GetProperty("profile").GetString(),
                intendedMutations = Array.Empty<string>(),
                mutationDetails = Array.Empty<object>()
            };
            File.WriteAllText(
                metadataPath,
                JsonSerializer.Serialize(withoutMutation, GeneratorContractJson.Options));

            var result = BaselineFixtureValidator.Validate(fixture);

            Assert.False(result.IsValid);
            Assert.Contains(
                result.Errors,
                error => error.Contains(
                    "not declared in evaluator-only metadata",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void ProcessingFailureApplicationArtifactsContainNoWorkflowOutcomes()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-processing-failure-1");

            foreach (var boundary in new[] { "application-inputs", "staged-responses", "replay-only" })
            {
                foreach (var path in Directory.EnumerateFiles(
                             Path.Combine(outputDirectory, boundary),
                             "*",
                             SearchOption.AllDirectories))
                {
                    var content = File.ReadAllText(path);
                    Assert.DoesNotContain("\"finding\"", content, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("\"policyDecision\"", content, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("\"reviewDecision\"", content, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("\"approvalCommand\"", content, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static GeneratorConfiguration CreateConfiguration(string outputDirectory) =>
        new(
            42,
            "processing-failure-fixture-1",
            new DateOnly(2026, 9, 10),
            "RUN-0001",
            "processing-failure",
            outputDirectory,
            new Dictionary<string, string>
            {
                ["dotnet"] = "10.0.401",
                ["generator"] = "1.0.0"
            });

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "airlinedemo-processing-failure-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
