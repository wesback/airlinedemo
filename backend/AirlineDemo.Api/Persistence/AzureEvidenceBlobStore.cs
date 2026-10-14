using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AirlineDemo.Api.Persistence;

public sealed class AzureEvidenceBlobStore(IEvidenceBlobContainer container) : EvidenceBlobStoreBase(container)
{
    // Only an https container URI is accepted; access uses DefaultAzureCredential, never a key or SAS.
    public static AzureEvidenceBlobStore Create(string containerUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerUri);
        if (containerUri.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase)
            || containerUri.Contains("SharedAccessSignature=", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Account keys and SAS tokens are not accepted; configure a container URI and use DefaultAzureCredential.",
                nameof(containerUri));
        }

        if (!Uri.TryCreate(containerUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Expected an https container URI.", nameof(containerUri));
        }

        var client = new BlobContainerClient(uri, new DefaultAzureCredential());
        return new AzureEvidenceBlobStore(new BlobEvidenceContainer(client));
    }
}

internal sealed class BlobEvidenceContainer(BlobContainerClient client) : IEvidenceBlobContainer
{
    public async Task<IReadOnlyList<string>> ListNamesAsync(string prefix, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await foreach (var item in client.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken))
        {
            names.Add(item.Name);
        }

        return names;
    }

    public async Task<bool> TryCreateAsync(string name, byte[] content, CancellationToken cancellationToken)
    {
        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
        };

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            await client.GetBlobClient(name).UploadAsync(stream, options, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return false;
        }
    }

    public async Task<byte[]?> DownloadAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetBlobClient(name).DownloadContentAsync(cancellationToken);
            return response.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
