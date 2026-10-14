using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
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
    public Task Azure_store_passes_the_contract_against_a_blob_client_double() =>
        RunContractAsync(() => CreateAzureStore().Store);

    [Fact]
    public async Task Azure_adapter_never_sends_an_unconditional_write()
    {
        var (store, service) = CreateAzureStore();

        await store.StoreAsync("doc", Doc);
        await store.StoreAsync("doc", Doc);
        await store.StoreAsync("doc", OtherDoc);

        Assert.DoesNotContain(service.Calls, c => c.StartsWith("unconditional:", StringComparison.Ordinal));
        Assert.Equal(Doc, service.Blobs["doc/v1-" + Sha(Doc)]);
        Assert.Equal(OtherDoc, service.Blobs["doc/v2-" + Sha(OtherDoc)]);
    }

    [Fact]
    public async Task Azure_adapter_takes_the_next_version_when_another_writer_claims_the_first()
    {
        var (store, service) = CreateAzureStore();
        service.Blobs["doc/claims/v1"] = Encoding.UTF8.GetBytes(Sha(OtherDoc));

        var version = await store.StoreAsync("doc", Doc);

        Assert.Equal(2, version.Version);
        Assert.Equal(Doc, await store.ReadAsync(version.Name, version.Sha256));
    }

    [Fact]
    public async Task Concurrent_write_of_different_content_gets_its_own_version()
    {
        var container = new RecordingBlobContainer(new InMemoryEvidenceBlobContainer())
        {
            RaceOnNextCreate = true,
            RacePayload = Encoding.UTF8.GetBytes(Sha(OtherDoc)),
        };
        var store = new AzureEvidenceBlobStore(container);

        var version = await store.StoreAsync("doc", Doc);

        Assert.Equal(2, version.Version);
        Assert.Equal(Doc, await store.ReadAsync(version.Name, version.Sha256));
    }

    [Fact]
    public async Task Store_gives_up_after_repeated_claim_conflicts()
    {
        var container = new RecordingBlobContainer(new InMemoryEvidenceBlobContainer()) { RejectAllCreates = true };
        var store = new AzureEvidenceBlobStore(container);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync("doc", Doc));
    }

    [Fact]
    public async Task Invalid_document_ids_and_digests_are_rejected()
    {
        var store = new InMemoryEvidenceBlobStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.StoreAsync("a/b", Doc));
        await Assert.ThrowsAsync<ArgumentException>(() => store.StoreAsync(" ", Doc));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.StoreAsync("doc", null!));
        await Assert.ThrowsAsync<EvidenceDigestMismatchException>(() => store.ReadAsync("no-slash", Sha(Doc)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync("doc/v1-x", " "));
    }

    [Fact]
    public void Container_uri_carrying_a_sas_query_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            AzureEvidenceBlobStore.Create("https://acct.blob.core.windows.net/evidence?sv=2024&sig=abc"));
    }

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

        Assert.Equal(2, container.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal) && !c.Contains("/claims/", StringComparison.Ordinal)));
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
        Assert.Equal(1, container.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal) && !c.Contains("/claims/", StringComparison.Ordinal)));
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

    private static (AzureEvidenceBlobStore Store, FakeBlobService Service) CreateAzureStore()
    {
        var service = new FakeBlobService();
        var options = new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(service)) };
        var client = new BlobContainerClient(new Uri("https://acct.blob.core.windows.net/evidence"), options);
        return (new AzureEvidenceBlobStore(client), service);
    }

    // HTTP-level double of the Blob service for one container: it records every request and honours If-None-Match: *.
    private sealed class FakeBlobService : HttpMessageHandler
    {
        private const string ContainerPath = "/evidence/";

        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Get && uri.Query.Contains("comp=list", StringComparison.Ordinal))
            {
                var prefix = Uri.UnescapeDataString(
                    uri.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("prefix=", StringComparison.Ordinal))["prefix=".Length..]);
                Calls.Add("list:" + prefix);
                var items = Blobs.Keys
                    .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(k => $"<Blob><Name>{k}</Name><Properties /></Blob>");
                var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><EnumerationResults ContainerName=\"evidence\">"
                    + $"<Prefix>{prefix}</Prefix><Blobs>{string.Concat(items)}</Blobs><NextMarker /></EnumerationResults>";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
            }

            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (!path.StartsWith(ContainerPath, StringComparison.Ordinal))
            {
                return Status(HttpStatusCode.NotFound);
            }

            var name = path[ContainerPath.Length..];
            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                var conditional = request.Headers.TryGetValues("If-None-Match", out var conditions) && conditions.Contains("*");
                Calls.Add((conditional ? "create:" : "unconditional:") + name);
                if (conditional && Blobs.ContainsKey(name))
                {
                    return Status(HttpStatusCode.Conflict);
                }

                Blobs[name] = body;
                return Status(HttpStatusCode.Created);
            }

            Calls.Add("download:" + name);
            return Blobs.TryGetValue(name, out var blob)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(blob) }
                : Status(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Status(HttpStatusCode code) =>
            new(code) { Content = new ByteArrayContent(Array.Empty<byte>()) };
    }

    private sealed class RecordingBlobContainer(IEvidenceBlobContainer inner) : IEvidenceBlobContainer
    {
        public List<string> Calls { get; } = [];

        public byte[]? TamperedContent { get; set; }

        public bool RaceOnNextCreate { get; set; }

        // Content the racing writer stores under the contested name; defaults to the same content as ours.
        public byte[]? RacePayload { get; set; }

        public bool RejectAllCreates { get; set; }

        public Task<IReadOnlyList<string>> ListNamesAsync(string prefix, CancellationToken cancellationToken)
        {
            Calls.Add("list:" + prefix);
            return inner.ListNamesAsync(prefix, cancellationToken);
        }

        public async Task<bool> TryCreateAsync(string name, byte[] content, CancellationToken cancellationToken)
        {
            Calls.Add("create:" + name);
            if (RejectAllCreates)
            {
                return false;
            }

            if (RaceOnNextCreate)
            {
                // Simulate another writer creating the blob between our list and create.
                RaceOnNextCreate = false;
                await inner.TryCreateAsync(name, RacePayload ?? content, cancellationToken);
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
