using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace AirlineDemo.Api.Evidence;

public static class EvidenceLimits
{
    public const int MaxDocuments = 12;
    public const long MaxDocumentBytes = 10L * 1024 * 1024;
    public const long MaxPackageBytes = 50L * 1024 * 1024;
    public const int MaxPagesPerDocument = 200;

    // Bounds the decoded text produced per document, so compressed content cannot expand without limit.
    public const int MaxExtractedCharactersPerDocument = 1_000_000;

    // Bounds decoded content streams before PdfPig materialises any page text.
    public const long MaxDecodedBytesPerStream = 10L * 1024 * 1024;
    public const long MaxDecodedBytesPerDocument = 50L * 1024 * 1024;
}

public enum EvidenceFailureKind
{
    EmptyPackage,
    LimitExceeded,
    UnsupportedFormat,
    ActiveContent,
    Truncated,
    Corrupt,
    NoText,
}

public enum EvidenceProcessingState
{
    Complete,
    Failed,
}

public sealed record EvidenceDocument(string Name, byte[] Bytes);

public sealed record EvidenceFailure(EvidenceFailureKind Kind, string Message, bool Retryable);

public sealed record EvidenceIntakeResult(
    EvidenceProcessingState State,
    string? Content,
    string? ContentDigest,
    EvidenceFailure? Failure);

public sealed record EvidenceBasisVersion(int Version, string Content, string ContentDigest, DateTimeOffset CreatedAt);

public sealed class EvidenceWitnessMismatchException(string message) : Exception(message);

