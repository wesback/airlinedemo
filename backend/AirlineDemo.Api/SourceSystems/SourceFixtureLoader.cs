using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AirlineDemo.Api.SourceSystems;

// Trust anchor for source fixtures: the PEM public key of the release signing key. The private key stays outside
// the repository. While null, every fixture load is refused.
public static class SourceFixtureTrust
{
    public const string? PinnedReleasePublicKeyPem = null;
}

public enum SourceFixtureLoadStatus
{
    Published,
    AlreadyPublished,
    Refused,
}

public sealed record SourceFixtureLoadResult(SourceFixtureLoadStatus Status, IReadOnlyList<string> Reasons);

// A verified and fully parsed fixture set, ready for atomic publication.
public sealed record SourceFixtureCandidate(
    SourceFixtureManifest Manifest,
    string ManifestSha256,
    IReadOnlyList<SourceRecord<JsonNode>> Records);

public sealed record SourceBasisPin(string CaseId, string SourceFixtureVersion, string ManifestSha256);

public sealed record SourcePublicationSnapshot(
    IReadOnlyList<SourceRecord<JsonNode>> Records,
    IReadOnlyList<SourceBasisPin> BasisPins,
    IReadOnlyList<string> AuditEvents);

// Holds the published source state. A publication replaces all of it or none of it (NFR-002).
public sealed class SourcePublicationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _versionDigests = new(StringComparer.Ordinal);
    private List<SourceRecord<JsonNode>> _records = [];
    private List<SourceBasisPin> _basisPins = [];
    private List<string> _auditEvents = [];

    // Test seam: runs after the new state is staged and before it is published. A throw aborts the publication.
    public Action? BeforeCommit { get; set; }

    public SourcePublicationSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SourcePublicationSnapshot(_records.ToList(), _basisPins.ToList(), _auditEvents.ToList());
        }
    }

    internal SourceFixtureLoadResult Commit(SourceFixtureCandidate candidate)
    {
        var version = candidate.Manifest.SourceFixtureVersion;
        var digest = candidate.ManifestSha256;

        lock (_gate)
        {
            if (_versionDigests.TryGetValue(version, out var storedDigest))
            {
                return storedDigest == digest
                    ? new SourceFixtureLoadResult(SourceFixtureLoadStatus.AlreadyPublished, [])
                    : new SourceFixtureLoadResult(
                        SourceFixtureLoadStatus.Refused,
                        ["sourceFixtureVersion is already published with different content."]);
            }

            var records = _records.Concat(candidate.Records).ToList();
            var cases = candidate.Manifest.Files.SelectMany(f => f.RecordCounts.Keys).Distinct(StringComparer.Ordinal);
            var pins = _basisPins.Concat(cases.Select(c => new SourceBasisPin(c, version, digest))).ToList();
            var audit = _auditEvents.Append($"published {version} sha256={digest}").ToList();

            BeforeCommit?.Invoke();

            _records = records;
            _basisPins = pins;
            _auditEvents = audit;
            _versionDigests[version] = digest;
            return new SourceFixtureLoadResult(SourceFixtureLoadStatus.Published, []);
        }
    }
}

// Refuse-on-mismatch loader for signed, versioned source fixtures.
public sealed class SourceFixtureLoader
{
    private const string ManifestFileName = "manifest.json";
    private const string SignatureFileName = "manifest.sig";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RSA? _trustKey;
    private readonly DateOnly _scenarioDate;

    public SourceFixtureLoader(string? pinnedPublicKeyPem, DateOnly scenarioDate)
    {
        _scenarioDate = scenarioDate;
        if (!string.IsNullOrWhiteSpace(pinnedPublicKeyPem))
        {
            _trustKey = RSA.Create();
            _trustKey.ImportFromPem(pinnedPublicKeyPem);
        }
    }

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Verifies the fixture directory and publishes it atomically. Any mismatch refuses publication.
    public SourceFixtureLoadResult Load(string directory, SourcePublicationStore store)
    {
        var reasons = new List<string>();
        var candidate = Verify(directory, reasons);
        return candidate is null
            ? new SourceFixtureLoadResult(SourceFixtureLoadStatus.Refused, reasons)
            : Publish(store, candidate);
    }

    public SourceFixtureLoadResult Publish(SourcePublicationStore store, SourceFixtureCandidate candidate)
    {
        try
        {
            return store.Commit(candidate);
        }
        catch (Exception ex)
        {
            return new SourceFixtureLoadResult(
                SourceFixtureLoadStatus.Refused,
                [$"publication failed: {ex.GetType().Name}"]);
        }
    }

