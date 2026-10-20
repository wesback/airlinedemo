using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirlineDemo.Api.Fixtures;

public sealed record AircraftCasePackManifest
{
    public int SchemaVersion { get; init; }
    public string PackVersion { get; init; } = ""; public string ScenarioDate { get; init; } = "";
    public string Sha256 { get; init; } = ""; public string ProductOwnerReviewReference { get; init; } = "";
    public List<AircraftCasePackCase> Cases { get; init; } = []; public List<string> Files { get; init; } = [];
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public sealed record ParseResult(AircraftCasePackManifest? Manifest, IReadOnlyList<string> Errors);
    public static ParseResult Parse(string json, IReadOnlyDictionary<string, byte[]> fileContents)
    {
        AircraftCasePackManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AircraftCasePackManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new(null, [$"{(ex.Path is null or "$" ? "input" : ex.Path)}: malformed or invalid JSON"]);
        }
        if (manifest is null) return new(null, ["input: manifest must be a JSON object"]);
        var errors = Validate(manifest, fileContents);
        return new(errors.Count == 0 ? manifest : null, errors);
    }
    public static IReadOnlyList<string> Validate(AircraftCasePackManifest manifest, IReadOnlyDictionary<string, byte[]> fileContents)
    {
        var errors = new List<string>();
        if (manifest.SchemaVersion != 1) errors.Add("schemaVersion: must be integer 1");
        if (string.IsNullOrWhiteSpace(manifest.PackVersion)) errors.Add("packVersion: must be non-empty");
        else if (!Version.TryParse(manifest.PackVersion, out var packVersion) || packVersion < new Version(1, 0, 2))
            errors.Add("packVersion: fictional identity labels require version 1.0.2 or later");
        if (!StringComparer.Ordinal.Equals(manifest.ScenarioDate, "2026-10-01"))
            errors.Add("scenarioDate: must be 2026-10-01");
        if (!IsHash(manifest.Sha256)) errors.Add("sha256: must be 64 lowercase hexadecimal characters");
        if (string.IsNullOrWhiteSpace(manifest.ProductOwnerReviewReference)) errors.Add("productOwnerReviewReference: must be non-empty");
        var cases = manifest.Cases ?? [];
        if (cases.Count != 3) errors.Add("cases: must contain exactly 3 cases");
        var declaredFiles = manifest.Files ?? [];
        var paths = new HashSet<string>(declaredFiles, StringComparer.Ordinal);
        if (paths.Count != declaredFiles.Count) errors.Add("files: paths must be unique");
        for (var i = 0; i < declaredFiles.Count; i++) if (!IsRelativePath(declaredFiles[i])) errors.Add($"files[{i}]: must be a relative fixture path");
        if (!paths.SetEquals(fileContents.Keys)) errors.Add("files: declared paths must exactly match supplied file contents");
        var totalDocuments = 0;
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            var prefix = $"cases[{i}]";
            if (c is null) { errors.Add($"{prefix}: must be an object"); continue; }
            if (string.IsNullOrWhiteSpace(c.CaseId)) errors.Add($"{prefix}.caseId: must be non-empty");
            else if (c.CaseId is not ("cp-case-a" or "cp-case-b" or "cp-case-c"))
                errors.Add($"{prefix}.caseId: must be cp-case-a, cp-case-b, or cp-case-c");
            else if (!caseIds.Add(c.CaseId)) errors.Add($"{prefix}.caseId: must be unique");
            if (!c.Synthetic) errors.Add($"{prefix}.synthetic: must be true");
            if (string.IsNullOrWhiteSpace(c.GeneratorReference)) errors.Add($"{prefix}.generatorReference: must be non-empty");
            if (c.AircraftCount != 1) errors.Add($"{prefix}.aircraftCount: must be 1");
            if (c.EngineCount != 1) errors.Add($"{prefix}.engineCount: must be 1");
            var components = c.Components ?? [];
            var documents = c.Documents ?? [];
            if (c.Components is null) errors.Add($"{prefix}.components: must be an array");
            else if (components.Count != 2) errors.Add($"{prefix}.components: must contain exactly 2 components");
            if (c.Documents is null) errors.Add($"{prefix}.documents: must be an array");
            else if (documents.Count is < 8 or > 12) errors.Add($"{prefix}.documents: must contain 8 through 12 documents");
            totalDocuments += documents.Count;
            var documentPaths = new HashSet<string>(StringComparer.Ordinal);
            var componentIds = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < documents.Count; j++)
            {
                var d = documents[j];
                var docPrefix = $"{prefix}.documents[{j}]";
                if (d is null) { errors.Add($"{docPrefix}: must be an object"); continue; }
                if (string.IsNullOrWhiteSpace(d.Path)) errors.Add($"{docPrefix}.path: must be non-empty");
                else if (!documentPaths.Add(d.Path)) errors.Add($"{docPrefix}.path: must be unique in its case");
                if (!IsRelativePath(d.Path)) errors.Add($"{docPrefix}.path: must be a relative fixture path");
                if (!d.Synthetic) errors.Add($"{docPrefix}.synthetic: must be true"); if (string.IsNullOrWhiteSpace(d.GeneratorReference)) errors.Add($"{docPrefix}.generatorReference: must be non-empty");
                if (!paths.Contains(d.Path)) errors.Add($"{docPrefix}.path: must be listed in files");
            }
            for (var j = 0; j < components.Count; j++)
            {
                var component = components[j];
                if (component is null) { errors.Add($"{prefix}.components[{j}]: must be an object"); continue; }
                if (string.IsNullOrWhiteSpace(component.ComponentId))
                    errors.Add($"{prefix}.components[{j}].componentId: must be non-empty");
                else if (!componentIds.Add(component.ComponentId))
                    errors.Add($"{prefix}.components[{j}].componentId: must be unique in its case");
                if (component.EvidenceDocumentPaths is null ||
                    component.EvidenceDocumentPaths.Count == 0 ||
                    component.EvidenceDocumentPaths.Any(path => !documentPaths.Contains(path)))
                    errors.Add($"{prefix}.components[{j}].evidenceDocumentPaths: must map to this case's documents");
            }
            ValidateIdentity(c, components, componentIds, prefix, errors);
        }
        if (totalDocuments is < 24 or > 36) errors.Add("cases.documents: total must be 24 through 36");
        if (errors.Count == 0 && IsHash(manifest.Sha256))
        {
            var expected = ComputeSha256(manifest, fileContents);
            if (!StringComparer.Ordinal.Equals(expected, manifest.Sha256)) errors.Add("sha256: does not match manifest and file contents");
        }
        return errors;
    }
    public static IReadOnlyList<string> Compare(AircraftCasePackManifest previous, AircraftCasePackManifest current,
        IReadOnlyDictionary<string, byte[]> previousFiles, IReadOnlyDictionary<string, byte[]> currentFiles)
    {
        var previousErrors = Validate(previous, previousFiles);
        var currentErrors = Validate(current, currentFiles);
        var errors = previousErrors.Select(e => $"previous {e}").Concat(currentErrors.Select(e => $"current {e}")).ToList();
        var changed = previousErrors.Count == 0 && currentErrors.Count == 0 &&
            !StringComparer.Ordinal.Equals(ComputeSha256(previous with { PackVersion = "" }, previousFiles),
                ComputeSha256(current with { PackVersion = "" }, currentFiles));
        if (changed && StringComparer.Ordinal.Equals(previous.PackVersion, current.PackVersion))
            errors.Add("packVersion: must change when canonical content changes");
        return errors;
    }
    public static string ComputeSha256(AircraftCasePackManifest manifest, IReadOnlyDictionary<string, byte[]> fileContents)
    {
        var canonical = new
        {
            schemaVersion = manifest.SchemaVersion, packVersion = manifest.PackVersion,
            scenarioDate = manifest.ScenarioDate, productOwnerReviewReference = manifest.ProductOwnerReviewReference,
            cases = manifest.Cases.OrderBy(c => c.CaseId, StringComparer.Ordinal).Select(c => new
            {
                caseId = c.CaseId,
                synthetic = c.Synthetic,
                generatorReference = c.GeneratorReference,
                aircraftCount = c.AircraftCount,
                engineCount = c.EngineCount,
                components = c.Components.OrderBy(x => x.ComponentId, StringComparer.Ordinal).Select(x =>
                    new { componentId = x.ComponentId, evidenceDocumentPaths = x.EvidenceDocumentPaths.Order(StringComparer.Ordinal) }),
                documents = c.Documents.OrderBy(x => x.Path, StringComparer.Ordinal).Select(x =>
                    new { path = x.Path, synthetic = x.Synthetic, generatorReference = x.GeneratorReference }),
                aircraftRegistration = c.AircraftRegistration is null ? null : new
                {
                    value = c.AircraftRegistration.Value,
                    synthetic = c.AircraftRegistration.Synthetic
                },
                serialNumbers = c.SerialNumbers!.OrderBy(x => x.Kind, StringComparer.Ordinal)
                    .ThenBy(x => x.ComponentId, StringComparer.Ordinal).Select(x => new
                    {
                        kind = x.Kind,
                        value = x.Value,
                        synthetic = x.Synthetic,
                        componentId = x.ComponentId
                    })
            }),
            files = manifest.Files.Order(StringComparer.Ordinal).Select(path =>
                new { path, sha256 = Hex(SHA256.HashData(fileContents[path])) })
        };
        return Hex(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }
    private static void ValidateIdentity(AircraftCasePackCase c, IReadOnlyList<AircraftCasePackComponent> components,
        IReadOnlySet<string> componentIds, string prefix, List<string> errors)
    {
        var caseLetter = c.CaseId switch
        {
            "cp-case-a" => "A",
            "cp-case-b" => "B",
            "cp-case-c" => "C",
            _ => null
        };
        var labelPrefix = caseLetter is null ? null : $"SYN-CP-{caseLetter}";
        var registration = c.AircraftRegistration;
        if (registration is null)
            errors.Add($"{prefix}.aircraftRegistration: must be an object");
        else
        {
            ValidateLabel(registration.Value, registration.Synthetic,
                $"{prefix}.aircraftRegistration", labelPrefix is null ? null : $"{labelPrefix}-REG", errors);
        }

        var serials = c.SerialNumbers;
        if (serials is null)
        {
            errors.Add($"{prefix}.serialNumbers: must contain exactly 4 records");
            return;
        }
        if (serials.Count != 4) errors.Add($"{prefix}.serialNumbers: must contain exactly 4 records");
        if (serials.Count(s => s?.Kind == "aircraft") != 1)
            errors.Add($"{prefix}.serialNumbers: must contain exactly one aircraft serial");
        if (serials.Count(s => s?.Kind == "engine") != 1)
            errors.Add($"{prefix}.serialNumbers: must contain exactly one engine serial");
        if (serials.Count(s => s?.Kind == "component") != 2)
            errors.Add($"{prefix}.serialNumbers: must contain exactly two component serials");

        var values = new HashSet<string>(StringComparer.Ordinal);
        if (registration is not null && !string.IsNullOrWhiteSpace(registration.Value))
            values.Add(registration.Value);
        var componentSerialIds = new HashSet<string>(StringComparer.Ordinal);
        var expectedComponentSerials = labelPrefix is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : components.Where(component => component is not null && !string.IsNullOrWhiteSpace(component.ComponentId))
                .GroupBy(component => component.ComponentId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(component => component.ComponentId, StringComparer.Ordinal)
                .Select((component, index) => (component.ComponentId, Value: $"{labelPrefix}-CMP-{index + 1:000}"))
                .ToDictionary(item => item.ComponentId, item => item.Value, StringComparer.Ordinal);

        for (var i = 0; i < serials.Count; i++)
        {
            var serial = serials[i];
            var serialPrefix = $"{prefix}.serialNumbers[{i}]";
            if (serial is null)
            {
                errors.Add($"{serialPrefix}: must be an object");
                continue;
            }
            if (serial.Kind is not ("aircraft" or "engine" or "component"))
                errors.Add($"{serialPrefix}.kind: must be aircraft, engine, or component");
            if (string.IsNullOrWhiteSpace(serial.Value))
                errors.Add($"{serialPrefix}.value: must be non-empty");
            else
            {
                if (!serial.Value.StartsWith("SYN-CP-", StringComparison.Ordinal))
                    errors.Add($"{serialPrefix}.value: must start with SYN-CP-");
                if (!values.Add(serial.Value))
                    errors.Add($"{serialPrefix}.value: must be unique in its case");
            }
            if (!serial.Synthetic) errors.Add($"{serialPrefix}.synthetic: must be true");

            string? expectedValue = serial.Kind switch
            {
                "aircraft" when labelPrefix is not null => $"{labelPrefix}-AIR-001",
                "engine" when labelPrefix is not null => $"{labelPrefix}-ENG-001",
                "component" when serial.ComponentId is not null &&
                    expectedComponentSerials.TryGetValue(serial.ComponentId, out var value) => value,
                _ => null
            };
            if (expectedValue is not null && !StringComparer.Ordinal.Equals(serial.Value, expectedValue))
                errors.Add($"{serialPrefix}.value: does not match the fictional identity for this case");

            if (serial.Kind == "component")
            {
                if (string.IsNullOrWhiteSpace(serial.ComponentId) || !componentIds.Contains(serial.ComponentId))
                    errors.Add($"{serialPrefix}.componentId: must identify a component in this case");
                else if (!componentSerialIds.Add(serial.ComponentId))
                    errors.Add($"{serialPrefix}.componentId: must be unique in its case");
            }
            else if (serial.ComponentId is not null)
                errors.Add($"{serialPrefix}.componentId: is only valid for a component serial");
        }
        if (!componentSerialIds.SetEquals(componentIds))
            errors.Add($"{prefix}.serialNumbers: component serials must map to each case component");
    }

    private static void ValidateLabel(string value, bool synthetic, string prefix, string? expectedValue, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{prefix}.value: must be non-empty");
        else
        {
            if (!value.StartsWith("SYN-CP-", StringComparison.Ordinal))
                errors.Add($"{prefix}.value: must start with SYN-CP-");
            if (expectedValue is not null && !StringComparer.Ordinal.Equals(value, expectedValue))
                errors.Add($"{prefix}.value: does not match the fictional identity for this case");
        }
        if (!synthetic) errors.Add($"{prefix}.synthetic: must be true");
    }

    private static bool IsHash(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static bool IsRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') && !path.Contains('\\') &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
}
public sealed record AircraftCasePackCase(string CaseId = "", bool Synthetic = false, string GeneratorReference = "",
    int AircraftCount = 0, int EngineCount = 0, List<AircraftCasePackComponent>? Components = null,
    List<AircraftCasePackDocument>? Documents = null,
    AircraftCasePackRegistration? AircraftRegistration = null,
    IReadOnlyList<AircraftCasePackSerialNumber>? SerialNumbers = null);
public sealed record AircraftCasePackComponent(string ComponentId = "", List<string>? EvidenceDocumentPaths = null);
public sealed record AircraftCasePackDocument(string Path = "", bool Synthetic = false, string GeneratorReference = "");
public sealed record AircraftCasePackRegistration(string Value = "", bool Synthetic = false);
public sealed record AircraftCasePackSerialNumber(
    string Kind = "",
    string Value = "",
    bool Synthetic = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComponentId = null);