internal sealed class EvidenceIntakeException(string message, EvidenceFailureKind kind, bool retryable)
    : Exception(message)
{
    public EvidenceFailureKind Kind { get; } = kind;

    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Text-only PDF intake. Limits are checked before any parsing. Parsing uses PdfPig in-process
/// for text extraction only; no OCR, network, or native code is involved.
/// </summary>
public static class EvidenceIntake
{
    public static EvidenceIntakeResult Process(IReadOnlyList<EvidenceDocument> documents)
    {
        try
        {
            var content = ExtractPackage(documents);
            return new EvidenceIntakeResult(EvidenceProcessingState.Complete, content, Sha256Hex(content), null);
        }
        catch (EvidenceIntakeException ex)
        {
            return new EvidenceIntakeResult(
                EvidenceProcessingState.Failed,
                null,
                null,
                new EvidenceFailure(ex.Kind, ex.Message, ex.Retryable));
        }
    }

    private static string ExtractPackage(IReadOnlyList<EvidenceDocument> documents)
    {
        if (documents.Count == 0)
        {
            throw new EvidenceIntakeException("Package has no documents.", EvidenceFailureKind.EmptyPackage, false);
        }

        if (documents.Count > EvidenceLimits.MaxDocuments)
        {
            throw new EvidenceIntakeException(
                $"Package has {documents.Count} documents; the limit is {EvidenceLimits.MaxDocuments}.",
                EvidenceFailureKind.LimitExceeded,
                true);
        }

        long total = 0;
        foreach (var document in documents)
        {
            if (document.Bytes.LongLength > EvidenceLimits.MaxDocumentBytes)
            {
                throw new EvidenceIntakeException(
                    $"Document '{document.Name}' exceeds the {EvidenceLimits.MaxDocumentBytes} byte limit.",
                    EvidenceFailureKind.LimitExceeded,
                    true);
            }

            total += document.Bytes.LongLength;
        }

        if (total > EvidenceLimits.MaxPackageBytes)
        {
            throw new EvidenceIntakeException(
                $"Package exceeds the {EvidenceLimits.MaxPackageBytes} byte limit.",
                EvidenceFailureKind.LimitExceeded,
                true);
        }

        foreach (var document in documents)
        {
            ValidateBytes(document);
        }

        var parts = documents.Select(ExtractDocument).ToList();
        return string.Join("\n", parts);
    }

    private static void ValidateBytes(EvidenceDocument document)
    {
        var bytes = document.Bytes;

        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
        {
            throw new EvidenceIntakeException(
                $"Document '{document.Name}' is a nested archive; archives are rejected.",
                EvidenceFailureKind.UnsupportedFormat,
                true);
        }

        if (!StartsWithAscii(bytes, "%PDF-"))
        {
            throw new EvidenceIntakeException(
                $"Document '{document.Name}' is not a PDF.",
                EvidenceFailureKind.UnsupportedFormat,
                true);
        }

        var tail = Encoding.Latin1.GetString(bytes, Math.Max(0, bytes.Length - 1024), Math.Min(1024, bytes.Length));
        if (!tail.Contains("%%EOF", StringComparison.Ordinal))
        {
            throw new EvidenceIntakeException(
                $"Document '{document.Name}' is truncated (no end-of-file marker).",
                EvidenceFailureKind.Truncated,
                true);
        }

    }

    private static readonly HashSet<string> AllowedCatalogKeys = new(StringComparer.Ordinal)
    {
        "Type", "Pages", "Version", "Metadata", "MarkInfo", "Lang", "PageLabels", "ViewerPreferences", "Outlines",
    };

    // Decides active, embedded, form, and image content from parsed PDF entries, never from raw bytes,
    // so ordinary text that contains marker words is accepted. The catalog is an allowlist: any other key is rejected.
    private static void RejectActiveContent(string name, PdfDocument pdf)
    {
        var catalog = pdf.Structure.Catalog.CatalogDictionary.Data;
        foreach (var key in new[] { "OpenAction", "AA", "Names", "AcroForm", "AF", "EmbeddedFiles", "JavaScript", "Launch" })
        {
            if (catalog.ContainsKey(key))
            {
                throw ActiveContentFailure(name, $"/{key} in the document catalog");
            }
        }

        foreach (var key in catalog.Keys)
        {
            if (!AllowedCatalogKeys.Contains(key))
            {
                throw ActiveContentFailure(name, $"/{key} in the document catalog is not allowed");
            }
        }
    }

    // Any page annotation, page-level additional action, or image/form XObject is rejected from parsed dictionaries.
    private static void RejectPageContent(string name, PdfDocument pdf, Page page)
    {
        var dictionary = page.Dictionary.Data;
        if (dictionary.ContainsKey("Annots"))
        {
            throw ActiveContentFailure(name, "/Annots on a page");
        }

        if (dictionary.TryGetValue("Resources", out var resourcesToken)
            && Resolve(pdf, resourcesToken) is DictionaryToken resources
            && resources.Data.TryGetValue("XObject", out var xObjectsToken)
            && Resolve(pdf, xObjectsToken) is DictionaryToken xObjects)
        {
            foreach (var xObjectToken in xObjects.Data.Values)
            {
                if (Resolve(pdf, xObjectToken) is StreamToken xObject
                    && xObject.StreamDictionary.Data.TryGetValue("Subtype", out var subtype)
                    && Resolve(pdf, subtype) is NameToken { Data: "Image" or "Form" } subtypeName)
                {
                    throw ActiveContentFailure(name, $"/{subtypeName.Data} XObject on a page");
                }
            }
        }
    }

    // Decodes the page's content streams and enforces decoded-size bounds before PdfPig extracts any text.
    private static void MeasureDecodedContent(string name, PdfDocument pdf, Page page, ref long decodedTotal)
    {
        if (!page.Dictionary.Data.TryGetValue("Contents", out var contents))
        {
            return;
        }

        var streams = Resolve(pdf, contents) switch
        {
            StreamToken single => new[] { single },
            ArrayToken array => array.Data.Select(entry => Resolve(pdf, entry)).OfType<StreamToken>().ToArray(),
            _ => Array.Empty<StreamToken>(),
        };

        foreach (var stream in streams)
        {
            var decodedLength = DecodedLength(name, pdf, stream);
            if (decodedLength > EvidenceLimits.MaxDecodedBytesPerStream)
            {
                throw new EvidenceIntakeException(
                    $"Document '{name}' has a content stream that decodes to more than {EvidenceLimits.MaxDecodedBytesPerStream} bytes; it is rejected.",
                    EvidenceFailureKind.LimitExceeded,
                    true);
            }

            decodedTotal += decodedLength;
            if (decodedTotal > EvidenceLimits.MaxDecodedBytesPerDocument)
            {
                throw new EvidenceIntakeException(
                    $"Document '{name}' decodes to more than {EvidenceLimits.MaxDecodedBytesPerDocument} bytes of content; it is rejected.",
                    EvidenceFailureKind.LimitExceeded,
                    true);
            }
        }
    }

    // Accepted: unfiltered streams, single FlateDecode streams (inflated only up to the limit), and hex/ASCII85 chains
    // that cannot expand. Any other filter chain is rejected before it is decoded.
    private static long DecodedLength(string name, PdfDocument pdf, StreamToken stream)
    {
        if (!stream.StreamDictionary.Data.ContainsKey("Filter"))
        {
            return stream.Data.Count;
        }

        if (IsNonExpandingFilterChain(pdf, stream.StreamDictionary))
        {
            // Hex and ASCII85 decoding never yield more bytes than their input, which is already bounded per document.
            return stream.Decode(DefaultFilterProvider.Instance).Count;
        }

        if (!IsFlateOnly(pdf, stream.StreamDictionary))
        {
            throw new EvidenceIntakeException(
                $"Document '{name}' has a content stream with an unsupported filter chain; it is rejected.",
                EvidenceFailureKind.UnsupportedFormat,
                true);
        }

        var limit = EvidenceLimits.MaxDecodedBytesPerStream;
        var buffer = new byte[64 * 1024];
        long total = 0;
        try
        {
            using var input = new MemoryStream(stream.Data.ToArray());
            using var inflater = new ZLibStream(input, CompressionMode.Decompress);
            int read;
            while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > limit)
                {
                    return total;
                }
            }
        }
        catch (InvalidDataException)
        {
            throw new EvidenceIntakeException(
                $"Document '{name}' has a corrupt compressed content stream.",
                EvidenceFailureKind.Corrupt,
                true);
        }

        return total;
    }

    private static bool IsNonExpandingFilterChain(PdfDocument pdf, DictionaryToken dictionary)
    {
        if (!dictionary.Data.TryGetValue("Filter", out var filter))
        {
            return false;
        }

        var resolved = Resolve(pdf, filter);
        var names = resolved is ArrayToken array
            ? array.Data.Select(entry => Resolve(pdf, entry)).ToList()
            : [resolved];

        return names.Count > 0 && names.All(entry =>
            entry is NameToken { Data: "ASCIIHexDecode" or "AHx" or "ASCII85Decode" or "A85" });
    }

    private static bool IsFlateOnly(PdfDocument pdf, DictionaryToken dictionary)
    {
        if (!dictionary.Data.TryGetValue("Filter", out var filter))
        {
            return false;
        }

        var resolved = Resolve(pdf, filter);
        if (resolved is ArrayToken { Data.Count: 1 } single)
        {
            resolved = Resolve(pdf, single.Data[0]);
        }

        return resolved is NameToken { Data: "FlateDecode" or "Fl" };
    }

    private static IToken Resolve(PdfDocument pdf, IToken token) =>
        token is IndirectReferenceToken reference ? pdf.Structure.GetObject(reference.Data).Data : token;

    private static EvidenceIntakeException ActiveContentFailure(string name, string detail) =>
        new(
            $"Document '{name}' contains active, embedded, or form content ({detail}); it is rejected.",
            EvidenceFailureKind.ActiveContent,
            true);

    private static string ExtractDocument(EvidenceDocument document)
    {
        try
        {
            using var pdf = PdfDocument.Open(document.Bytes);
            var pages = pdf.NumberOfPages;

            if (pages > EvidenceLimits.MaxPagesPerDocument)
            {
                throw new EvidenceIntakeException(
                    $"Document '{document.Name}' has {pages} pages; the limit is {EvidenceLimits.MaxPagesPerDocument}.",
                    EvidenceFailureKind.LimitExceeded,
                    true);
            }

            RejectActiveContent(document.Name, pdf);

            if (pages == 0)
            {
                throw new EvidenceIntakeException(
                    $"Document '{document.Name}' has no pages.",
                    EvidenceFailureKind.Corrupt,
                    true);
            }

            var text = new StringBuilder();
            long decodedTotal = 0;
            for (var page = 1; page <= pages; page++)
            {
                var pdfPage = pdf.GetPage(page);
                foreach (var key in new[] { "AF", "AA" })
                {
                    if (pdfPage.Dictionary.Data.ContainsKey(key))
                    {
                        throw ActiveContentFailure(document.Name, $"/{key} on page {page}");
                    }
                }

                RejectPageContent(document.Name, pdf, pdfPage);
                MeasureDecodedContent(document.Name, pdf, pdfPage, ref decodedTotal);
                // Decoded content streams expose inline images even when compressed; reject them as image payloads.
                if (pdfPage.GetImages().Any())
                {
                    throw new EvidenceIntakeException(
                        $"Document '{document.Name}' contains an image payload (image XObject or inline image); it is rejected.",
                        EvidenceFailureKind.ActiveContent,
                        true);
                }

                text.AppendLine(pdfPage.Text);
                if (text.Length > EvidenceLimits.MaxExtractedCharactersPerDocument)
                {
                    throw new EvidenceIntakeException(
                        $"Document '{document.Name}' extracts more than {EvidenceLimits.MaxExtractedCharactersPerDocument} characters; it is rejected.",
                        EvidenceFailureKind.LimitExceeded,
                        true);
                }
            }

            if (string.IsNullOrWhiteSpace(text.ToString()))
            {
                throw new EvidenceIntakeException(
                    $"Document '{document.Name}' has no extractable text (OCR is not supported).",
                    EvidenceFailureKind.NoText,
                    false);
            }

            return $"--- {document.Name} ({pages} pages) ---\n{text}";
        }
        catch (EvidenceIntakeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EvidenceIntakeException(
                $"Document '{document.Name}' could not be parsed: {ex.Message}",
                EvidenceFailureKind.Corrupt,
                true);
        }
    }

    private static bool StartsWithAscii(byte[] bytes, string prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    internal static string Sha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>Append-only store of immutable evidence basis versions per case.</summary>
public sealed class EvidenceBasisStore
{
    private readonly Dictionary<string, List<EvidenceBasisVersion>> _versions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Stores a completed intake as the next immutable version. Failed intakes never create a basis.</summary>
    public EvidenceBasisVersion Append(string caseId, EvidenceIntakeResult result)
    {
        if (result.State != EvidenceProcessingState.Complete || result.Content is null || result.ContentDigest is null)
        {
            throw new InvalidOperationException("A failed or partial intake cannot become an evidence basis.");
        }

        if (!string.Equals(EvidenceIntake.Sha256Hex(result.Content), result.ContentDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Basis digest does not match its content.");
        }

        lock (_gate)
        {
            if (!_versions.TryGetValue(caseId, out var list))
            {
                list = [];
                _versions[caseId] = list;
            }

            var version = new EvidenceBasisVersion(
                list.Count + 1,
                result.Content,
                result.ContentDigest,
                DateTimeOffset.UtcNow);
            list.Add(version);
            return version;
        }
    }

    public IReadOnlyList<EvidenceBasisVersion> Versions(string caseId)
    {
        lock (_gate)
        {
            return _versions.TryGetValue(caseId, out var list) ? list.ToArray() : [];
        }
    }

    /// <summary>Rejects a witness whose digest differs from the stored basis version.</summary>
    public void VerifyWitness(string caseId, int version, string witnessDigest)
    {
        var stored = Versions(caseId).FirstOrDefault(v => v.Version == version)
            ?? throw new EvidenceWitnessMismatchException($"No basis version {version} exists for this case.");

        if (!string.Equals(stored.ContentDigest, witnessDigest, StringComparison.Ordinal))
        {
            throw new EvidenceWitnessMismatchException(
                $"Witness digest does not match basis version {version}.");
        }
    }
}