    private SourceFixtureCandidate? Verify(string directory, List<string> reasons)
    {
        if (_trustKey is null)
        {
            reasons.Add("no trust key is pinned; publication denied.");
            return null;
        }

        var manifestPath = Path.Combine(directory, ManifestFileName);
        var signaturePath = Path.Combine(directory, SignatureFileName);
        if (!File.Exists(manifestPath))
        {
            reasons.Add("manifest.json is missing.");
            return null;
        }

        if (!File.Exists(signaturePath))
        {
            reasons.Add("manifest.sig is missing.");
            return null;
        }

        // The signature covers the exact bytes of manifest.json, so it is checked before anything is parsed.
        var manifestBytes = File.ReadAllBytes(manifestPath);
        if (!TryDecodeSignature(File.ReadAllText(signaturePath), out var signature)
            || !VerifySignature(manifestBytes, signature))
        {
            reasons.Add("manifest signature does not verify against the pinned key.");
            return null;
        }

        SourceFixtureManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SourceFixtureManifest>(manifestBytes, JsonOptions);
        }
        catch (JsonException)
        {
            reasons.Add("manifest.json is not valid for this schema.");
            return null;
        }

        if (manifest is null)
        {
            reasons.Add("manifest.json is empty.");
            return null;
        }

        ValidateManifest(manifest, reasons);
        if (reasons.Count > 0)
        {
            return null;
        }

        var records = new List<SourceRecord<JsonNode>>();
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(directory, file.FileName);
            if (!File.Exists(path))
            {
                reasons.Add($"{file.FileName} is missing.");
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            if (!string.Equals(Sha256Hex(bytes), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"{file.FileName} does not match its declared sha256 digest.");
                continue;
            }

            List<SourceRecord<JsonNode>>? fileRecords;
            try
            {
                fileRecords = JsonSerializer.Deserialize<List<SourceRecord<JsonNode>>>(bytes, JsonOptions);
            }
            catch (JsonException)
            {
                reasons.Add($"{file.FileName} is not valid for this schema.");
                continue;
            }

            if (fileRecords is null)
            {
                reasons.Add($"{file.FileName} is empty.");
                continue;
            }

            ValidateFileRecords(file, fileRecords, reasons);
            records.AddRange(fileRecords);
        }

        return reasons.Count == 0
            ? new SourceFixtureCandidate(manifest, Sha256Hex(manifestBytes), records)
            : null;
    }

    private static bool TryDecodeSignature(string text, out byte[] signature)
    {
        try
        {
            signature = Convert.FromBase64String(text.Trim());
            return signature.Length > 0;
        }
        catch (FormatException)
        {
            signature = Array.Empty<byte>();
            return false;
        }
    }

    private bool VerifySignature(byte[] manifestBytes, byte[] signature)
    {
        try
        {
            return _trustKey!.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private void ValidateManifest(SourceFixtureManifest manifest, List<string> reasons)
    {
        if (manifest.SchemaVersion != SourceFixtureContract.SupportedSchemaVersion)
        {
            reasons.Add($"schemaVersion {manifest.SchemaVersion} is not supported.");
        }

        if (string.IsNullOrWhiteSpace(manifest.SourceFixtureVersion))
        {
            reasons.Add("sourceFixtureVersion is required.");
        }

        if (manifest.ScenarioDate != _scenarioDate)
        {
            reasons.Add($"scenarioDate {manifest.ScenarioDate:yyyy-MM-dd} does not match the scenario date.");
        }

        var files = manifest.Files ?? Array.Empty<SourceFixtureFile>();
        var declared = files.Select(f => f.Source).ToHashSet(StringComparer.Ordinal);
        if (files.Count != SourceFixtureContract.Sources.Count || !declared.SetEquals(SourceFixtureContract.Sources))
        {
            reasons.Add("the manifest must declare exactly one file for each of the five sources.");
        }

        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file.FileName)
                || file.FileName != Path.GetFileName(file.FileName)
                || file.FileName.Contains('\\'))
            {
                reasons.Add($"{file.Source} fileName must be a plain file name.");
            }

            if (!IsSha256Hex(file.Sha256))
            {
                reasons.Add($"{file.Source} sha256 must be 64 hexadecimal characters.");
            }

            if (file.RecordCounts is null || file.RecordCounts.Count == 0 || file.RecordCounts.Values.Any(count => count < 0))
            {
                reasons.Add($"{file.Source} recordCounts must declare at least one case with non-negative counts.");
            }
        }
    }

    private static void ValidateFileRecords(
        SourceFixtureFile file,
        List<SourceRecord<JsonNode>> records,
        List<string> reasons)
    {
        foreach (var record in records)
        {
            if (record is null)
            {
                reasons.Add($"{file.FileName} contains a null record.");
                continue;
            }

            if (!string.Equals(record.SourceId, file.Source, StringComparison.Ordinal))
            {
                reasons.Add($"{record.RecordId} belongs to source {record.SourceId}, not {file.Source}.");
            }

            // Case ownership: a record may only sit under a case that the manifest entry declares for this file.
            if (record.CaseId is null || !file.RecordCounts.ContainsKey(record.CaseId))
            {
                reasons.Add($"{record.RecordId} is assigned to case {record.CaseId}, which {file.FileName} does not declare.");
            }

            foreach (var problem in record.Validate(record.CaseId ?? string.Empty))
            {
                reasons.Add($"{record.RecordId}: {problem}");
            }
        }

        foreach (var (caseId, expected) in file.RecordCounts)
        {
            var actual = records.Count(r => r is not null && r.CaseId == caseId);
            if (actual != expected)
            {
                reasons.Add($"{file.FileName} declares {expected} records for {caseId} but holds {actual}.");
            }
        }
    }

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
