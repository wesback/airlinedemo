using System.Text.Json;
using AirlineDemo.Generator;

namespace AirlineDemo.Tests;

public sealed class ContradictionFixtureTests
{
    [Fact]
    public void ContradictionProfileEmitsAChronologicalLaterVersionForTheSameScopedRecord()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-contradiction-1");
            var versions = fixture.ContractPackage.Documents
                .Where(document => document.DocumentId == "DOC-0004")
                .OrderBy(document => document.Version)
                .ToArray();
            var laterPackage = fixture.ContractPackage.SubmissionPackages
                .Single(package => package.PackageId == "PKG-0003");

            Assert.Equal(
                [WorkflowContract.ContradictionLaterVersionMutation],
                fixture.Receipt.IntendedMutationIdentifiers);
            Assert.Equal([1, 2], versions.Select(document => document.Version));
            Assert.Equal(versions[0].DocumentId, versions[1].DocumentId);
            Assert.Equal(versions[0].SourceRecordId, versions[1].SourceRecordId);
            Assert.NotEqual(versions[0].Sha256, versions[1].Sha256);
            Assert.True(versions[1].IssuedOn > versions[0].IssuedOn);
            Assert.Equal(versions[1], laterPackage.Manifest.Single());
            Assert.Equal(
                [650L, 2200L, 900L, 2100L],
                fixture.Baseline!.UsageCounters.Select(counter => counter.FlightHours));
            Assert.DoesNotContain(
                fixture.ContractPackage.PathBoundaries.SelectedInitialInputManifest.Entries,
                entry => entry.DocumentId == versions[1].DocumentId && entry.Version == 2);

            using var metadata = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(outputDirectory, "evaluator-only", "scenario-metadata.json")));
            var detail = metadata.RootElement.GetProperty("mutationDetails")
                .EnumerateArray()
                .Single();
            Assert.Equal(versions[0].Sha256, detail.GetProperty("earlierSha256").GetString());
            Assert.Equal(versions[1].Sha256, detail.GetProperty("laterSha256").GetString());
            Assert.Equal("PKG-0003", detail.GetProperty("packageId").GetString());
            Assert.Equal(
                "CASE-RUN-0001",
                detail.GetProperty("scope").GetProperty("caseId").GetString());
            Assert.True(BaselineFixtureValidator.Validate(fixture).IsValid);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void ContradictionValidationRejectsUndeclaredOrUnrelatedConflicts()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-contradiction-1");
            var undeclared = fixture with
            {
                ContractPackage = fixture.ContractPackage with
                {
                    Receipt = fixture.ContractPackage.Receipt with
                    {
                        IntendedMutationIdentifiers = []
                    }
                }
            };

            var undeclaredResult = BaselineFixtureValidator.Validate(undeclared);
            Assert.False(undeclaredResult.IsValid);
            Assert.Contains(
                undeclaredResult.Errors,
                error => error.Contains("not declared", StringComparison.OrdinalIgnoreCase));

            var metadataPath = Path.Combine(
                outputDirectory,
                "evaluator-only",
                "scenario-metadata.json");
            var metadata = File.ReadAllText(metadataPath)
                .Replace(
                    "AIRLINE-0001",
                    "AIRLINE-9999",
                    StringComparison.Ordinal);
            File.WriteAllText(metadataPath, metadata);

            var unrelatedScopeResult = BaselineFixtureValidator.Validate(fixture);
            Assert.False(unrelatedScopeResult.IsValid);
            Assert.Contains(
                unrelatedScopeResult.Errors,
                error => error.Contains("scope", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void ContradictionApplicationInputsContainEvidenceOnly()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-contradiction-1");
            var applicationFiles = Directory.EnumerateFiles(
                Path.Combine(outputDirectory, "application-inputs"),
                "*",
                SearchOption.AllDirectories);

            Assert.Contains(
                applicationFiles,
                path => path.EndsWith("package-002/manifest.json", StringComparison.Ordinal));
            Assert.All(applicationFiles, path =>
            {
                var content = File.ReadAllText(path);
                Assert.DoesNotContain("\"finding\"", content, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("\"policyDecision\"", content, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("\"reviewDecision\"", content, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("\"approval\"", content, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("approval-history", content, StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static GeneratorConfiguration CreateConfiguration(string outputDirectory) =>
        new(
            42,
            "contradiction-fixture-1",
            new DateOnly(2026, 9, 10),
            "RUN-0001",
            "contradiction",
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
            "airlinedemo-contradiction-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
