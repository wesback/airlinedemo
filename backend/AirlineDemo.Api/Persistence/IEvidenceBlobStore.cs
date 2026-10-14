using System.Security.Cryptography;

namespace AirlineDemo.Api.Persistence;

public sealed record EvidenceBlobVersion(string Name, string Sha256, int Version);

public sealed class EvidenceDigestMismatchException()
    : Exception("Evidence digest does not match the requested document; no content returned.");

public interface IEvidenceBlobStore
{
    Task<EvidenceBlobVersion> StoreAsync(string documentId, byte[] content, CancellationToken cancellationToken = default);

    Task<byte[]?> ReadAsync(string name, string expectedSha256, CancellationToken cancellationToken = default);
}

// Minimal blob-container surface. Implementations must never overwrite an existing blob.
public interface IEvidenceBlobContainer
{
    Task<IReadOnlyList<string>> ListNamesAsync(string prefix, CancellationToken cancellationToken);

    // Creates the blob only when no blob with that name exists; returns false when it already exists.
    Task<bool> TryCreateAsync(string name, byte[] content, CancellationToken cancellationToken);

    // Returns null when the blob does not exist.
    Task<byte[]?> DownloadAsync(string name, CancellationToken cancellationToken);
}

public abstract class EvidenceBlobStoreBase(IEvidenceBlobContainer container) : IEvidenceBlobStore
{
    private const int MaxAttempts = 5;

    public async Task<EvidenceBlobVersion> StoreAsync(string documentId, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(content);
        if (documentId.Contains('/'))
        {
            throw new ArgumentException("Document id must not contain '/'.", nameof(documentId));
        }

        var sha = ComputeSha256(content);
        var prefix = documentId + "/";
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var existing = (await container.ListNamesAsync(prefix, cancellationToken))
                .Select(name => ParseName(prefix, name))
                .OfType<EvidenceBlobVersion>()
                .ToList();

            var match = existing.Where(v => v.Sha256 == sha).OrderBy(v => v.Version).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }

            var next = existing.Count == 0 ? 1 : existing.Max(v => v.Version) + 1;
            var name = prefix + $"v{next}-{sha}";
            if (await container.TryCreateAsync(name, content, cancellationToken))
            {
                return new EvidenceBlobVersion(name, sha, next);
            }
            // Another writer took this version first; list again and retry.
        }

        throw new InvalidOperationException("Could not allocate an evidence version after repeated conflicts.");
    }

    public async Task<byte[]?> ReadAsync(string name, string expectedSha256, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        var expected = expectedSha256.ToLowerInvariant();
        var slash = name.LastIndexOf('/');
        var parsed = slash < 0 ? null : ParseName(name[..(slash + 1)], name);
        if (parsed is null || parsed.Sha256 != expected)
        {
            throw new EvidenceDigestMismatchException();
        }

        var content = await container.DownloadAsync(name, cancellationToken);
        if (content is null)
        {
            return null;
        }

        if (ComputeSha256(content) != expected)
        {
            throw new EvidenceDigestMismatchException();
        }

        return content;
    }

    // Blob names have the form "{documentId}/v{version}-{sha256}".
    private static EvidenceBlobVersion? ParseName(string prefix, string name)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = name[prefix.Length..];
        if (!rest.StartsWith('v'))
        {
            return null;
        }

        var dash = rest.IndexOf('-');
        if (dash < 2 || !int.TryParse(rest[1..dash], out var version) || version < 1)
        {
            return null;
        }

        return new EvidenceBlobVersion(name, rest[(dash + 1)..], version);
    }

    private static string ComputeSha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
