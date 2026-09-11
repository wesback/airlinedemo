using System.Text.Json;
using AirlineDemo.Generator;

namespace AirlineDemo.Tests;

public sealed class DuplicateEventsFixtureTests
{
    [Fact]
    public void DuplicateEventsProfileEmitsAValidReplayAndConflictSequence()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-duplicate-events-1");
            var events = fixture.ContractPackage.Events;
            using var emittedEvents = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(outputDirectory, "replay-only", "events.json")));
            var declaredPackageIds = fixture.ContractPackage.SubmissionPackages
                .Select(package => package.PackageId)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(3, events.Count);
            Assert.Equal(3, emittedEvents.RootElement.GetArrayLength());
            Assert.All(events, envelope => Assert.Equal("1.0", envelope.SchemaVersion));
            Assert.Equal((events[0].RunId, events[0].EventId), (events[1].RunId, events[1].EventId));
            Assert.Equal((events[0].RunId, events[0].EventId), (events[2].RunId, events[2].EventId));
            Assert.Equal(events[0].Payload.GetRawText(), events[1].Payload.GetRawText());
            Assert.NotEqual(events[0].Payload.GetRawText(), events[2].Payload.GetRawText());
            Assert.Equal(
                events.Count,
                events.Select(envelope => envelope.OccurredAt).Distinct().Count());
            Assert.Equal(
                events.Count,
                events.Select(envelope => envelope.ScenarioEffectiveAt).Distinct().Count());
            Assert.All(
                events,
                envelope => Assert.Contains(
                    envelope.Payload.GetProperty("packageId").GetString()!,
                    declaredPackageIds));
            Assert.True(BaselineFixtureValidator.Validate(fixture).IsValid);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void DuplicateEventsValidationRejectsInvalidCanonicalRelationshipsAndReferences()
    {
        var outputDirectory = CreateTemporaryDirectory();
        try
        {
            var fixture = FixtureGenerator.Generate(
                CreateConfiguration(outputDirectory),
                "template-duplicate-events-1");
            var events = fixture.ContractPackage.Events;

            using var differentPayload = JsonDocument.Parse("""{"packageId":"PKG-0002"}""");
            var changedDuplicate = fixture.ContractPackage with
            {
                Events =
                [
                    events[0],
                    events[1] with { Payload = differentPayload.RootElement.Clone() },
                    events[2]
                ]
            };
            var duplicateResult = GeneratorContractValidator.Validate(changedDuplicate);
            Assert.Contains(
                duplicateResult.Errors,
                error => error.Contains("identical pair", StringComparison.OrdinalIgnoreCase));

            var sameConflict = fixture.ContractPackage with
            {
                Events =
                [
                    events[0],
                    events[1],
                    events[2] with { Payload = events[0].Payload }
                ]
            };
            var conflictResult = GeneratorContractValidator.Validate(sameConflict);
            Assert.Contains(
                conflictResult.Errors,
                error => error.Contains("different canonical payload", StringComparison.OrdinalIgnoreCase));

            var invalidReferences = fixture.ContractPackage with
            {
                Events =
                [
                    events[0] with { CorrelationId = string.Empty },
                    events[1] with { CaseId = string.Empty },
                    events[2] with
                    {
                        Payload = CreatePayload("PKG-UNKNOWN")
                    }
                ]
            };
            var referenceResult = BaselineFixtureValidator.Validate(
                fixture with { ContractPackage = invalidReferences });
            Assert.Contains(
                referenceResult.Errors,
                error => error.Contains("correlation ID", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                referenceResult.Errors,
                error => error.Contains("scope IDs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                referenceResult.Errors,
                error => error.Contains(
                    "declared package reference",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static JsonElement CreatePayload(string packageId)
    {
        using var payload = JsonDocument.Parse($$"""{"packageId":"{{packageId}}"}""");
        return payload.RootElement.Clone();
    }

    private static GeneratorConfiguration CreateConfiguration(string outputDirectory) =>
        new(
            42,
            "duplicate-events-fixture-1",
            new DateOnly(2026, 9, 10),
            "RUN-0001",
            "duplicate-events",
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
            "airlinedemo-duplicate-events-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
