using System.Security.Cryptography;
using AirlineDemo.Generator;

namespace AirlineDemo.Tests;

public sealed class IsolationFixtureTests
{
    [Fact]
    public void IsolationGenerationIsDeterministicAndRecordsClassifiedCrossScopeArtifacts()
    {
        var firstDirectory = CreateTemporaryDirectory();
        var secondDirectory = CreateTemporaryDirectory();
        try
        {
            var first = FixtureGenerator.Generate(CreateConfiguration(firstDirectory), "template-isolation-1");
            var second = FixtureGenerator.Generate(CreateConfiguration(secondDirectory), "template-isolation-1");

            Assert.Equal(
                GeneratorContractJson.Serialize(first.ContractPackage),
                GeneratorContractJson.Serialize(second.ContractPackage)
                    .Replace(secondDirectory, firstDirectory, StringComparison.Ordinal));

            var firstEvidence = first.Receipt.ArtifactEvidence!;
            var secondEvidence = second.Receipt.ArtifactEvidence!;
            Assert.Equal(
                firstEvidence.Select(ArtifactIdentity),
                secondEvidence.Select(ArtifactIdentity));

            var crossScopeEvidence = firstEvidence
                .Where(artifact => artifact.Scope.AirlineId == "AIRLINE-0002")
                .ToArray();
            Assert.Equal(2, crossScopeEvidence.Length);
            Assert.All(
                crossScopeEvidence,
                artifact =>
                {
                    Assert.Equal("evaluator-only", artifact.Classification);
                    Assert.StartsWith(
                        "evaluator-only/isolation/cross-scope-package/",
                        artifact.RelativePath,
                        StringComparison.Ordinal);
                    Assert.Matches("^[A-F0-9]{64}$", artifact.Sha256);
                    Assert.Equal("RUN-0001", artifact.Scope.RunId);
                    Assert.Equal("CASE-0002", artifact.Scope.CaseId);
                    Assert.Equal("MOCK-AC-002", artifact.Scope.AircraftId);
                    var path = Path.Combine(
                        firstDirectory,
                        artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    Assert.Equal(
                        artifact.Sha256,
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
                });

            Assert.All(
                first.ContractPackage.PathBoundaries.SelectedInitialInputManifest.Entries,
                entry =>
                {
                    Assert.StartsWith("application-inputs/", entry.RelativePath);
                    Assert.Equal("CASE-RUN-0001", entry.Scope!.CaseId);
                    Assert.NotEqual("AIRLINE-0002", entry.Scope.AirlineId);
                });
            Assert.True(ReproducibilityReceiptValidator
                .ValidateFiles(first.Receipt, firstDirectory)
                .IsValid);
        }
        finally
        {
            Directory.Delete(firstDirectory, recursive: true);
            Directory.Delete(secondDirectory, recursive: true);
        }
    }

    [Fact]
    public void IsolationValidationRejectsASelectedInputWithDifferentScope()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-isolation-1");
            var crossScopeArtifact = fixture.Receipt.ArtifactEvidence!
                .Single(artifact =>
                    artifact.Scope.AirlineId == "AIRLINE-0002" &&
                    artifact.RelativePath.EndsWith(".pdf", StringComparison.Ordinal));
            var invalidManifest = fixture.ContractPackage.PathBoundaries
                .SelectedInitialInputManifest.Entries
                .Append(new ManifestEntry(
                    crossScopeArtifact.RelativePath,
                    "DOC-ISOLATION-0001",
                    1,
                    crossScopeArtifact.Sha256,
                    crossScopeArtifact.Scope))
                .ToArray();
            var invalidPackage = fixture.ContractPackage with
            {
                PathBoundaries = fixture.ContractPackage.PathBoundaries with
                {
                    SelectedInitialInputManifest = new SelectedInitialInputManifest(invalidManifest)
                }
            };

            var result = GeneratorContractValidator.Validate(invalidPackage);

            Assert.False(result.IsValid);
            Assert.Contains(
                result.Errors,
                error => error.Contains("selected initial case", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void IsolationValidationRejectsMisplacedOrUnclassifiedCrossScopeArtifacts()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-isolation-1");
            var evidence = fixture.Receipt.ArtifactEvidence!;
            var misplaced = evidence
                .Select(artifact => artifact.Scope.AirlineId == "AIRLINE-0002"
                    ? artifact with
                    {
                        RelativePath = artifact.RelativePath.Replace(
                            "evaluator-only/",
                            "application-inputs/",
                            StringComparison.Ordinal)
                    }
                    : artifact)
                .ToArray();
            var misplacedResult = BaselineFixtureValidator.Validate(
                fixture with
                {
                    Receipt = fixture.Receipt with { ArtifactEvidence = misplaced },
                    ContractPackage = fixture.ContractPackage with
                    {
                        Receipt = fixture.ContractPackage.Receipt with
                        {
                            ArtifactEvidence = misplaced
                        }
                    }
                });

            Assert.False(misplacedResult.IsValid);
            Assert.Contains(
                misplacedResult.Errors,
                error => error.Contains("must be evaluator-only", StringComparison.OrdinalIgnoreCase));

            var withoutClassification = evidence
                .Where(artifact => artifact.Scope.AirlineId != "AIRLINE-0002")
                .ToArray();
            var omittedResult = BaselineFixtureValidator.Validate(
                fixture with
                {
                    Receipt = fixture.Receipt with
                    {
                        ArtifactEvidence = withoutClassification
                    },
                    ContractPackage = fixture.ContractPackage with
                    {
                        Receipt = fixture.ContractPackage.Receipt with
                        {
                            ArtifactEvidence = withoutClassification
                        }
                    }
                });

            Assert.False(omittedResult.IsValid);
            Assert.Contains(
                omittedResult.Errors,
                error => error.Contains(
                    "omitted from evaluator-only artifact classification",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static object ArtifactIdentity(ReceiptArtifact artifact) =>
        new
        {
            artifact.RelativePath,
            artifact.Sha256,
            artifact.Classification,
            artifact.Scope
        };

    private static GeneratorConfiguration CreateConfiguration(string outputDirectory) =>
        new(
            42,
            "isolation-fixture-1",
            new DateOnly(2026, 9, 10),
            "RUN-0001",
            "isolation",
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
            "airlinedemo-isolation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
