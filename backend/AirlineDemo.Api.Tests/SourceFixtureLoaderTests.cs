using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Tests;

public sealed class SourceFixtureLoaderTests
{
    private const string ValidDigest = "3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b";

    private static SourceFixtureManifest ValidManifest() => new()
    {
        SchemaVersion = "1",
        SourceFixtureVersion = "2026.10.1",
        ScenarioDate = "2026-10-01",
        Sha256 = ValidDigest,
        RecordCounts = new Dictionary<string, int>
        {
            ["technical"] = 4,
            ["maintenance"] = 3,
            ["finance"] = 5,
            ["records"] = 6,
            ["contracts"] = 2,
        },
    };

    // AC-004: a manifest with supported metadata and all declared counts validates.
    [Fact]
    public void ManifestMetadataAcceptsSupportedValues()
    {
        var manifest = ValidManifest();

        Assert.Equal("1", manifest.SchemaVersion);
        Assert.Equal("2026.10.1", manifest.SourceFixtureVersion);
        Assert.Equal("2026-10-01", manifest.ScenarioDate);
        Assert.Equal(ValidDigest, manifest.Sha256);
        Assert.Equal(4, manifest.RecordCounts["technical"]);
        Assert.Empty(manifest.Validate());
    }

    // AC-004: unsupported schema version, invalid scenario date, malformed digest and a missing source count are rejected.
    [Fact]
    public void ManifestMetadataRejectsInvalidValues()
    {
        var unsupportedSchema = ValidManifest() with { SchemaVersion = "2" };
        Assert.Equal(
            ["schemaVersion '2' is not supported; expected '1'."],
            unsupportedSchema.Validate());

        var invalidDate = ValidManifest() with { ScenarioDate = "2026-02-30" };
        Assert.Equal(
            ["scenarioDate '2026-02-30' must be a valid date in YYYY-MM-DD form."],
            invalidDate.Validate());

        var malformedDigest = ValidManifest() with { Sha256 = ValidDigest[..63] };
        Assert.Equal(
            ["sha256 must be 64 hexadecimal characters."],
            malformedDigest.Validate());

        var digestWithTrailingNewline = ValidManifest() with { Sha256 = ValidDigest + "\n" };
        Assert.Equal(
            ["sha256 must be 64 hexadecimal characters."],
            digestWithTrailingNewline.Validate());

        var missingCount = ValidManifest();
        var counts = new Dictionary<string, int>(missingCount.RecordCounts);
        counts.Remove("finance");
        missingCount = missingCount with { RecordCounts = counts };
        Assert.Equal(
            ["recordCounts is missing a count for source 'finance'."],
            missingCount.Validate());
    }
}
