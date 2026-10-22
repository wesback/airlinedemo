using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AirlineDemo.Api.Fixtures;
using AirlineDemo.Api.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.Persistence;

public sealed record AircraftCasePackDocumentReference(string Path, string BlobName, string Sha256, int Version);
public sealed record PublishedAircraftCasePackComponent(string ComponentId, IReadOnlyList<string> EvidenceDocumentPaths);
public sealed record PublishedAircraftCasePackDocument(string Path, bool Synthetic, string GeneratorReference, AircraftCasePackDocumentReference Evidence);
public sealed record PublishedAircraftCasePackCase(
    string CaseId,
    bool Synthetic,
    string GeneratorReference,
    int AircraftCount,
    int EngineCount,
    IReadOnlyList<PublishedAircraftCasePackComponent> Components,
    IReadOnlyList<PublishedAircraftCasePackDocument> Documents,
    IReadOnlyList<AircraftCasePackDocumentReference> DocumentReferences);

public sealed class AircraftCasePackSnapshot
{
    internal AircraftCasePackSnapshot(
        long revision,
        AircraftCasePackManifest manifest,
        IReadOnlyDictionary<string, byte[]> files,
        IReadOnlyList<PublishedAircraftCasePackCase> cases)
    {
        Revision = revision;
        PackVersion = manifest.PackVersion;
        Sha256 = manifest.Sha256;
        Manifest = LoadedAircraftCasePack.CopyManifest(manifest);
        Files = new ReadOnlyDictionary<string, byte[]>(files.ToDictionary(
            pair => pair.Key, pair => (byte[])pair.Value.Clone(), StringComparer.Ordinal));
        Cases = Array.AsReadOnly(cases.ToArray());
    }

    public long Revision { get; }
    public string PackVersion { get; }
    public string Sha256 { get; }
    public IReadOnlyList<PublishedAircraftCasePackCase> Cases { get; }
    internal AircraftCasePackManifest Manifest { get; }
    internal IReadOnlyDictionary<string, byte[]> Files { get; }
}

public interface IAircraftCasePackSnapshotStore
{
    AircraftCasePackSnapshot? Read();
    bool TryCommit(long expectedRevision, AircraftCasePackSnapshot snapshot);
}
public sealed class InMemoryAircraftCasePackSnapshotStore : IAircraftCasePackSnapshotStore
{
    private readonly object _gate = new();
    private AircraftCasePackSnapshot? _snapshot;

    public AircraftCasePackSnapshot? Read() => Volatile.Read(ref _snapshot);

    public bool TryCommit(long expectedRevision, AircraftCasePackSnapshot snapshot)
    {
        lock (_gate)
        {
            var currentRevision = _snapshot?.Revision ?? 0;
            if (currentRevision != expectedRevision) return false;
            if (snapshot.Revision != expectedRevision + 1)
                throw new ArgumentException("Pack snapshot revision must be the expected revision plus one.", nameof(snapshot));
            Volatile.Write(ref _snapshot, snapshot);
            return true;
        }
    }
}

public interface IAircraftCasePackDocumentReferenceStager
{
    IReadOnlyDictionary<string, AircraftCasePackDocumentReference> Stage(
        AircraftCasePackCase record,
        IReadOnlyDictionary<string, EvidenceBlobVersion> documentBlobs);
}

public sealed class AircraftCasePackDocumentReferenceStager : IAircraftCasePackDocumentReferenceStager
{
    public IReadOnlyDictionary<string, AircraftCasePackDocumentReference> Stage(
        AircraftCasePackCase record,
        IReadOnlyDictionary<string, EvidenceBlobVersion> documentBlobs)
    {
        var references = record.Documents!.ToDictionary(
            document => document.Path,
            document =>
            {
                var blob = documentBlobs[document.Path];
                return new AircraftCasePackDocumentReference(document.Path, blob.Name, blob.Sha256, blob.Version);
            },
            StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, AircraftCasePackDocumentReference>(references);
    }
}

public interface IAircraftCasePackCaseStager
{
    PublishedAircraftCasePackCase Stage(AircraftCasePackCase record, IReadOnlyDictionary<string, EvidenceBlobVersion> documentBlobs);

    PublishedAircraftCasePackCase Stage(
        AircraftCasePackCase record,
        IReadOnlyDictionary<string, AircraftCasePackDocumentReference> documentReferences)
    {
        var blobs = documentReferences.ToDictionary(
            pair => pair.Key,
            pair => new EvidenceBlobVersion(pair.Value.BlobName, pair.Value.Sha256, pair.Value.Version),
            StringComparer.Ordinal);
        return Stage(record, blobs);
    }
}

public sealed class AircraftCasePackCaseStager : IAircraftCasePackCaseStager
{
    public PublishedAircraftCasePackCase Stage(
        AircraftCasePackCase record,
        IReadOnlyDictionary<string, EvidenceBlobVersion> documentBlobs)
    {
        var references = new AircraftCasePackDocumentReferenceStager().Stage(record, documentBlobs);
        return Stage(record, references);
    }

