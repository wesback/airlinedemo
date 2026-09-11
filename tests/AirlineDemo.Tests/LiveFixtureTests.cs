using System.Text.Json;
using AirlineDemo.Generator;

namespace AirlineDemo.Tests;

public sealed class LiveFixtureTests
{
    [Fact]
    public void LiveProfileOmitsOnlyTheDeclaredComponentARemovalRecord()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-2026-09-10");

            Assert.Equal(
                [
                    WorkflowContract.LiveAmbiguousIdentityMutation,
                    WorkflowContract.LiveMissingHistoryMutation
                ],
                fixture.Receipt.IntendedMutationIdentifiers);
            Assert.DoesNotContain(
                fixture.ContractPackage.Documents,
                document => document.SourceRecordId == "SOURCE-REMOVAL-0001");
            Assert.Contains(
                fixture.Baseline!.ComponentMovements,
                movement => movement.ComponentId == "COMP-0001" && movement.Action == "removed");
            using var metadata = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(outputDirectory, "evaluator-only", "scenario-metadata.json")));
            Assert.Equal(
                2,
                metadata.RootElement.GetProperty("mutationDetails").GetArrayLength());
            Assert.True(BaselineFixtureValidator.Validate(fixture).IsValid);

            var invalidChronology = fixture.ContractPackage with
            {
                Baseline = fixture.Baseline with
                {
                    ComponentMovements =
                    [
                        fixture.Baseline.ComponentMovements[0],
                        fixture.Baseline.ComponentMovements[1] with
                        {
                            OccurredAt = fixture.Baseline.ComponentMovements[0].OccurredAt.AddMinutes(-1)
                        },
                        ..fixture.Baseline.ComponentMovements.Skip(2)
                    ]
                }
            };
            var result = GeneratorContractValidator.Validate(invalidChronology);

            Assert.Contains(result.Errors, error =>
                error.Contains("chronology", StringComparison.OrdinalIgnoreCase));

            var invalidRelationshipsAndCounters = fixture.ContractPackage with
            {
                Baseline = fixture.Baseline with
                {
                    ComponentMovements =
                    [
                        fixture.Baseline.ComponentMovements[0] with
                        {
                            ComponentId = "COMP-UNKNOWN"
                        },
                        ..fixture.Baseline.ComponentMovements.Skip(1)
                    ],
                    UsageCounters =
                    [
                        fixture.Baseline.UsageCounters[0] with { FlightHours = -1 },
                        ..fixture.Baseline.UsageCounters.Skip(1)
                    ]
                }
            };
            var relationshipResult = GeneratorContractValidator.Validate(invalidRelationshipsAndCounters);

            Assert.Contains(relationshipResult.Errors, error =>
                error.Contains("relationship", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(relationshipResult.Errors, error =>
                error.Contains("usage", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void LiveComponentBScanContainsCandidatesWithoutLeakingTheCleanSerial()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-2026-09-10");
            var scanPath = fixture.GeneratedFiles.Single(path =>
                path.EndsWith("005-component-b-identity-scan.pdf", StringComparison.Ordinal));
            var scanText = File.ReadAllText(
                Path.Combine(outputDirectory, scanPath.Replace('/', Path.DirectorySeparatorChar)));
            var scanDocument = fixture.ContractPackage.Documents.Single(document =>
                document.SourceRecordId == "SOURCE-SCAN-0002");
            var generatorOutputs = fixture.GeneratedFiles
                .Where(path =>
                    !path.StartsWith("application-inputs/", StringComparison.Ordinal) &&
                    !path.StartsWith("evaluator-only/", StringComparison.Ordinal))
                .Select(path => File.ReadAllText(Path.Combine(
                    outputDirectory,
                    path.Replace('/', Path.DirectorySeparatorChar))));

            Assert.Equal(2, scanText.Split("Candidate serial:", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("SERIAL-0002", scanText, StringComparison.Ordinal);
            Assert.DoesNotContain("SERIAL-0002", Path.GetFileName(scanPath), StringComparison.Ordinal);
            Assert.Equal("005-component-b-identity-scan.pdf", scanDocument.FileName);
            Assert.Contains("/Title (Component B identity scan)", scanText, StringComparison.Ordinal);
            Assert.Contains("/Subject (Component B identity scan:", scanText, StringComparison.Ordinal);
            Assert.Contains("/Alt (Component B identity scan:", scanText, StringComparison.Ordinal);
            Assert.NotEmpty(generatorOutputs);
            Assert.All(
                generatorOutputs,
                output => Assert.DoesNotContain("SERIAL-0002", output, StringComparison.Ordinal));
            Assert.True(BaselineFixtureValidator.Validate(fixture).IsValid);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void LivePackStagesResponseWithoutRequestIdOrEvaluatorDataInApplicationInputs()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-2026-09-10");
            var stagedRoot = Path.Combine(outputDirectory, "staged-responses");
            var stagedFiles = Directory.EnumerateFiles(stagedRoot, "*", SearchOption.AllDirectories);

            Assert.NotEmpty(stagedFiles);
            Assert.All(stagedFiles, path =>
                Assert.DoesNotContain(
                    "requestId",
                    File.ReadAllText(path),
                    StringComparison.OrdinalIgnoreCase));

            var applicationFiles = Directory.EnumerateFiles(
                Path.Combine(outputDirectory, "application-inputs"),
                "*.json",
                SearchOption.AllDirectories);
            Assert.All(applicationFiles, path =>
            {
                var content = File.ReadAllText(path);
                Assert.DoesNotContain("intendedMutations", content, StringComparison.Ordinal);
                Assert.DoesNotContain("mutationDetails", content, StringComparison.Ordinal);
                Assert.DoesNotContain("evaluator-only", content, StringComparison.Ordinal);
            });

            using var events = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(outputDirectory, "replay-only", "events.json")));
            Assert.DoesNotContain(
                events.RootElement.EnumerateArray(),
                envelope => envelope.GetProperty("type").GetString() == "partner.response.received");
            Assert.True(BaselineFixtureValidator.Validate(fixture).IsValid);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void LiveValidationRejectsAnUndeclaredMissingHistoryDefect()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-2026-09-10");
            var undeclared = fixture.ContractPackage with
            {
                Receipt = fixture.Receipt with
                {
                    IntendedMutationIdentifiers =
                    [WorkflowContract.LiveAmbiguousIdentityMutation]
                }
            };

            var result = BaselineFixtureValidator.Validate(
                fixture with { ContractPackage = undeclared });

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error =>
                error.Contains("removal-history", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("declared", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static GeneratorConfiguration CreateConfiguration(string outputDirectory) =>
        new(
            42,
            "live-fixture-1",
            new DateOnly(2026, 9, 10),
            "RUN-0001",
            "live",
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
            "airlinedemo-live-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
