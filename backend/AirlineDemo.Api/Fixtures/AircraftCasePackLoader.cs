using System.Collections.ObjectModel;
using System.Text.Json;

namespace AirlineDemo.Api.Fixtures;

public sealed class AircraftCasePackValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = Array.AsReadOnly(errors.ToArray());
}

public sealed class LoadedAircraftCasePack
{
    private readonly AircraftCasePackManifest _manifest;
    private readonly IReadOnlyDictionary<string, byte[]> _files;
    internal LoadedAircraftCasePack(AircraftCasePackManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        _manifest = CopyManifest(manifest);
        _files = new ReadOnlyDictionary<string, byte[]>(files.ToDictionary(
            pair => pair.Key, pair => (byte[])pair.Value.Clone(), StringComparer.Ordinal));
    }

    public AircraftCasePackManifest Manifest => CopyManifest(_manifest);
    public byte[] ReadFile(string path) => (byte[])_files[path].Clone();
    internal IReadOnlyDictionary<string, byte[]> Files => _files;
    internal AircraftCasePackManifest ValidatedManifest => _manifest;
    internal static AircraftCasePackManifest CopyManifest(AircraftCasePackManifest manifest) => manifest with
    {
        Cases = manifest.Cases.Select(item => item with
        {
            Components = item.Components?.Select(component => component with { EvidenceDocumentPaths = component.EvidenceDocumentPaths?.ToList() }).ToList(),
            Documents = item.Documents?.ToList()
        }).ToList(),
        Files = manifest.Files.ToList()
    };
}

public sealed class AircraftCasePackLoader(string fixtureDirectory)
{
    public LoadedAircraftCasePack Load()
    {
        var manifestJson = File.ReadAllText(Path.Combine(fixtureDirectory, "manifest.json"));
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var missingFiles = new List<string>();
        try
        {
            using var json = JsonDocument.Parse(manifestJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return Load(manifestJson, contents);
            if (json.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                var root = Path.GetFullPath(fixtureDirectory) + Path.DirectorySeparatorChar;
                foreach (var item in files.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var relativePath = item.GetString()!;
                    if (!IsRelativePath(relativePath)) continue;
                    var path = Path.GetFullPath(Path.Combine(fixtureDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(root, StringComparison.Ordinal)) continue;
                    if (!File.Exists(path))
                    {
                        missingFiles.Add(relativePath);
                        continue;
                    }
                    if (!contents.ContainsKey(relativePath)) contents.Add(relativePath, File.ReadAllBytes(path));
                }
            }
        }
        catch (JsonException)
        {
            return Load(manifestJson, contents);
        }

        var result = AircraftCasePackManifest.Parse(manifestJson, contents);
        var errors = result.Errors.Concat(missingFiles.Select(path => $"{path}: fixture file does not exist")).ToArray();
        if (errors.Length > 0)
        {
            throw new AircraftCasePackValidationException(errors);
        }

        return new LoadedAircraftCasePack(result.Manifest!, contents);
    }
    public LoadedAircraftCasePack Load(string manifestJson, IReadOnlyDictionary<string, byte[]> fileContents)
    {
        var result = AircraftCasePackManifest.Parse(manifestJson, fileContents);
        if (result.Manifest is null)
        {
            throw new AircraftCasePackValidationException(result.Errors);
        }

        return new LoadedAircraftCasePack(result.Manifest, fileContents);
    }
    private static bool IsRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') && !path.Contains('\\') &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
}
