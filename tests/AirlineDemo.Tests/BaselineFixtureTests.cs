using System.Security.Cryptography;
using System.Text.Json;
using AirlineDemo.Generator;

namespace AirlineDemo.Tests;

public sealed class BaselineFixtureTests
{
    [Fact]
    public void BaselineGenerationIsDeterministicAndReceiptHashesEveryGeneratedInput()
    {
        var firstDirectory = CreateTemporaryDirectory();
        var secondDirectory = CreateTemporaryDirectory();
        try
        {
            var first = FixtureGenerator.Generate(CreateConfiguration(firstDirectory), "template-baseline-1");
            var second = FixtureGenerator.Generate(CreateConfiguration(secondDirectory), "template-baseline-1");

            Assert.Equal(
                first.Receipt.GeneratedFiles.Select(file => (file.RelativePath, file.Sha256)),
                second.Receipt.GeneratedFiles.Select(file => (file.RelativePath, file.Sha256)));
            Assert.Empty(first.Receipt.IntendedMutationIdentifiers);
            Assert.Equal(
                first.GeneratedFiles.OrderBy(path => path, StringComparer.Ordinal),
                first.Receipt.GeneratedFiles.Select(file => file.RelativePath)
                    .OrderBy(path => path, StringComparer.Ordinal));
            Assert.All(first.Receipt.GeneratedFiles, file =>
            {
                var path = Path.Combine(
                    firstDirectory,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path));
                Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            });
        }
        finally
        {
            Directory.Delete(firstDirectory, recursive: true);
            Directory.Delete(secondDirectory, recursive: true);
        }
    }

    [Fact]
    public void BaselineContainsAltivaneReferenceDataAndTenManifestDocuments()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(CreateConfiguration(outputDirectory), "template-baseline-1");
            Assert.NotNull(fixture.Baseline);
            var baseline = fixture.Baseline!;
            Assert.Equal("Altivane Aviation Capital", baseline.LessorName);
            Assert.Equal("MOCK-AC-001", baseline.AircraftId);
            Assert.Equal("MOCK-ENG-001", baseline.EngineId);
            Assert.Equal(
                ["COMP-0001", "COMP-0002"],
                fixture.ContractPackage.Asset.Components.Select(component => component.ComponentId));
            Assert.Equal(4, baseline.ApprovedRequirements.Count);
            Assert.Equal(10, fixture.ContractPackage.SubmissionPackages.Single().Manifest.Count);
            Assert.True(BaselineFixtureValidator.Validate(fixture).IsValid);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void BaselineValidationRejectsRelationshipChronologyReferencesAndCounters()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(CreateConfiguration(outputDirectory), "template-baseline-1");
            Assert.NotNull(fixture.Baseline);
            var baseline = fixture.Baseline!;

            var inconsistent = fixture.ContractPackage with
            {
                Baseline = baseline with
                {
                    ComponentMovements =
                    [
                        baseline.ComponentMovements[0] with
                        {
                            ComponentId = "COMP-UNKNOWN",
                            OccurredAt = baseline.ComponentMovements[1].OccurredAt.AddDays(1)
                        },
                        ..baseline.ComponentMovements.Skip(1)
                    ],
                    UsageCounters =
                    [
                        baseline.UsageCounters[0] with { FlightHours = -1 },
                        ..baseline.UsageCounters.Skip(1)
                    ]
                },
                SubmissionPackages =
                [
                    fixture.ContractPackage.SubmissionPackages.Single() with
                    {
                        Manifest =
                        [
                            fixture.ContractPackage.SubmissionPackages.Single().Manifest[0] with
                            {
                                DocumentId = "DOC-UNKNOWN"
                            },
                            ..fixture.ContractPackage.SubmissionPackages.Single().Manifest.Skip(1)
                        ]
                    }
                ]
            };

            var result = GeneratorContractValidator.Validate(inconsistent);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("relationship", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Errors, error => error.Contains("usage", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Errors, error => error.Contains("unresolved", StringComparison.OrdinalIgnoreCase));

            var chronological = fixture.ContractPackage with
            {
                Baseline = baseline with
                {
                    ComponentMovements =
                    [
                        baseline.ComponentMovements[0],
                        baseline.ComponentMovements[1] with
                        {
                            OccurredAt = baseline.ComponentMovements[0].OccurredAt.AddMinutes(-1)
                        },
                        ..baseline.ComponentMovements.Skip(2)
                    ]
                }
            };
            var chronologyResult = GeneratorContractValidator.Validate(chronological);
            Assert.Contains(chronologyResult.Errors, error =>
                error.Contains("chronology", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void BaselineManifestHashMismatchIsRejected()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(CreateConfiguration(outputDirectory), "template-baseline-1");
            var entry = fixture.ContractPackage.PathBoundaries.SelectedInitialInputManifest.Entries[0];
            var path = Path.Combine(
                outputDirectory,
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            File.AppendAllText(path, "\nchanged");

            var result = BaselineFixtureValidator.Validate(fixture);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("hash", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void BaselineHasNoGeneratedFindingsPolicyDecisionsOrApprovalsAndStaysInOutputRoot()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(CreateConfiguration(outputDirectory), "template-baseline-1");
            var outputFiles = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(outputDirectory, path).Replace('\\', '/'))
                .ToArray();
            var outputRoot = Path.GetFullPath(outputDirectory);
            var outputRootWithSeparator = outputRoot + Path.DirectorySeparatorChar;

            Assert.DoesNotContain(outputFiles, path =>
                path.Contains("finding", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("policy", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("approval", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(outputFiles, path =>
                path.StartsWith("evaluator-only/", StringComparison.Ordinal));
            Assert.DoesNotContain(
                fixture.GeneratedFiles,
                path => Path.IsPathRooted(path) || path.Split('/').Contains(".."));
            Assert.DoesNotContain(
                fixture.Receipt.GeneratedFiles,
                file => file.RelativePath.Contains("evaluator-only", StringComparison.Ordinal));
            Assert.All(
                outputFiles,
                relativePath => Assert.StartsWith(
                    outputRootWithSeparator,
                    Path.GetFullPath(Path.Combine(
                        outputRoot,
                        relativePath.Replace('/', Path.DirectorySeparatorChar)))));

            var contract = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(outputDirectory, "generator-contract.json")));
            Assert.False(contract.RootElement.TryGetProperty("findings", out _));
            Assert.False(contract.RootElement.TryGetProperty("policyDecisions", out _));
            Assert.False(contract.RootElement.TryGetProperty("approvals", out _));
            Assert.False(contract.RootElement.TryGetProperty("intendedMutations", out _));
            Assert.Empty(
                contract.RootElement.GetProperty("receipt")
                    .GetProperty("intendedMutationIdentifiers")
                    .EnumerateArray());

            var events = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(outputDirectory, "replay-only", "events.json")));
            foreach (var envelope in events.RootElement.EnumerateArray())
            {
                Assert.False(envelope.TryGetProperty("finding", out _));
                Assert.False(envelope.TryGetProperty("policyDecision", out _));
                Assert.False(envelope.TryGetProperty("approval", out _));
                var payload = envelope.GetProperty("payload");
                Assert.False(payload.TryGetProperty("finding", out _));
                Assert.False(payload.TryGetProperty("policyDecision", out _));
                Assert.False(payload.TryGetProperty("approval", out _));
            }

            Assert.True(BaselineFixtureValidator.Validate(fixture).IsValid);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static GeneratorConfiguration CreateConfiguration(string outputDirectory) =>
        new(
            42,
            "baseline-fixture-1",
            new DateOnly(2026, 9, 10),
            "RUN-0001",
            "baseline",
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
            "airlinedemo-baseline-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
