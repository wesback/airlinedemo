using System.Security.Cryptography;
using System.Text.Json;

namespace AirlineDemo.Api.SourceSystems;

// Release-controlled trust anchor (SubjectPublicKeyInfo, base64). The private key is kept outside the repository.
public static class PinnedSourceFixtureKey
{
    public static readonly byte[] SubjectPublicKeyInfo = Convert.FromBase64String(
        "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAoDXVNodPXp/y0zQLK+oAo0HcQvEDjX+k1Ih3+hQyi1C9LjdojMF8Tq2SGFnMx5GIMIlhPI55jftnAZ8LZ/ADYl4xNOi7D6j3UmU33FAHoNUHJqVTdcl8JqY1/UfF+4JfUoJoE2CzjnFqKEhykHfAhNpqiceDaSRMB3qHDVTgl8lWGb6EQRktwLZrxdenbc6c2gyY0U6fsiBgd7fc9iSzDgdcj8Tf8/94RgexKgi+EnM86QvaMveA45afL+XhHsOS9dKPurQ0LW9cjMwXf6C3k7cymHWnXnpQh7BWNDc8ZDy2BeiAT3K+OWxkX596ZTct0TiASaeKOWzLGDxxTzhvm84kozvgFP1GPaQcdmjfBp2SQgxiAXeXEaLi3iy7nfo4lwZ4Z1Kx/C4qaTvX7LNPfCIJzKCbLzRdG32b2SFETxcc/859KyFPfhCoHJXoRnONdLpeRImjgIZhqaMyUEaqUh/dHJeeNn+0oYubcRsZCzsQ9PQN5TOUrtvyJG6EExpTAgMBAAE=");
}

// Raised when a fixture set is refused; nothing from a refused set is published.
public sealed class SourceFixtureRefusedException(string message) : Exception(message);

public sealed record PublishedSourceRecord(string SourceId, string RecordId, int RecordVersion, string CaseId);

public sealed record SourceBasisPin(string SourceId, string SourceFixtureVersion, string Sha256);

public sealed record SourceAuditEvent(string Action, string SourceFixtureVersion, string Detail);

// A fully verified set of the five source fixtures.
public sealed record SourceFixtureSet(
    string SourceFixtureVersion,
    string Digest,
    IReadOnlyList<PublishedSourceRecord> Records,
    IReadOnlyList<SourceBasisPin> Pins);

