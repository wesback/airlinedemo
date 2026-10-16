using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace AirlineDemo.Api.SourceSystems;

// Raised when a source fixture fails verification; the source must not be registered.
public sealed class SourceFixtureRefusedException(string message) : InvalidOperationException(message);

public sealed record VerifiedSourceFixture(string SourceId, SourceFixtureManifest Manifest, byte[] FixtureBytes);

// Refuse-on-mismatch loader for the five signed source fixtures (PRD FR-002, AC-004, AC-005).
// The manifest digest detects drift only; the detached RSA-PSS signature establishes authenticity.
public static class SourceFixtureLoader
{
    private const string ScenarioDate = "2026-10-01";

    // Release-controlled SubjectPublicKeyInfo (DER, base64) of the fixture signing key. Its private key stays outside the repository.
    private const string PinnedPublicKeySpkiBase64 =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAxkWnGGlRJPFRJT1QRY4fwHuoyYzfnUuDxAthHYXU0LICueX076pEROxD7Myr4NdP5mQ8v04ZleQD3g5Pnt6kQBiLdgO0wo2ltqGr70ewlEwaTw1BtgEQA0ge47M0suLirPCsoAmRkeiisGTilreVfBzQXOl/ZjZvQnlubaB3jDBM+d8M/ZxBlLO93UwADRUUztW2Att/sMgbAQCZRB9in90AhBTM3SWWn/NlBayPlOqupB0GGogdeONg1WCLpnI0+pjuDtbcZGxSxpODOXaaR+TZepcSpXA8i1Cp0dXwH2PjLV/CBMgwioTHTvfU4MEF0UFCHUw66QYrcaHBqjzURwIDAQAB";

    private static readonly JsonSerializerOptions ManifestOptions = new() { PropertyNameCaseInsensitive = true };

    // Production registration: verifies the complete set against the pinned application key only.
    public static IReadOnlyDictionary<string, VerifiedSourceFixture> LoadPinned(string directory) =>
        Load(directory, Convert.FromBase64String(PinnedPublicKeySpkiBase64));

    // Test seam: the trust key is supplied explicitly. A missing key refuses every source.
    internal static IReadOnlyDictionary<string, VerifiedSourceFixture> Load(string directory, byte[]? trustKeySpki)
    {
        if (trustKeySpki is null || trustKeySpki.Length == 0)
        {
            throw new SourceFixtureRefusedException("Trust key is missing; source registration is refused.");
        }

        using var trustKey = RSA.Create();
        try
        {
            trustKey.ImportSubjectPublicKeyInfo(trustKeySpki, out _);
        }
        catch (CryptographicException)
        {
            throw new SourceFixtureRefusedException("Trust key is not a valid RSA SubjectPublicKeyInfo; source registration is refused.");
        }

        var verified = new Dictionary<string, VerifiedSourceFixture>(StringComparer.Ordinal);
        foreach (var source in SourceFixtureManifest.RequiredSources)
        {
            verified[source] = LoadSource(directory, source, trustKey);
        }

        return verified;
    }

    private static VerifiedSourceFixture LoadSource(string directory, string source, RSA trustKey)
    {
        var fixtureBytes = ReadRequired(directory, $"{source}.json", source, "fixture");
        var manifestBytes = ReadRequired(directory, $"{source}.manifest.json", source, "manifest");
        var signature = ReadRequired(directory, $"{source}.manifest.sig", source, "signature");

        if (!trustKey.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            throw Refuse(source, "manifest signature does not verify against the trust key.");
        }

        var (manifest, owningCaseIds) = ParseManifest(source, manifestBytes);

        var problems = new List<string>(manifest.Validate());
        if (!string.Equals(manifest.ScenarioDate, ScenarioDate, StringComparison.Ordinal))
        {
            problems.Add($"scenarioDate must be {ScenarioDate}.");
        }

        if (problems.Count > 0)
        {
            throw Refuse(source, string.Join(" ", problems));
        }

        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fixtureBytes));
        if (!string.Equals(digest, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse(source, "fixture SHA-256 digest does not match the manifest.");
        }

        var recordCaseIds = ReadRecordCaseIds(source, fixtureBytes);
        if (recordCaseIds.Count != manifest.RecordCounts[source])
        {
            throw Refuse(source, $"declared record count {manifest.RecordCounts[source]} does not match {recordCaseIds.Count} records.");
        }

        foreach (var caseId in recordCaseIds)
        {
            if (!owningCaseIds.Contains(caseId, StringComparer.Ordinal))
            {
                throw Refuse(source, $"record caseId '{caseId}' is not a declared owning case.");
            }
        }

        return new VerifiedSourceFixture(source, manifest, fixtureBytes);
    }

    private static (SourceFixtureManifest Manifest, IReadOnlyList<string> OwningCaseIds) ParseManifest(string source, byte[] manifestBytes)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<SourceFixtureManifest>(manifestBytes, ManifestOptions)
                ?? throw Refuse(source, "manifest is empty.");

            using var document = JsonDocument.Parse(manifestBytes);
            if (!document.RootElement.TryGetProperty("owningCaseIds", out var owning) || owning.ValueKind != JsonValueKind.Array)
            {
                throw Refuse(source, "manifest owningCaseIds is missing.");
            }

            var owningCaseIds = owning.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty)
                .ToList();
            if (owningCaseIds.Count == 0 || owningCaseIds.Any(string.IsNullOrWhiteSpace))
            {
                throw Refuse(source, "manifest owningCaseIds must name at least one case.");
            }

            return (manifest, owningCaseIds);
        }
        catch (JsonException)
        {
            throw Refuse(source, "manifest is not valid JSON.");
        }
    }

    private static List<string> ReadRecordCaseIds(string source, byte[] fixtureBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(fixtureBytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("sourceId", out var sourceId)
                || sourceId.ValueKind != JsonValueKind.String
                || sourceId.GetString() != source)
            {
                throw Refuse(source, "fixture sourceId does not match the source.");
            }

            if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            {
                throw Refuse(source, "fixture records array is missing.");
            }

            var caseIds = new List<string>();
            foreach (var record in records.EnumerateArray())
            {
                if (!record.TryGetProperty("caseId", out var caseId) || caseId.ValueKind != JsonValueKind.String)
                {
                    throw Refuse(source, "a fixture record has no caseId.");
                }

                caseIds.Add(caseId.GetString()!);
            }

            return caseIds;
        }
        catch (JsonException)
        {
            throw Refuse(source, "fixture is not valid JSON.");
        }
    }

    private static byte[] ReadRequired(string directory, string fileName, string source, string kind)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw Refuse(source, $"{kind} file '{fileName}' is missing.");
        }

        return File.ReadAllBytes(path);
    }

    private static SourceFixtureRefusedException Refuse(string source, string reason) =>
        new($"Source fixture '{source}' refused: {reason}");
}
