using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Evidence;

public enum EvidenceProcessingState
{
    Complete,
    RejectedRetryable,
    RejectedPermanent,
}

public sealed record EvidenceDocument(string FileName, byte[] Content);

public sealed record EvidenceBasisVersion(string Key, int Version, string Digest, string Text);

public sealed record SourceBasisPin(string SourceId, string RecordId, int RecordVersion, string Content, string ContentDigest);
public sealed record GatheredSourceBasis(IReadOnlyList<SourceBasisPin> Pins, string Content);
public sealed record EvidenceIntakeResult(
    EvidenceProcessingState State,
    string Reason,
    IReadOnlyList<EvidenceBasisVersion> Versions);

/// <summary>Per-document outcome: <c>accepted</c>, <c>rejected-retryable</c> or <c>rejected-permanent</c>.</summary>
public sealed record EvidenceDocumentResult(string FileName, string Status, string Reason, int? Version, string? Digest);

/// <summary>Package outcome: <c>complete</c>, <c>partial</c> (some documents accepted) or <c>failed</c> (none accepted).</summary>
public sealed record EvidencePackageOutcome(string State, IReadOnlyList<EvidenceDocumentResult> Documents);

/// <summary>
/// Append-only store of evidence basis content. Each commit creates a new version per key;
/// prior versions are never overwritten.
/// </summary>
public sealed class EvidenceBasisStore
{
    private readonly Dictionary<string, List<EvidenceBasisVersion>> _versions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public EvidenceBasisVersion? Latest(string key)
    {
        lock (_gate)
        {
            return _versions.TryGetValue(key, out var list) && list.Count > 0 ? list[^1] : null;
        }
    }

    public IReadOnlyList<EvidenceBasisVersion> History(string key)
    {
        lock (_gate)
        {
            return _versions.TryGetValue(key, out var list) ? list.ToArray() : Array.Empty<EvidenceBasisVersion>();
        }
    }

