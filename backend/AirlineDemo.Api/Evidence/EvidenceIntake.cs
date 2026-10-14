using System.Security.Cryptography;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

namespace AirlineDemo.Api.Evidence;

public enum EvidenceProcessingState
{
    Complete,
    RejectedRetryable,
    RejectedPermanent,
}

public sealed record EvidenceDocument(string FileName, byte[] Content);

public sealed record EvidenceBasisVersion(string Key, int Version, string Digest, string Text);

public sealed record EvidenceIntakeResult(
    EvidenceProcessingState State,
    string Reason,
    IReadOnlyList<EvidenceBasisVersion> Versions);

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
    public const int MaxDocuments = 12;
    public const long MaxDocumentBytes = 10L * 1024 * 1024;
    public const long MaxPackageBytes = 50L * 1024 * 1024;
    public const int MaxPagesPerDocument = 200;

    /// <summary>
    /// Validates a package and commits extracted text as new basis versions only when every document is accepted.
    /// A rejected package creates no basis content, so no partial basis is ever stored.
    /// </summary>
    public static EvidenceIntakeResult Process(
        IReadOnlyList<EvidenceDocument> documents,
        EvidenceBasisStore store,
        IsolatedPdfParserOptions? parserOptions = null)
    {
        if (documents.Count > MaxDocuments)
        {
            return Permanent($"package has {documents.Count} documents; limit is {MaxDocuments}");
        }

        long total = 0;
        foreach (var document in documents)
        {
            if (document.Content.LongLength > MaxDocumentBytes)
            {
                return Permanent($"{document.FileName} is {document.Content.LongLength} bytes; limit is {MaxDocumentBytes}");
            }

            total += document.Content.LongLength;
        }

        if (total > MaxPackageBytes)
        {
            return Permanent($"package is {total} bytes; limit is {MaxPackageBytes}");
        }

        foreach (var document in documents)
        {
            if (!string.Equals(Path.GetExtension(document.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return Permanent($"{document.FileName}: unsupported type; only PDF is accepted");
            }
        }

        var extracted = new List<(string Key, string Text)>(documents.Count);
        foreach (var document in documents)
        {
            var outcome = IsolatedPdfParser.Parse(document.Content, parserOptions);
            if (outcome.State != EvidenceProcessingState.Complete)
            {
                return new EvidenceIntakeResult(outcome.State, $"{document.FileName}: {outcome.Reason}", Array.Empty<EvidenceBasisVersion>());
            }

            extracted.Add((document.FileName, outcome.Text));
        }

        var versions = store.CommitAll(extracted);
        return new EvidenceIntakeResult(EvidenceProcessingState.Complete, "accepted", versions);
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

                text.AppendLine(page.Text);
            }

            return (EvidenceProcessingState.Complete, "accepted", text.ToString());
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
