using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AirlineDemo.Api;

public interface IPageInventoryStorage
{
    Task<IReadOnlyList<int>> GetPageInventoryAsync(
        CaseContext context,
        DocumentMetadata document,
        CancellationToken cancellationToken);
}

public interface IApprovedRequirementStorage
{
    Task<IReadOnlyList<ApprovedRequirementVersion>> GetApprovedRequirementVersionsAsync(
        CaseContext context,
        CancellationToken cancellationToken);
}

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

public interface ISelectedManifestStorage
{
    Task<string?> GetSelectedManifestSha256Async(
        CaseContext context,
        CancellationToken cancellationToken);
}

public sealed class FileDocumentStorage :
    IDocumentStorage,
    IManifestDocumentStorage,
    ISelectedManifestStorage,
    IPageInventoryStorage,
    IApprovedRequirementStorage
{
    private readonly string rootDirectory;
    private readonly IReadOnlyList<SelectedFixtureEntry>? selectedFixtureEntries;
    private readonly string? selectedFixtureManifestError;

    public FileDocumentStorage(string rootDirectory)
    {
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(this.rootDirectory);
        (selectedFixtureEntries, selectedFixtureManifestError) =
            LoadSelectedFixtureManifest(this.rootDirectory);
    }

    public async Task<string?> ReadPagePreviewAsync(
        string storageLocator,
        int page,
        CancellationToken cancellationToken)
    {
        var documentDirectory = ResolveStoragePath(storageLocator);
        if (page < 1 || documentDirectory is null)
        {
            return null;
        }

        if (File.Exists(documentDirectory))
        {
            if (page != 1)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(documentDirectory, cancellationToken);
            ValidatePdfDocument(bytes);
            return ExtractPdfText(bytes);
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
        if (selectedFixtureManifestError is not null)
        {
            return selectedFixtureManifestError;
        }

        if (selectedFixtureEntries is not null)
        {
            return await ValidateSelectedFixtureManifestAsync(
                context,
                manifest,
                selectedFixtureEntries,
                cancellationToken);
        }

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

    public async Task<string?> GetSelectedManifestSha256Async(
        CaseContext context,
        CancellationToken cancellationToken)
    {
        if (selectedFixtureEntries is null)
        {
            return null;
        }

        var selectedDirectories = selectedFixtureEntries
            .Where(entry =>
                entry.Scope.RunId == context.RunId &&
                (string.IsNullOrEmpty(context.CaseId) || entry.Scope.Matches(context)))
            .Select(entry => Path.GetDirectoryName(entry.RelativePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var directory in selectedDirectories)
        {
            var manifestPath = ResolveSelectedPath(Path.Combine(directory!, "manifest.json"));
            if (manifestPath is null || !File.Exists(manifestPath))
            {
                continue;
            }

            await using var stream = File.OpenRead(manifestPath);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }

        return null;
    }

    public async Task<IReadOnlyList<int>> GetPageInventoryAsync(
        CaseContext context,
        DocumentMetadata document,
        CancellationToken cancellationToken)
    {
        var locator = $"{context.RunId}/{context.CaseId}/{document.DocumentId}/v{document.Version}";
        var documentDirectory = ResolveStoragePath(locator);
        if (documentDirectory is null)
        {
            return [];
        }

        if (File.Exists(documentDirectory))
        {
            if (selectedFixtureEntries is not null)
            {
                await ValidatePdfDocumentAsync(documentDirectory, cancellationToken);
            }

            return [1];
        }

        var pageFiles = Directory.Exists(documentDirectory)
            ? Directory.EnumerateFiles(documentDirectory, "page-*.txt")
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Select(name => name["page-".Length..])
                .Where(value => int.TryParse(value, out _))
                .Select(int.Parse)
                .OrderBy(page => page)
                .ToArray()
            : [];
        if (pageFiles.Length > 0)
        {
            return pageFiles;
        }

        IReadOnlyList<int> pages = File.Exists(Path.Combine(documentDirectory, document.FileName))
            ? [1]
            : [];
        return pages;
    }

    private static async Task ValidatePdfDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        ValidatePdfDocument(bytes);
    }

    private static void ValidatePdfDocument(byte[] bytes)
    {
        var content = System.Text.Encoding.ASCII.GetString(bytes);
        if (!content.StartsWith("%PDF-", StringComparison.Ordinal) ||
            !content.Contains("%%EOF", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The declared document is not a readable PDF.");
        }
    }

    private static string ExtractPdfText(byte[] bytes)
    {
        var content = Encoding.ASCII.GetString(bytes);
        var lines = Regex.Matches(
                content,
                @"\((?<text>(?:\\.|[^\\)])*)\)\s*Tj",
                RegexOptions.CultureInvariant)
            .Select(match => UnescapePdfString(match.Groups["text"].Value));
        return string.Join('\n', lines);
    }

    private static string UnescapePdfString(string value) =>
        value
            .Replace("\\(", "(", StringComparison.Ordinal)
            .Replace("\\)", ")", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);

    public async Task<IReadOnlyList<ApprovedRequirementVersion>> GetApprovedRequirementVersionsAsync(
        CaseContext context,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath("application-inputs/reference-data/requirements.json");
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        var requirements = await JsonSerializer.DeserializeAsync<List<ApprovedRequirementVersion>>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken: cancellationToken);
        return requirements?
            .Where(requirement => !string.IsNullOrWhiteSpace(requirement.RequirementId) &&
                requirement.Version > 0)
            .Select(requirement => new ApprovedRequirementVersion(
                requirement.RequirementId,
                requirement.Version,
                requirement.ComponentId))
            .ToArray()
            ?? [];
    }

    private async Task<string?> ValidateSelectedFixtureManifestAsync(
        CaseContext context,
        IReadOnlyList<DocumentMetadata> manifest,
        IReadOnlyList<SelectedFixtureEntry> selectedEntries,
        CancellationToken cancellationToken)
    {
        var scopedEntries = selectedEntries
            .Where(entry => entry.Scope.Matches(context))
            .ToArray();
        if (scopedEntries.Length != selectedEntries.Count)
        {
            return "The selected initial input manifest is outside the requested case scope.";
        }

        var duplicateEntry = selectedEntries
            .GroupBy(entry => $"{entry.DocumentId}:{entry.Version}", StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateEntry is not null)
        {
            return "The selected initial input manifest contains duplicate documents.";
        }

        var duplicatePath = selectedEntries
            .GroupBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePath is not null)
        {
            return "The selected initial input manifest contains duplicate paths.";
        }

        var selectedByDocument = selectedEntries.ToDictionary(
            entry => $"{entry.DocumentId}:{entry.Version}",
            StringComparer.Ordinal);
        var packageKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in manifest)
        {
            if (document is null)
            {
                return "The package manifest contains a null document.";
            }

            var key = $"{document.DocumentId}:{document.Version}";
            if (!packageKeys.Add(key) ||
                !selectedByDocument.TryGetValue(key, out var selectedEntry))
            {
                return $"The document '{document.DocumentId}' is not declared by the selected initial input manifest.";
            }

            if (!StringComparer.Ordinal.Equals(
                    Path.GetFileName(selectedEntry.RelativePath),
                    document.FileName) ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    selectedEntry.Sha256,
                    document.Sha256))
            {
                return $"The declared file '{document.FileName}' does not match the selected initial input manifest.";
            }

            var selectedPath = ResolveSelectedPath(selectedEntry.RelativePath);
            if (selectedPath is null || Directory.Exists(selectedPath))
            {
                return $"The selected input '{selectedEntry.RelativePath}' is not a file.";
            }

            if (!File.Exists(selectedPath))
            {
                return $"The declared file '{document.FileName}' is not present in authorised input storage.";
            }

            await using var stream = File.OpenRead(selectedPath);
            var actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken));
            if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, selectedEntry.Sha256))
            {
                return $"The selected input '{selectedEntry.RelativePath}' does not match its SHA-256 hash.";
            }
        }

        if (packageKeys.Count != selectedEntries.Count)
        {
            return "The package manifest does not declare every selected initial input.";
        }

        var packageDirectories = selectedEntries
            .Select(entry => Path.GetDirectoryName(entry.RelativePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var declaredPaths = selectedEntries
            .Select(entry => ResolveSelectedPath(entry.RelativePath))
            .Where(path => path is not null)
            .Select(path => Path.GetFullPath(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in packageDirectories)
        {
            var packageDirectory = ResolveSelectedPath(directory!);
            if (packageDirectory is null || !Directory.Exists(packageDirectory))
            {
                return $"The selected input directory '{directory}' does not exist.";
            }

            if (Directory.EnumerateDirectories(
                    packageDirectory,
                    "*",
                    SearchOption.AllDirectories).Any())
            {
                return "Recursive directory loading is not permitted for selected package inputs.";
            }

            foreach (var path in Directory.EnumerateFiles(
                         packageDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                if (Path.GetFileName(path).Equals("manifest.json", StringComparison.OrdinalIgnoreCase) &&
                    StringComparer.OrdinalIgnoreCase.Equals(
                        Path.GetDirectoryName(path),
                        packageDirectory))
                {
                    continue;
                }

                if (!declaredPaths.Contains(Path.GetFullPath(path)))
                {
                    return $"The input file '{Path.GetRelativePath(packageDirectory, path)}' is not declared by the selected initial input manifest.";
                }
            }
        }

        return null;
    }

    private string? ResolveStoragePath(string storageLocator)
    {
        if (selectedFixtureEntries is null)
        {
            return ResolvePath(storageLocator);
        }

        var parts = storageLocator.Split(
            new[] { '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 ||
            !int.TryParse(parts[3].TrimStart('v'), out var version))
        {
            return null;
        }

        var entry = selectedFixtureEntries.SingleOrDefault(candidate =>
            candidate.Scope.RunId == parts[0] &&
            candidate.Scope.CaseId == parts[1] &&
            candidate.DocumentId == parts[2] &&
            candidate.Version == version);
        return entry is null ? null : ResolveSelectedPath(entry.RelativePath);
    }

    private string? ResolveSelectedPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(relativePath) ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or "..") ||
            !normalized.StartsWith("application-inputs/", StringComparison.Ordinal))
        {
            return null;
        }

        return ResolvePath(normalized.Replace('/', Path.DirectorySeparatorChar));
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

    private static (IReadOnlyList<SelectedFixtureEntry>? Entries, string? Error)
        LoadSelectedFixtureManifest(string rootDirectory)
    {
        var contractPath = Path.Combine(rootDirectory, "generator-contract.json");
        if (!File.Exists(contractPath))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(contractPath));
            var entries = document.RootElement
                .GetProperty("pathBoundaries")
                .GetProperty("selectedInitialInputManifest")
                .GetProperty("entries")
                .EnumerateArray()
                .Select(ParseSelectedFixtureEntry)
                .ToArray();
            return (entries, null);
        }
        catch (Exception exception) when (
            exception is JsonException or
            KeyNotFoundException or
            InvalidOperationException or
            FormatException or
            InvalidDataException)
        {
            return (null, "The selected initial input manifest is invalid.");
        }
    }

    private static SelectedFixtureEntry ParseSelectedFixtureEntry(JsonElement value)
    {
        var scope = value.GetProperty("scope");
        return new SelectedFixtureEntry(
            value.GetProperty("relativePath").GetString()
                ?? throw new InvalidDataException(),
            value.GetProperty("documentId").GetString()
                ?? throw new InvalidDataException(),
            value.GetProperty("version").GetInt32(),
            value.GetProperty("sha256").GetString()
                ?? throw new InvalidDataException(),
            new CaseContext(
                scope.GetProperty("runId").GetString() ?? throw new InvalidDataException(),
                scope.GetProperty("caseId").GetString() ?? throw new InvalidDataException(),
                scope.GetProperty("airlineId").GetString() ?? throw new InvalidDataException(),
                scope.GetProperty("aircraftId").GetString() ?? throw new InvalidDataException(),
                scope.GetProperty("leaseId").GetString() ?? throw new InvalidDataException()));
    }

    private sealed record SelectedFixtureEntry(
        string RelativePath,
        string DocumentId,
        int Version,
        string Sha256,
        CaseContext Scope);
}

internal static class CaseContextExtensions
{
    public static bool Matches(this CaseContext left, CaseContext right) =>
        left.RunId == right.RunId &&
        left.CaseId == right.CaseId &&
        left.AirlineId == right.AirlineId &&
        left.AircraftId == right.AircraftId &&
        left.LeaseId == right.LeaseId;
}
