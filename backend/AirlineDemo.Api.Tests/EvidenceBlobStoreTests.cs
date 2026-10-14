using System.Security.Cryptography;
using System.Text;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Tests;

public sealed class EvidenceBlobStoreTests
{
    private static readonly byte[] Doc = Encoding.UTF8.GetBytes("aircraft return readiness evidence");
    private static readonly byte[] OtherDoc = Encoding.UTF8.GetBytes("revised evidence");

    private static string Sha(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    [Fact]
    public Task InMemory_store_passes_the_contract() => RunContractAsync(() => new InMemoryEvidenceBlobStore());

    [Fact]
    public Task Azure_store_passes_the_contract_against_a_recording_double() =>
        RunContractAsync(() => new AzureEvidenceBlobStore(
            new RecordingBlobContainer(new InMemoryEvidenceBlobContainer())));

    private static async Task RunContractAsync(Func<IEvidenceBlobStore> factory)
    {
        var store = factory();

        var first = await store.StoreAsync("case-1-doc", Doc);
        Assert.Equal(1, first.Version);
        Assert.Equal(Sha(Doc), first.Sha256);
        Assert.Contains(Sha(Doc), first.Name);
        Assert.Contains("v1", first.Name);

        var same = await store.StoreAsync("case-1-doc", Doc);
        Assert.Equal(first, same);

        var second = await store.StoreAsync("case-1-doc", OtherDoc);
        Assert.Equal(2, second.Version);
        Assert.NotEqual(first.Name, second.Name);
        Assert.Contains(Sha(OtherDoc), second.Name);

        Assert.Equal(Doc, await store.ReadAsync(first.Name, first.Sha256));
        Assert.Equal(OtherDoc, await store.ReadAsync(second.Name, second.Sha256));
        Assert.Null(await store.ReadAsync("case-1-doc/v9-" + Sha(Doc), Sha(Doc)));
    }

    [Fact]
    public async Task Read_with_mismatched_digest_is_rejected_and_returns_no_content()
    {
        IEvidenceBlobStore[] stores =
        [
            new InMemoryEvidenceBlobStore(),
            new AzureEvidenceBlobStore(new InMemoryEvidenceBlobContainer()),
        ];

        foreach (var store in stores)
        {
            var stored = await store.StoreAsync("doc", Doc);
            await Assert.ThrowsAsync<EvidenceDigestMismatchException>(() => store.ReadAsync(stored.Name, Sha(OtherDoc)));
        }
    }

    [Fact]
    public async Task Read_rejects_content_whose_digest_does_not_match_the_stored_name()
    {
        var container = new RecordingBlobContainer(new InMemoryEvidenceBlobContainer());
        var store = new AzureEvidenceBlobStore(container);
        var version = await store.StoreAsync("doc", Doc);

        container.TamperedContent = OtherDoc;

        await Assert.ThrowsAsync<EvidenceDigestMismatchException>(() => store.ReadAsync(version.Name, version.Sha256));
    }

    [Fact]
    public async Task Azure_store_only_creates_blobs_and_never_overwrites_existing_ones()
    {
        var container = new RecordingBlobContainer(new InMemoryEvidenceBlobContainer());
        var store = new AzureEvidenceBlobStore(container);

        var first = await store.StoreAsync("doc", Doc);
        await store.StoreAsync("doc", Doc);
        await store.StoreAsync("doc", OtherDoc);

        Assert.Equal(2, container.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal)));
        Assert.Equal(Doc, await store.ReadAsync(first.Name, first.Sha256));
    }

    [Fact]
    public async Task Concurrent_identical_write_returns_existing_version_without_overwrite()
    {
        var container = new RecordingBlobContainer(new InMemoryEvidenceBlobContainer()) { RaceOnNextCreate = true };
        var store = new AzureEvidenceBlobStore(container);

        var version = await store.StoreAsync("doc", Doc);

        Assert.Equal(1, version.Version);
        Assert.Equal(Doc, await store.ReadAsync(version.Name, version.Sha256));
        Assert.Equal(1, container.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=abc123==;EndpointSuffix=core.windows.net")]
    [InlineData("BlobEndpoint=https://acct.blob.core.windows.net/evidence?sv=2024&SharedAccessSignature=abc")]
    public void Connection_strings_with_shared_key_material_are_rejected(string configuration)
    {
        Assert.Throws<ArgumentException>(() => AzureEvidenceBlobStore.Create(configuration));
    }

    [Fact]
    public void Create_accepts_an_https_container_uri_and_rejects_plain_http()
    {
        Assert.NotNull(AzureEvidenceBlobStore.Create("https://acct.blob.core.windows.net/evidence"));
        Assert.Throws<ArgumentException>(() => AzureEvidenceBlobStore.Create("http://acct.blob.core.windows.net/evidence"));
    }

    private sealed class RecordingBlobContainer(IEvidenceBlobContainer inner) : IEvidenceBlobContainer
    {
        public List<string> Calls { get; } = [];

        public byte[]? TamperedContent { get; set; }

        public bool RaceOnNextCreate { get; set; }

        public Task<IReadOnlyList<string>> ListNamesAsync(string prefix, CancellationToken cancellationToken)
        {
            Calls.Add("list:" + prefix);
            return inner.ListNamesAsync(prefix, cancellationToken);
        }

        public async Task<bool> TryCreateAsync(string name, byte[] content, CancellationToken cancellationToken)
        {
            Calls.Add("create:" + name);
            if (RaceOnNextCreate)
            {
                // Simulate another writer creating the same blob between our list and create.
                RaceOnNextCreate = false;
                await inner.TryCreateAsync(name, content, cancellationToken);
            }

            return await inner.TryCreateAsync(name, content, cancellationToken);
        }

        public async Task<byte[]?> DownloadAsync(string name, CancellationToken cancellationToken)
        {
            Calls.Add("download:" + name);
            return TamperedContent ?? await inner.DownloadAsync(name, cancellationToken);
        }
    }
}
