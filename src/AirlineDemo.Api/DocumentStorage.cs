using System.Security.Cryptography;

namespace AirlineDemo.Api;

public interface IDocumentStorage
{
    Task<string?> ReadPagePreviewAsync(string storageLocator, int page, CancellationToken cancellationToken);
}

public interface IManifestDocumentStorage
{
    Task<string?> ValidateManifestAsync(
        CaseContext context,
        IReadOnlyList<DocumentMetadata> manifest,
        CancellationToken cancellationToken);
}

public sealed class FileDocumentStorage : IDocumentStorage, IManifestDocumentStorage
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
        var documentDirectory = ResolvePath(storageLocator);
        if (page < 1 || documentDirectory is null)
        {
            return null;
        }

        var pagePath = Path.Combine(documentDirectory, $"page-{page}.txt");
        if (!File.Exists(pagePath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(pagePath, cancellationToken);
    }

    public async Task<string?> ValidateManifestAsync(
        CaseContext context,
        IReadOnlyList<DocumentMetadata> manifest,
        CancellationToken cancellationToken)
    {
        var caseDirectory = ResolvePath(
            Path.Combine(context.RunId, context.CaseId));
        if (caseDirectory is null || !Directory.Exists(caseDirectory))
        {
            return "The package input storage does not exist.";
        }

        var declaredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in manifest)
        {
            if (document is null)
            {
                return "The package manifest contains a null document.";
            }

            var locator = $"{context.RunId}/{context.CaseId}/{document.DocumentId}/v{document.Version}";
            var documentDirectory = ResolvePath(locator);
            var declaredPath = documentDirectory is not null
                ? ResolvePath(Path.Combine(locator, document.FileName))
                : null;
            if (declaredPath is null || !File.Exists(declaredPath))
            {
                return $"The declared file '{document.FileName}' is not present in authorised input storage.";
            }

            await using var stream = File.OpenRead(declaredPath);
            var actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken));
            if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, document.Sha256))
            {
                return $"The declared file '{document.FileName}' does not match its SHA-256 hash.";
            }

            declaredFiles.Add(Path.GetFullPath(declaredPath));
        }

        foreach (var path in Directory.EnumerateFiles(caseDirectory, "*", SearchOption.AllDirectories))
        {
            if (!declaredFiles.Contains(Path.GetFullPath(path)))
            {
                return $"The input file '{Path.GetRelativePath(caseDirectory, path)}' is not declared by the package.";
            }
        }

        return null;
    }

    private string? ResolvePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        var rootWithSeparator = rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? rootDirectory
            : rootDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }
}
