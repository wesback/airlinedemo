namespace AirlineDemo.Api;

public interface IDocumentStorage
{
    Task<string?> ReadPagePreviewAsync(string storageLocator, int page, CancellationToken cancellationToken);
}

public sealed class FileDocumentStorage : IDocumentStorage
{
    private readonly string rootDirectory;

    public FileDocumentStorage(string rootDirectory)
    {
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(this.rootDirectory);
    }

    public async Task<string?> ReadPagePreviewAsync(
        string storageLocator,
        int page,
        CancellationToken cancellationToken)
    {
        if (page < 1 || Path.IsPathRooted(storageLocator) || storageLocator.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var documentDirectory = Path.Combine(rootDirectory, storageLocator);
        var pagePath = Path.Combine(documentDirectory, $"page-{page}.txt");
        if (!File.Exists(pagePath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(pagePath, cancellationToken);
    }
}