// Verifies each source's detached RSA-PSS/SHA-256 manifest signature, then the manifest metadata, digest and
// count against the fixture. The manifest digest detects drift only; authenticity comes from the signature (PRD FR-002, AC-004, AC-005).
public sealed class SourceFixtureLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly byte[]? _publicKeySpki;

    // The key argument is the test seam; production code passes PinnedSourceFixtureKey.SubjectPublicKeyInfo.
    public SourceFixtureLoader(byte[]? publicKeySpki)
    {
        _publicKeySpki = publicKeySpki;
    }

    public static SourceFixtureLoader CreatePinned() => new(PinnedSourceFixtureKey.SubjectPublicKeyInfo);

    // Loads all five sources or throws SourceFixtureRefusedException; never returns a partial set.
    public SourceFixtureSet Load(string directory)
    {
        if (_publicKeySpki is null || _publicKeySpki.Length == 0)
        {
            throw new SourceFixtureRefusedException("Source fixture trust key is missing.");
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(_publicKeySpki, out _);
        }
        catch (CryptographicException)
        {
            throw new SourceFixtureRefusedException("Source fixture trust key is not a valid public key.");
        }

        var records = new List<PublishedSourceRecord>();
        var pins = new List<SourceBasisPin>();
        string? version = null;

        foreach (var source in SourceFixtureManifest.RequiredSources)
        {
            var manifest = LoadSource(directory, source, rsa, records);
            if (version is not null && !string.Equals(version, manifest.SourceFixtureVersion, StringComparison.Ordinal))
            {
                throw new SourceFixtureRefusedException(
                    $"Source '{source}' declares sourceFixtureVersion '{manifest.SourceFixtureVersion}', expected '{version}'.");
            }

            version = manifest.SourceFixtureVersion;
            pins.Add(new SourceBasisPin(source, manifest.SourceFixtureVersion, manifest.Sha256.ToLowerInvariant()));
        }

        var digest = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", pins.Select(p => $"{p.SourceId}:{p.Sha256}")))))
            .ToLowerInvariant();
        return new SourceFixtureSet(version!, digest, records.AsReadOnly(), pins.AsReadOnly());
    }

    // Loads, verifies and publishes in one step for application start-up.
    public static SourceFixturePublication PublishVerified(string directory)
    {
        var publication = new SourceFixturePublication();
        publication.Publish(CreatePinned().Load(directory));
        return publication;
    }

    private static SourceFixtureManifest LoadSource(string directory, string source, RSA rsa, List<PublishedSourceRecord> records)
    {
        var fixture = ReadRequired(directory, $"{source}.json");
        var manifestBytes = ReadRequired(directory, $"{source}.manifest.json");
        var signatureText = System.Text.Encoding.UTF8.GetString(ReadRequired(directory, $"{source}.manifest.sig")).Trim();

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText);
        }
        catch (FormatException)
        {
            throw new SourceFixtureRefusedException($"Signature for source '{source}' is not valid base64.");
        }

        if (!rsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            throw new SourceFixtureRefusedException($"Signature for source '{source}' does not verify.");
        }

        SourceFixtureManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SourceFixtureManifest>(manifestBytes, JsonOptions)
                ?? throw new SourceFixtureRefusedException($"Manifest for source '{source}' is empty.");
        }
        catch (JsonException)
        {
            throw new SourceFixtureRefusedException($"Manifest for source '{source}' is not valid JSON.");
        }

        IReadOnlyList<SourceRecord<IReadOnlyDictionary<string, string>>> parsed;
        try
        {
            parsed = FixtureSourceSystem<IReadOnlyDictionary<string, string>>.ParseFixture(
                source, $"{source}.json", manifest, fixture, fields => fields);
            CheckOwnership(source, fixture);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            throw new SourceFixtureRefusedException(ex.Message);
        }

        records.AddRange(parsed.Select(r => new PublishedSourceRecord(source, r.RecordId, r.RecordVersion, r.CaseId)));
        return manifest;
    }

    // A record may declare its owning case; a caseId that differs from it is refused.
    private static void CheckOwnership(string source, byte[] fixture)
    {
        using var document = JsonDocument.Parse(fixture);
        foreach (var record in document.RootElement.GetProperty("records").EnumerateArray())
        {
            if (record.TryGetProperty("owningCaseId", out var owner)
                && !string.Equals(owner.GetString(), record.GetProperty("caseId").GetString(), StringComparison.Ordinal))
            {
                throw new SourceFixtureRefusedException(
                    $"Source '{source}' record '{record.GetProperty("recordId").GetString()}' caseId does not match its owning case.");
            }
        }
    }

    private static byte[] ReadRequired(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new SourceFixtureRefusedException($"Required source fixture file '{fileName}' is missing.");
        }

        return File.ReadAllBytes(path);
    }
}

// Published source state. A refused or failed publication leaves all three collections unchanged (NFR-002).
public sealed class SourceFixturePublication
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _versions = new(StringComparer.Ordinal);
    private IReadOnlyList<PublishedSourceRecord> _records = [];
    private IReadOnlyList<SourceBasisPin> _pins = [];
    private IReadOnlyList<SourceAuditEvent> _audit = [];

    // Test seam: runs after the new state is built and before it is swapped in; throwing aborts the publication.
    public Action? BeforeCommit { get; set; }

    public IReadOnlyList<PublishedSourceRecord> Records { get { lock (_gate) { return _records; } } }

    public IReadOnlyList<SourceBasisPin> Pins { get { lock (_gate) { return _pins; } } }

    public IReadOnlyList<SourceAuditEvent> AuditEvents { get { lock (_gate) { return _audit; } } }

    // Test seam: seeds the published state.
    public void Seed(
        IEnumerable<PublishedSourceRecord> records,
        IEnumerable<SourceBasisPin> pins,
        IEnumerable<SourceAuditEvent> auditEvents)
    {
        lock (_gate)
        {
            _records = records.ToList().AsReadOnly();
            _pins = pins.ToList().AsReadOnly();
            _audit = auditEvents.ToList().AsReadOnly();
        }
    }

    // Returns false for a no-op (same version, same digest); refuses a stored version with different content.
    public bool Publish(SourceFixtureSet set)
    {
        lock (_gate)
        {
            if (_versions.TryGetValue(set.SourceFixtureVersion, out var storedDigest))
            {
                if (string.Equals(storedDigest, set.Digest, StringComparison.Ordinal))
                {
                    return false;
                }

                throw new SourceFixtureRefusedException(
                    $"sourceFixtureVersion '{set.SourceFixtureVersion}' is already published with different content.");
            }

            var records = _records.Concat(set.Records).ToList().AsReadOnly();
            var pins = _pins.Concat(set.Pins).ToList().AsReadOnly();
            var audit = _audit
                .Append(new SourceAuditEvent("source_fixture_published", set.SourceFixtureVersion, set.Digest))
                .ToList().AsReadOnly();

            BeforeCommit?.Invoke();

            _records = records;
            _pins = pins;
            _audit = audit;
            _versions[set.SourceFixtureVersion] = set.Digest;
            return true;
        }
    }
}
