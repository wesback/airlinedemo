using System.Security.Cryptography;
using System.Text;

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
        var claimPrefix = prefix + "claims/";
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var names = await container.ListNamesAsync(prefix, cancellationToken);
            var stored = names.Select(name => ParseName(prefix, name)).OfType<EvidenceBlobVersion>().ToList();
            var claimed = names.Select(name => ParseClaimVersion(claimPrefix, name)).OfType<int>().ToList();

            var match = stored.Where(v => v.Sha256 == sha).OrderBy(v => v.Version).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }

            // A claim without a content blob belongs to a writer still in progress; adopt it when it holds this digest.
            foreach (var version in claimed.Except(stored.Select(v => v.Version)).OrderBy(v => v))
            {
                if (await ReadClaimAsync(claimPrefix, version, cancellationToken) == sha)
                {
                    var adopted = prefix + $"v{version}-{sha}";
                    await container.TryCreateAsync(adopted, content, cancellationToken);
                    return new EvidenceBlobVersion(adopted, sha, version);
                }
            }

            // Each version number is claimed by creating a claim blob, which is unique per document, so two
            // different documents can never receive the same version.
            var next = stored.Select(v => v.Version).Concat(claimed).DefaultIfEmpty(0).Max() + 1;
            if (!await container.TryCreateAsync(claimPrefix + $"v{next}", Encoding.UTF8.GetBytes(sha), cancellationToken))
            {
                // Another writer claimed this version first; list again and retry.
                continue;
            }

            var name = prefix + $"v{next}-{sha}";
            await container.TryCreateAsync(name, content, cancellationToken);
            return new EvidenceBlobVersion(name, sha, next);
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

    // Claim blobs are named "{documentId}/claims/v{version}" and hold the digest of the writer that claimed it.
    private async Task<string?> ReadClaimAsync(string claimPrefix, int version, CancellationToken cancellationToken)
    {
        var bytes = await container.DownloadAsync(claimPrefix + $"v{version}", cancellationToken);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static int? ParseClaimVersion(string claimPrefix, string name)
    {
        if (!name.StartsWith(claimPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = name[claimPrefix.Length..];
        return rest.StartsWith('v') && int.TryParse(rest[1..], out var version) && version >= 1 ? version : null;
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
