namespace AirlineDemo.Api.Persistence;

public sealed class InMemoryEvidenceBlobStore() : EvidenceBlobStoreBase(new InMemoryEvidenceBlobContainer());

public sealed class InMemoryEvidenceBlobContainer : IEvidenceBlobContainer
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<IReadOnlyList<string>> ListNamesAsync(string prefix, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<string> names = _blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            return Task.FromResult(names);
        }
    }

    public Task<bool> TryCreateAsync(string name, byte[] content, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_blobs.TryAdd(name, (byte[])content.Clone()));
        }
    }

    public Task<byte[]?> DownloadAsync(string name, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            byte[]? content = _blobs.TryGetValue(name, out var blob) ? (byte[])blob.Clone() : null;
            return Task.FromResult(content);
        }
    }
}
