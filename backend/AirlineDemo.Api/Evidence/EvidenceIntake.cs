using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace AirlineDemo.Api.Evidence;

public static class EvidenceLimits
{
    public const int MaxDocuments = 12;
    public const long MaxDocumentBytes = 10L * 1024 * 1024;
    public const long MaxPackageBytes = 50L * 1024 * 1024;
    public const int MaxPagesPerDocument = 200;
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
    // Names that signal scripts, forms, embedded files, images, or launch actions.
    private static readonly string[] ActiveContentMarkers =
    [
        "/JavaScript", "/JS", "/Launch", "/EmbeddedFile", "/AcroForm", "/XFA", "/RichMedia",
        // Object streams hide dictionaries (and their actions) inside compressed data, so they are rejected, not inspected.
        "/ObjStm",
    ];

    // Image XObjects; PDF allows whitespace and comments (%...EOL) between the /Subtype key and the /Image name.
    private static readonly Regex ImageSubtype = new(
        @"/Subtype(?:[\x00\t\n\f\r ]|%[^\r\n]*[\r\n])*/Image\b",
        RegexOptions.Compiled);

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

        // PDF name objects may hex-escape characters (e.g. /J#61vaScript), so decode #xx before matching.
        var text = NameHexEscape.Replace(
            Encoding.Latin1.GetString(bytes),
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        foreach (var marker in ActiveContentMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                throw new EvidenceIntakeException(
                    $"Document '{document.Name}' contains active or embedded content ({marker}); it is rejected.",
                    EvidenceFailureKind.ActiveContent,
                    true);
            }
        }

        // Inline images (BI ... EI) are detected from decoded page content in ExtractDocument, not raw bytes.
        if (ImageSubtype.IsMatch(text))
        {
            throw new EvidenceIntakeException(
                $"Document '{document.Name}' contains an image payload (image XObject or inline image); it is rejected.",
                EvidenceFailureKind.ActiveContent,
                true);
        }
    }

    private static readonly Regex NameHexEscape = new("#([0-9A-Fa-f]{2})", RegexOptions.Compiled);

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

            if (pages == 0)
            {
                throw new EvidenceIntakeException(
                    $"Document '{document.Name}' has no pages.",
                    EvidenceFailureKind.Corrupt,
                    true);
            }

            var text = new StringBuilder();
            for (var page = 1; page <= pages; page++)
            {
                var pdfPage = pdf.GetPage(page);
                // Decoded content streams expose inline images even when compressed; reject them as image payloads.
                if (pdfPage.GetImages().Any())
                {
                    throw new EvidenceIntakeException(
                        $"Document '{document.Name}' contains an image payload (image XObject or inline image); it is rejected.",
                        EvidenceFailureKind.ActiveContent,
                        true);
                }

                text.AppendLine(pdfPage.Text);
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
