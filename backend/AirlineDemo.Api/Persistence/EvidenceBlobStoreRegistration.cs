namespace AirlineDemo.Api.Persistence;

/// <summary>Registers the evidence store selected by <c>Persistence:EvidenceProvider</c>: <c>Memory</c> (default) or <c>Blob</c>.</summary>
public static class EvidenceBlobStoreRegistration
{
    public const string ProviderKey = "Persistence:EvidenceProvider";
    public const string BlobContainerUriKey = "Persistence:EvidenceBlobContainerUri";

    public static IServiceCollection AddEvidenceBlobStore(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration[ProviderKey];
        if (string.IsNullOrWhiteSpace(provider) || string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEvidenceBlobStore>(_ => new InMemoryEvidenceBlobStore());
            return services;
        }

        if (!string.Equals(provider, "Blob", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{ProviderKey} must be Memory or Blob, but was '{provider}'.");
        }

        var containerUri = configuration[BlobContainerUriKey];
        if (string.IsNullOrWhiteSpace(containerUri))
        {
            throw new InvalidOperationException($"{BlobContainerUriKey} is required when {ProviderKey} is Blob.");
        }

        // Creating the container client makes no network request; the container is reached on first use.
        var store = AzureEvidenceBlobStore.Create(containerUri);
        services.AddSingleton<IEvidenceBlobStore>(store);
        return services;
    }
}
