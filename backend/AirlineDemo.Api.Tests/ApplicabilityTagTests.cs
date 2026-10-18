using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Tests;

public sealed class ApplicabilityTagTests
{
    private static readonly string[] Sources = ["technical", "maintenance", "finance", "records", "contracts"];
    private static string CommittedSources() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sources");

    [Fact]
    public void PayloadContractAcceptsAllowedTagsAndRejectsExactInvalidCases()
    {
        const string RequirementId = "SYN-AD-2026-014";

        Assert.Empty(ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>
        {
            ["requirementId"] = RequirementId,
        }));
        foreach (var regime in new[] { "EASA", "FAA-91", "FAA-121", "Other" })
        {
            Assert.Empty(ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>
            {
                ["requirementId"] = RequirementId,
                ["regime"] = regime,
            }));
        }
        foreach (var responsibleParty in new[] { "lessee-operator", "lessor", "camo", "unassigned" })
        {
            Assert.Empty(ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>
            {
                ["requirementId"] = RequirementId,
                ["responsibleParty"] = responsibleParty,
            }));
        }

        Assert.Equal(
            ["Applicability requirementId is required."],
            ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>()));
        Assert.Equal(
            ["Applicability requirementId must be a non-empty string."],
            ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?> { ["requirementId"] = "" }));
        Assert.Equal(
            ["Applicability requirementId must be a non-empty string."],
            ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?> { ["requirementId"] = 14 }));
        Assert.Equal(
            ["Applicability regime must be EASA, FAA-91, FAA-121 or Other."],
            ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>
            {
                ["requirementId"] = RequirementId,
                ["regime"] = "ICAO",
            }));
        Assert.Equal(
            ["Applicability responsibleParty must be lessee-operator, lessor, camo or unassigned."],
            ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>
            {
                ["requirementId"] = RequirementId,
                ["responsibleParty"] = "operator",
            }));
        Assert.Equal(
            ["Unexpected applicability field 'clearance'."],
            ApplicabilityPayloadValidator.Validate(new Dictionary<string, object?>
            {
                ["requirementId"] = RequirementId,
                ["clearance"] = "cleared",
            }));
    }

    [Fact]
    public void UpdatedFixtureVerifiesAndTamperingWithoutDetachedSignatureFailsAtomically()
    {
        using var key = RSA.Create(2048);
        using var directory = new TemporaryDirectory();
        CopyDirectory(CommittedSources(), directory.Path);
        foreach (var source in Sources)
        {
            var manifestPath = Path.Combine(directory.Path, $"{source}.manifest.json");
            File.WriteAllBytes(
                Path.Combine(directory.Path, $"{source}.manifest.sig"),
                key.SignData(File.ReadAllBytes(manifestPath), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        }

        var store = new SourceFixtureStore(PublishedFixtureState.Empty);
        Assert.Equal(
            SourceFixturePublication.Published,
            SourceFixtureLoader.LoadAndPublish(directory.Path, key.ExportSubjectPublicKeyInfo(), store));
        var before = store.State;

        var fixturePath = Path.Combine(directory.Path, "technical.json");
        var fixture = File.ReadAllBytes(fixturePath);
        var text = Encoding.UTF8.GetString(fixture);
        var changedAt = text.IndexOf("SYN-AD-2026-014", StringComparison.Ordinal);
        Assert.True(changedAt >= 0);
        var changedCharacter = changedAt + "SYN-AD-2026-014".Length - 1;
        fixture[Encoding.UTF8.GetByteCount(text[..changedCharacter])] = (byte)'5';
        File.WriteAllBytes(fixturePath, fixture);

        var manifestPathToChange = Path.Combine(directory.Path, "technical.manifest.json");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(manifestPathToChange))!;
        manifest["sha256"] = JsonSerializer.SerializeToElement(Convert.ToHexString(SHA256.HashData(fixture)).ToLowerInvariant());
        File.WriteAllBytes(manifestPathToChange, JsonSerializer.SerializeToUtf8Bytes(manifest));

        var error = Assert.Throws<SourceFixtureRefusedException>(() =>
            SourceFixtureLoader.LoadAndPublish(directory.Path, key.ExportSubjectPublicKeyInfo(), store));
        Assert.Equal("Source fixture 'technical' refused: manifest signature does not verify against the trust key.", error.Message);
        Assert.Equal(before.Packs, store.State.Packs);
        Assert.Equal(before.SourceRecords, store.State.SourceRecords);
        Assert.Equal(before.BasisPins, store.State.BasisPins);
        Assert.Equal(before.AuditEvents, store.State.AuditEvents);
        Assert.Equal(before.CaseData, store.State.CaseData);

        Assert.Equal(5, SourceFixtureLoader.LoadPinned(CommittedSources()).Count);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
