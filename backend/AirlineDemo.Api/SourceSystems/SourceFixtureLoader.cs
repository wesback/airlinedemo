using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.SourceSystems;

// Raised when a source fixture fails verification; the source must not be registered.
public sealed class SourceFixtureRefusedException(string message) : InvalidOperationException(message);

public sealed record VerifiedSourceFixture(string SourceId, SourceFixtureManifest Manifest, byte[] FixtureBytes);

public enum SourceFixturePublication
{
    Published,
    AlreadyPublished,
}

public sealed record StoredFixturePack(string Version, string Digest);

public sealed record StoredSourceRecord(string SourceId, string Version, int Ordinal, string CaseId);

public sealed record StoredBasisPin(string SourceId, string Version, string Sha256);

public sealed record StoredAuditEvent(int Sequence, string Action, string Version);

public sealed record StoredCaseDatum(string CaseId, string Value);

// Every collection is replaced as a whole on publication and never edited in place.
public sealed record PublishedFixtureState(
    IReadOnlyList<StoredFixturePack> Packs,
    IReadOnlyList<StoredSourceRecord> SourceRecords,
    IReadOnlyList<StoredBasisPin> BasisPins,
    IReadOnlyList<StoredAuditEvent> AuditEvents,
    IReadOnlyList<StoredCaseDatum> CaseData)
{
    public static PublishedFixtureState Empty { get; } = new([], [], [], [], []);
}

public sealed class SourceFixtureStore(PublishedFixtureState initial)
{
    private PublishedFixtureState state = initial;

    // Serializes read-stage-commit so concurrent publications cannot build from the same snapshot and lose each other's version.
    internal object PublishLock { get; } = new();

    public PublishedFixtureState State => state;

    // A single reference swap: readers observe either the previous complete state or the next one.
    internal void Commit(PublishedFixtureState next) => state = next;
}

// Refuse-on-mismatch loader for the five source fixtures (PRD FR-002, AC-004, AC-005).
// The manifest digest detects drift but does not establish authenticity.
public static class SourceFixtureLoader
{
    private const string ScenarioDate = "2026-10-01";

    private static readonly JsonSerializerOptions ManifestOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyDictionary<string, VerifiedSourceFixture> Load(
        string directory, ILogger? logger = null, string environment = "production")
    {
        var diagnosticStartedAt = Stopwatch.GetTimestamp();
        try
        {
            return LoadCore(directory);
        }
        catch
        {
            if (logger is not null)
                SourceSystemDiagnostics.FixtureRejected(logger, environment,
                    SourceSystemDiagnostics.ElapsedMilliseconds(diagnosticStartedAt));
            throw;
        }
    }

    private static IReadOnlyDictionary<string, VerifiedSourceFixture> LoadCore(string directory)
    {
        var verified = new Dictionary<string, VerifiedSourceFixture>(StringComparer.Ordinal);
        foreach (var source in SourceFixtureManifest.RequiredSources)
        {
            verified[source] = LoadSource(directory, source);
        }

        return verified;
    }

    private const string PublishAction = "source-fixtures.published";

    // Verifies the complete set, then publishes it with one commit. A refusal or a failure before the commit leaves the store unchanged.
    // beforeCommit is the publication test seam: it runs on the staged state and may throw to simulate a publication failure.
    internal static SourceFixturePublication LoadAndPublish(
        string directory,
        SourceFixtureStore store,
        Action<PublishedFixtureState>? beforeCommit = null,
        ILogger? logger = null,
        string environment = "production")
    {
        var diagnosticStartedAt = Stopwatch.GetTimestamp();
        try
        {
            return LoadAndPublishCore(directory, store, beforeCommit);
        }
        catch
        {
            if (logger is not null)
                SourceSystemDiagnostics.FixtureRejected(logger, environment,
                    SourceSystemDiagnostics.ElapsedMilliseconds(diagnosticStartedAt));
            throw;
        }
    }

    private static SourceFixturePublication LoadAndPublishCore(
        string directory,
        SourceFixtureStore store,
        Action<PublishedFixtureState>? beforeCommit)
    {
        var verified = LoadCore(directory);
        var versions = verified.Values.Select(fixture => fixture.Manifest.SourceFixtureVersion).Distinct(StringComparer.Ordinal).ToList();
        if (versions.Count != 1)
        {
            throw new SourceFixtureRefusedException("Source fixtures must share one sourceFixtureVersion; publication is refused.");
        }

        var version = versions[0];
        var digest = PackDigest(verified);

        lock (store.PublishLock)
        {
            var current = store.State;

            var stored = current.Packs.FirstOrDefault(pack => string.Equals(pack.Version, version, StringComparison.Ordinal));
            if (stored is not null)
            {
                if (string.Equals(stored.Digest, digest, StringComparison.Ordinal))
                {
                    return SourceFixturePublication.AlreadyPublished;
                }

                throw new SourceFixtureRefusedException($"Source fixture version '{version}' is already stored with different content; publication is refused.");
            }

            var next = Stage(current, verified, version, digest);
            beforeCommit?.Invoke(next);
            store.Commit(next);
            return SourceFixturePublication.Published;
        }
    }