    public PublishedAircraftCasePackCase Stage(
        AircraftCasePackCase record,
        IReadOnlyDictionary<string, AircraftCasePackDocumentReference> documentReferences)
    {
        var references = record.Documents!.Select(document => documentReferences[document.Path]).ToArray();
        var documents = record.Documents.Select((document, index) =>
            new PublishedAircraftCasePackDocument(document.Path, document.Synthetic, document.GeneratorReference, references[index])).ToArray();
        var components = record.Components!.Select(component => new PublishedAircraftCasePackComponent(
            component.ComponentId, Array.AsReadOnly(component.EvidenceDocumentPaths!.ToArray()))).ToArray();
        return new PublishedAircraftCasePackCase(record.CaseId, record.Synthetic, record.GeneratorReference, record.AircraftCount,
            record.EngineCount, Array.AsReadOnly(components), Array.AsReadOnly(documents), Array.AsReadOnly(references));
    }
}
public sealed class AircraftCasePackRevisionConflictException()
    : Exception("Aircraft case-pack revision changed; no pack was published.");
public sealed class AircraftCasePackPublisher(
    IEvidenceBlobStore evidenceStore,
    IAircraftCasePackSnapshotStore snapshotStore,
    IAircraftCasePackCaseStager caseStager,
    IAircraftCasePackDocumentReferenceStager? documentReferenceStager = null,
    ILogger<AircraftCasePackPublisher>? logger = null)
{
    private static readonly HashSet<string> EvaluationCaseIds = CreateEvaluationCaseIds();

    public Task<AircraftCasePackSnapshot> PublishAsync(AircraftCasePackLoader loader, long expectedRevision,
        CancellationToken cancellationToken = default) => PublishAsync(loader.Load, expectedRevision, cancellationToken);
    internal async Task<AircraftCasePackSnapshot> PublishAsync(
        Func<LoadedAircraftCasePack> load,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        ILogger diagnosticsLogger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AircraftCasePackPublisher>.Instance;
        try
        {
            var pack = load();
            var manifest = pack.ValidatedManifest;
            var current = snapshotStore.Read();
            var provenanceErrors = ValidateCaseIdentifiers(manifest.Cases.Select(item => item.CaseId));
            if (provenanceErrors.Count > 0) throw new AircraftCasePackValidationException(provenanceErrors);

            if (current is not null)
            {
                var errors = CompareVersions(current.Manifest, manifest, current.Files, pack.Files);
                if (errors.Count > 0) throw new AircraftCasePackValidationException(errors);
                if (StringComparer.Ordinal.Equals(current.PackVersion, manifest.PackVersion)
                    && StringComparer.Ordinal.Equals(current.Sha256, manifest.Sha256))
                {
                    CasePackDiagnostics.PackLoad(diagnosticsLogger, "success", CasePackDiagnostics.ElapsedMilliseconds(started), manifest.PackVersion);
                    return current;
                }
            }

            var cases = new List<PublishedAircraftCasePackCase>(manifest.Cases.Count);
            foreach (var record in manifest.Cases)
            {
                var blobs = new Dictionary<string, EvidenceBlobVersion>(StringComparer.Ordinal);
                foreach (var document in record.Documents!)
                {
                    var identity = record.CaseId + "\0" + document.Path;
                    var documentId = "case-pack-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
                    blobs.Add(document.Path, await evidenceStore.StoreAsync(documentId, pack.ReadFile(document.Path), cancellationToken));
                }
                var references = (documentReferenceStager ?? new AircraftCasePackDocumentReferenceStager()).Stage(record, blobs);
                cases.Add(caseStager.Stage(record, references));
            }

            var snapshot = new AircraftCasePackSnapshot(expectedRevision + 1, manifest, pack.Files, cases);
            if (!snapshotStore.TryCommit(expectedRevision, snapshot))
                throw new AircraftCasePackRevisionConflictException();
            CasePackDiagnostics.PackLoad(diagnosticsLogger, "success", CasePackDiagnostics.ElapsedMilliseconds(started), manifest.PackVersion);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AircraftCasePackValidationException)
        {
            CasePackDiagnostics.PackLoad(diagnosticsLogger, "validation_failed", CasePackDiagnostics.ElapsedMilliseconds(started));
            throw;
        }
        catch (AircraftCasePackRevisionConflictException)
        {
            CasePackDiagnostics.PackLoad(diagnosticsLogger, "conflict", CasePackDiagnostics.ElapsedMilliseconds(started));
            throw;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
        {
            CasePackDiagnostics.PackLoad(diagnosticsLogger, "validation_failed", CasePackDiagnostics.ElapsedMilliseconds(started));
            throw;
        }
        catch (Exception)
        {
            CasePackDiagnostics.PackLoad(diagnosticsLogger, "failed", CasePackDiagnostics.ElapsedMilliseconds(started));
            throw;
        }
    }
    public static IReadOnlyList<string> CompareVersions(AircraftCasePackManifest previous, AircraftCasePackManifest current,
        IReadOnlyDictionary<string, byte[]> previousFiles, IReadOnlyDictionary<string, byte[]> currentFiles)
    {
        var errors = AircraftCasePackManifest.Compare(previous, current, previousFiles, currentFiles).ToList();
        errors.AddRange(current.Cases.Where(item => EvaluationCaseIds.Contains(item.CaseId))
            .Select(item => $"{item.CaseId}: evaluation-suite case identifiers are not part of the pack"));
        return errors.AsReadOnly();
    }
    public static IReadOnlyList<string> ValidateCaseIdentifiers(IEnumerable<string> caseIds) =>
        caseIds.Where(EvaluationCaseIds.Contains).Select(item => $"{item}: evaluation-suite case identifiers are not part of the pack").ToArray();
    private static HashSet<string> CreateEvaluationCaseIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in new[] { 1, 2 })
            for (var number = 1; number <= 40; number++) ids.Add($"EVAL-V{version}-{number:00}");
        return ids;
    }
}