    /// <summary>A witness matches only when it names an existing version and its digest equals that version's digest.</summary>
    public bool Verify(string key, int version, string digest)
    {
        lock (_gate)
        {
            if (!_versions.TryGetValue(key, out var list) || version < 1 || version > list.Count)
            {
                return false;
            }

            return string.Equals(list[version - 1].Digest, digest, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal IReadOnlyList<EvidenceBasisVersion> CommitAll(IReadOnlyList<(string Key, string Text)> items)
    {
        lock (_gate)
        {
            var created = new List<EvidenceBasisVersion>(items.Count);
            foreach (var (key, text) in items)
            {
                if (!_versions.TryGetValue(key, out var list))
                {
                    list = new List<EvidenceBasisVersion>();
                    _versions[key] = list;
                }

                var version = new EvidenceBasisVersion(key, list.Count + 1, Digest(text), text);
                list.Add(version);
                created.Add(version);
            }

            return created;
        }
    }

    internal static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

public static class EvidenceIntake
{
    public const string SourceBasisFormat = "source-record-pins-v1";

    private static readonly JsonSerializerOptions SourceRecordJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DictionaryKeyPolicy = null,
    };

    public const int MaxDocuments = 12;
    public const long MaxDocumentBytes = 10L * 1024 * 1024;
    public const long MaxPackageBytes = 50L * 1024 * 1024;
    public const int MaxPagesPerDocument = 200;

    /// <summary>Separates the pages of a stored document text.</summary>
    public const char PageSeparator = '\f';

    /// <summary>Reads and pins the current records from all five case-scoped source systems.</summary>
    public static async Task<GatheredSourceBasis> GatherSourceBasisAsync(
        string caseId,
        ISourceSystem<TechnicalPayload> technical,
        ISourceSystem<MaintenancePayload> maintenance,
        ISourceSystem<FinancePayload> finance,
        ISourceSystem<RecordsPayload> records,
        ISourceSystem<ContractsPayload> contracts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentNullException.ThrowIfNull(technical);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(finance);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(contracts);
        var pins = new List<SourceBasisPin>();
        pins.AddRange(CreatePins("technical", caseId, await ReadAsync(technical, caseId, cancellationToken)));
        pins.AddRange(CreatePins("maintenance", caseId, await ReadAsync(maintenance, caseId, cancellationToken)));
        pins.AddRange(CreatePins("finance", caseId, await ReadAsync(finance, caseId, cancellationToken)));
        pins.AddRange(CreatePins("records", caseId, await ReadAsync(records, caseId, cancellationToken)));
        pins.AddRange(CreatePins("contracts", caseId, await ReadAsync(contracts, caseId, cancellationToken)));
        return CreateSourceBasis(pins);
    }
    /// <summary>Builds a deterministic source-basis payload, rejecting empty or duplicate pin sets.</summary>
    public static GatheredSourceBasis CreateSourceBasis(IEnumerable<SourceBasisPin> sourcePins)
    {
        ArgumentNullException.ThrowIfNull(sourcePins);
        var suppliedPins = sourcePins.ToArray();
        if (suppliedPins.Any(pin => pin is null))
        {
            throw new ArgumentException("A gathered source basis cannot contain null pins.", nameof(sourcePins));
        }
        var pins = suppliedPins
            .OrderBy(pin => pin.SourceId, StringComparer.Ordinal)
            .ThenBy(pin => pin.RecordId, StringComparer.Ordinal)
            .ThenBy(pin => pin.RecordVersion)
            .ToArray();
        if (pins.Length == 0)
        {
            throw new ArgumentException("A gathered source basis must contain at least one source pin.", nameof(sourcePins));
        }
        var identities = new HashSet<(string SourceId, string RecordId, int RecordVersion)>();
        foreach (var pin in pins)
        {
            if (pin is null || string.IsNullOrWhiteSpace(pin.SourceId) || string.IsNullOrWhiteSpace(pin.RecordId)
                || pin.RecordVersion < 1 || string.IsNullOrEmpty(pin.Content) || string.IsNullOrWhiteSpace(pin.ContentDigest)
                || !string.Equals(pin.ContentDigest, Digest(pin.Content), StringComparison.Ordinal))
            {
                throw new ArgumentException("A source pin must contain a valid identity, snapshot and content digest.", nameof(sourcePins));
            }
            if (!identities.Add((pin.SourceId, pin.RecordId, pin.RecordVersion)))
            {
                throw new ArgumentException("A gathered source basis cannot contain duplicate source record pins.", nameof(sourcePins));
            }
        }
        var readOnlyPins = Array.AsReadOnly(pins);
        var content = JsonSerializer.Serialize(new SourceBasisSnapshot(SourceBasisFormat, pins), SourceRecordJsonOptions);
        return new GatheredSourceBasis(readOnlyPins, content);
    }
    private static async Task<IReadOnlyList<SourceRecord<TPayload>>> ReadAsync<TPayload>(
        ISourceSystem<TPayload> source,
        string caseId,
        CancellationToken cancellationToken) where TPayload : class
    {
        var result = await source.ListAsync(caseId, cancellationToken);
        return result switch
        {
            SourceReadSucceeded<IReadOnlyList<SourceRecord<TPayload>>> succeeded => succeeded.Value,
            SourceUnavailable<IReadOnlyList<SourceRecord<TPayload>>> unavailable =>
                throw new InvalidOperationException($"Source '{unavailable.SourceId}' is unavailable; its records cannot be pinned."),
            UnknownSource<IReadOnlyList<SourceRecord<TPayload>>> unknown =>
                throw new InvalidOperationException($"Source '{unknown.SourceId}' is not registered; its records cannot be pinned."),
            _ => throw new InvalidOperationException("Source read did not return a successful record list."),
        };
    }
    private static IEnumerable<SourceBasisPin> CreatePins<TPayload>(
        string expectedSourceId,
        string caseId,
        IReadOnlyList<SourceRecord<TPayload>> records) where TPayload : class
    {
        foreach (var record in records)
        {
            if (record is null || !string.Equals(record.SourceId, expectedSourceId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Source '{expectedSourceId}' returned an invalid record envelope.");
            }
            var problems = record.Validate(caseId);
            if (problems.Count > 0)
            {
                throw new InvalidDataException($"Source '{expectedSourceId}' returned an invalid record envelope: {string.Join(" ", problems)}");
            }
            var content = SerializeRecord(record);
            yield return new SourceBasisPin(
                record.SourceId,
                record.RecordId,
                record.RecordVersion,
                content,
                Digest(content));
        }
    }
    private static string SerializeRecord<TPayload>(SourceRecord<TPayload> record) where TPayload : class
    {
        var fields = record.Payload switch
        {
            TechnicalPayload payload => payload.Fields,
            MaintenancePayload payload => payload.Fields,
            FinancePayload payload => payload.Fields,
            RecordsPayload payload => payload.Fields,
            ContractsPayload payload => payload.Fields,
            _ => throw new InvalidDataException("Source payload type is not supported for basis pinning."),
        };
        var orderedFields = fields.OrderBy(field => field.Key, StringComparer.Ordinal)
            .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            asOf = record.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            caseId = record.CaseId,
            kind = record.Kind,
            payload = new { fields = orderedFields },
            recordId = record.RecordId,
            recordVersion = record.RecordVersion,
            sourceId = record.SourceId,
            stale = record.Stale,
            synthetic = record.Synthetic,
        }, SourceRecordJsonOptions);
    }
    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record SourceBasisSnapshot(string Format, IReadOnlyList<SourceBasisPin> Pins);

    /// <summary>
    /// Validates a package and commits extracted text as new basis versions only when every document is accepted.
    /// A rejected package creates no basis content, so no partial basis is ever stored.
    /// </summary>
    public static EvidenceIntakeResult Process(
        IReadOnlyList<EvidenceDocument> documents,
        EvidenceBasisStore store,
        IsolatedPdfParserOptions? parserOptions = null)
    {
        var invalid = Validate(documents);
        if (invalid is not null)
        {
            return Permanent(invalid);
        }

        var extracted = new List<(string Key, string Text)>(documents.Count);
        foreach (var document in documents)
        {
            var outcome = IsolatedPdfParser.ParseDocument(document.Content, document.FileName, parserOptions, rejectUnsafeContent: true);
            if (outcome.State != EvidenceProcessingState.Complete)
            {
                return new EvidenceIntakeResult(outcome.State, outcome.Reason, Array.Empty<EvidenceBasisVersion>());
            }

            extracted.Add((document.FileName, outcome.Text));
        }

        var versions = store.CommitAll(extracted);
        return new EvidenceIntakeResult(EvidenceProcessingState.Complete, "accepted", versions);
    }

    /// <summary>Returns the reason a package is outside the limits or is not PDF by name, or null when it is valid.</summary>
    public static string? Validate(IReadOnlyList<EvidenceDocument> documents)
    {
        if (documents.Count > MaxDocuments)
        {
            return $"package has {documents.Count} documents; limit is {MaxDocuments}";
        }

        long total = 0;
        foreach (var document in documents)
        {
            if (document.Content.LongLength > MaxDocumentBytes)
            {
                return $"{document.FileName} is {document.Content.LongLength} bytes; limit is {MaxDocumentBytes}";
            }

            total += document.Content.LongLength;
        }

        if (total > MaxPackageBytes)
        {
            return $"package is {total} bytes; limit is {MaxPackageBytes}";
        }

        foreach (var document in documents)
        {
            if (!string.Equals(Path.GetExtension(document.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return $"{document.FileName}: unsupported type; only PDF is accepted";
            }
        }

        return null;
    }

    /// <summary>
    /// Parses each document in isolation. Accepted documents are committed together as new basis versions keyed by
    /// case and filename; rejected documents are not committed. The package is partial when only some are accepted.
    /// </summary>
    public static EvidencePackageOutcome ProcessPackage(
        string caseId,
        IReadOnlyList<EvidenceDocument> documents,
        EvidenceBasisStore store,
        IsolatedPdfParserOptions parserOptions)
    {
        var results = new EvidenceDocumentResult[documents.Count];
        var accepted = new List<(int Index, string Key, string Text)>(documents.Count);
        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            var outcome = IsolatedPdfParser.ParseDocument(document.Content, document.FileName, parserOptions, rejectUnsafeContent: true);
            if (outcome.State == EvidenceProcessingState.Complete)
            {
                accepted.Add((i, $"{caseId}/{document.FileName}", outcome.Text));
                results[i] = new EvidenceDocumentResult(document.FileName, "accepted", "accepted", null, null);
            }
            else
            {
                var status = outcome.State == EvidenceProcessingState.RejectedRetryable ? "rejected-retryable" : "rejected-permanent";
                results[i] = new EvidenceDocumentResult(document.FileName, status, outcome.Reason, null, null);
            }
        }

        var versions = store.CommitAll(accepted.Select(a => (a.Key, a.Text)).ToList());
        for (var j = 0; j < accepted.Count; j++)
        {
            var index = accepted[j].Index;
            results[index] = results[index] with { Version = versions[j].Version, Digest = versions[j].Digest };
        }

        var state = accepted.Count == documents.Count ? "complete" : accepted.Count == 0 ? "failed" : "partial";
        return new EvidencePackageOutcome(state, results);
    }

    /// <summary>
    /// Extracts API evidence text in-process after bounded parser preflight checks. Non-text content is ignored;
    /// only PdfPig's page text is stored.
    /// </summary>
    internal static (EvidenceProcessingState State, string Reason, string Text) ExtractTextInProcess(
        byte[] content,
        Action? pdfPigOpening = null,
        Func<int, Func<string>, string>? pageTextReading = null,
        bool rejectUnsafeContent = false)
    {
        try
        {
            pdfPigOpening?.Invoke();
            using var pdf = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true });

            if (pdf.NumberOfPages > MaxPagesPerDocument)
            {
                return (EvidenceProcessingState.RejectedRetryable, $"{pdf.NumberOfPages} pages; limit is {MaxPagesPerDocument}", "");
            }

            var text = new StringBuilder();
            var pageIndex = 0;
            foreach (var page in pdf.GetPages())
            {
                if (rejectUnsafeContent && page.ExperimentalAccess.GetAnnotations().Any())
                    return (EvidenceProcessingState.RejectedPermanent, "annotations are not accepted", "");

                if (pageIndex > 0) text.Append(PageSeparator);
                var pageText = pageTextReading is null
                    ? page.Text
                    : pageTextReading(pageIndex, () => page.Text);
                if (!string.IsNullOrEmpty(pageText)) text.AppendLine(pageText);
                pageIndex++;
            }

            return (EvidenceProcessingState.Complete, "accepted", text.ToString());
        }
        catch (OutOfMemoryException) { return (EvidenceProcessingState.RejectedRetryable, "exceeded the memory limit; retry is possible", ""); }
        catch (Exception) { return (EvidenceProcessingState.RejectedRetryable, "PdfPig could not open or read the document; retry is possible", ""); }
    }

    /// <summary>
    /// Runs only inside the isolated PDF worker process (see IsolatedPdfParser), never in the API process.
    /// Text is extracted by PdfPig only; no OCR, network, native code or executable content is used.
    /// <paramref name="withinMemoryLimit"/> lets the worker stop early when its working set exceeds the limit.
    /// </summary>
    internal static (EvidenceProcessingState State, string Reason, string Text) ExtractText(
        byte[] content,
        Func<bool>? withinMemoryLimit = null)
    {
        try
        {
            if (withinMemoryLimit is not null && !withinMemoryLimit())
            {
                return (EvidenceProcessingState.RejectedRetryable, "worker memory limit exceeded; retry is possible", "");
            }

            using var pdf = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true });

            if (pdf.NumberOfPages > MaxPagesPerDocument)
            {
                return (EvidenceProcessingState.RejectedRetryable, $"{pdf.NumberOfPages} pages; limit is {MaxPagesPerDocument}", "");
            }

            if (HasEmbeddedFilesOrForms(pdf))
            {
                return (EvidenceProcessingState.RejectedPermanent, "embedded files or form fields are not accepted", "");
            }

            var text = new StringBuilder();
            var pageIndex = 0;
            foreach (var page in pdf.GetPages())
            {
                if (withinMemoryLimit is not null && !withinMemoryLimit())
                {
                    return (EvidenceProcessingState.RejectedRetryable, "worker memory limit exceeded; retry is possible", "");
                }

                if (page.ExperimentalAccess.GetAnnotations().Any())
                {
                    return (EvidenceProcessingState.RejectedPermanent, "annotations are not accepted", "");
                }

                if (page.GetImages().Any())
                {
                    return (EvidenceProcessingState.RejectedPermanent, "images are not accepted", "");
                }

                // A form feed separates pages, so a citation can be checked against the page it names. A one-page text is unchanged.
                if (pageIndex++ > 0) text.Append(PageSeparator);
                text.AppendLine(page.Text);
            }

            return (EvidenceProcessingState.Complete, "accepted", text.ToString());
        }
        catch (OutOfMemoryException)
        {
            return (EvidenceProcessingState.RejectedRetryable, "exceeded the memory limit; retry is possible", "");
        }
        catch (Exception)
        {
            return (EvidenceProcessingState.RejectedRetryable, "PdfPig could not open or read the document; retry is possible", "");
        }
    }

    private static bool HasEmbeddedFilesOrForms(PdfDocument pdf)
    {
        var catalog = pdf.Structure.Catalog.CatalogDictionary;
        if (HasKey(catalog, "AcroForm"))
        {
            return true;
        }

        return catalog.TryGet(NameToken.Create("Names"), out var names)
            && names is DictionaryToken namesDictionary
            && HasKey(namesDictionary, "EmbeddedFiles");
    }

    private static bool HasKey(DictionaryToken dictionary, string name) =>
        dictionary.Data.ContainsKey(name);

    private static EvidenceIntakeResult Permanent(string reason) =>
        new(EvidenceProcessingState.RejectedPermanent, reason, Array.Empty<EvidenceBasisVersion>());
}