    private static string PackDigest(IReadOnlyDictionary<string, VerifiedSourceFixture> verified)
    {
        var lines = SourceFixtureManifest.RequiredSources
            .Select(source => $"{source} {verified[source].Manifest.Sha256.ToLowerInvariant()}\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines)))).ToLowerInvariant();
    }

    private static PublishedFixtureState Stage(
        PublishedFixtureState current,
        IReadOnlyDictionary<string, VerifiedSourceFixture> verified,
        string version,
        string digest)
    {
        var packs = new List<StoredFixturePack>(current.Packs) { new StoredFixturePack(version, digest) };
        var records = new List<StoredSourceRecord>(current.SourceRecords);
        var pins = new List<StoredBasisPin>(current.BasisPins);
        var audits = new List<StoredAuditEvent>(current.AuditEvents);

        foreach (var source in SourceFixtureManifest.RequiredSources)
        {
            var fixture = verified[source];
            var caseIds = ReadRecordCaseIds(source, fixture.FixtureBytes);
            for (var ordinal = 0; ordinal < caseIds.Count; ordinal++)
            {
                records.Add(new StoredSourceRecord(source, version, ordinal, caseIds[ordinal]));
            }

            pins.Add(new StoredBasisPin(source, version, fixture.Manifest.Sha256.ToLowerInvariant()));
        }

        audits.Add(new StoredAuditEvent(audits.Count + 1, PublishAction, version));
        return new PublishedFixtureState(packs, records, pins, audits, current.CaseData);
    }

    private static VerifiedSourceFixture LoadSource(string directory, string source)
    {
        var fixtureBytes = ReadRequired(directory, $"{source}.json", source, "fixture");
        var manifestBytes = ReadRequired(directory, $"{source}.manifest.json", source, "manifest");
        var (manifest, owningCaseIds) = ParseManifest(source, manifestBytes);

        var problems = new List<string>(manifest.Validate());
        if (!string.Equals(manifest.ScenarioDate, ScenarioDate, StringComparison.Ordinal))
        {
            problems.Add($"scenarioDate must be {ScenarioDate}.");
        }

        if (problems.Count > 0)
        {
            throw Refuse(source, string.Join(" ", problems));
        }

        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fixtureBytes));
        if (!string.Equals(digest, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse(source, "fixture SHA-256 digest does not match the manifest.");
        }

        var recordCaseIds = ReadRecordCaseIds(source, fixtureBytes);
        if (recordCaseIds.Count != manifest.RecordCounts[source])
        {
            throw Refuse(source, $"declared record count {manifest.RecordCounts[source]} does not match {recordCaseIds.Count} records.");
        }

        foreach (var caseId in recordCaseIds)
        {
            if (!owningCaseIds.Contains(caseId, StringComparer.Ordinal))
            {
                throw Refuse(source, $"record caseId '{caseId}' is not a declared owning case.");
            }
        }

        return new VerifiedSourceFixture(source, manifest, fixtureBytes);
    }

    private static (SourceFixtureManifest Manifest, IReadOnlyList<string> OwningCaseIds) ParseManifest(string source, byte[] manifestBytes)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<SourceFixtureManifest>(manifestBytes, ManifestOptions)
                ?? throw Refuse(source, "manifest is empty.");

            using var document = JsonDocument.Parse(manifestBytes);
            if (!document.RootElement.TryGetProperty("owningCaseIds", out var owning) || owning.ValueKind != JsonValueKind.Array)
            {
                throw Refuse(source, "manifest owningCaseIds is missing.");
            }

            var owningCaseIds = owning.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty)
                .ToList();
            if (owningCaseIds.Count == 0 || owningCaseIds.Any(string.IsNullOrWhiteSpace))
            {
                throw Refuse(source, "manifest owningCaseIds must name at least one case.");
            }

            return (manifest, owningCaseIds);
        }
        catch (JsonException)
        {
            throw Refuse(source, "manifest is not valid JSON.");
        }
    }

    private static List<string> ReadRecordCaseIds(string source, byte[] fixtureBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(fixtureBytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("sourceId", out var sourceId)
                || sourceId.ValueKind != JsonValueKind.String
                || sourceId.GetString() != source)
            {
                throw Refuse(source, "fixture sourceId does not match the source.");
            }

            if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            {
                throw Refuse(source, "fixture records array is missing.");
            }

            var caseIds = new List<string>();
            foreach (var record in records.EnumerateArray())
            {
                if (!record.TryGetProperty("caseId", out var caseId) || caseId.ValueKind != JsonValueKind.String)
                {
                    throw Refuse(source, "a fixture record has no caseId.");
                }

                if (source == "records")
                {
                    if (!record.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                        || !payload.TryGetProperty("containsPersonalData", out var marker)
                        || marker.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw Refuse(source, "every record payload must contain containsPersonalData as a JSON boolean.");
                    }
                }

                caseIds.Add(caseId.GetString()!);
            }

            return caseIds;
        }
        catch (JsonException)
        {
            throw Refuse(source, "fixture is not valid JSON.");
        }
    }

    private static byte[] ReadRequired(string directory, string fileName, string source, string kind)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw Refuse(source, $"{kind} file '{fileName}' is missing.");
        }

        return File.ReadAllBytes(path);
    }

    private static SourceFixtureRefusedException Refuse(string source, string reason) =>
        new($"Source fixture '{source}' refused: {reason}");
}
