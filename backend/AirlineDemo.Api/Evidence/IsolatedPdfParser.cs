using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

using AirlineDemo.Api.Evidence.PdfWorker;

namespace AirlineDemo.Api.Evidence;

public sealed class IsolatedPdfParserOptions
{
    public long MemoryLimitBytes { get; init; } = 512L * 1024 * 1024;

    public TimeSpan WallClockLimit { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Largest JSON response the parent accepts from the child before it kills the child.</summary>
    public long OutputLimitBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>Null runs the application's own worker entry point; tests may substitute another program.</summary>
    public string? WorkerFileName { get; init; }

    public IReadOnlyList<string> WorkerArguments { get; init; } = Array.Empty<string>();

    public static IsolatedPdfParserOptions Default { get; } = new();
}

/// <summary>
/// Parses PDF page text in a bounded worker process after checking compressed stream expansion.
/// </summary>
public static class IsolatedPdfParser
{
    private const int PollMilliseconds = 25;
    private const int MaxDecodedStreamBytes = 8 * 1024 * 1024;
    // Bound PdfPig's per-glyph allocations before any page text is materialized.
    private const int MaxTextStreamBytes = 512 * 1024;
    private const string PdfReadFailureReason = "PdfPig could not open or read the document; retry is possible";
    private const string PdfStreamLimitReason = "PDF stream content exceeds 8388608 decoded bytes; retry is possible";
    private const string PdfTextLimitReason = "PDF text source exceeds 524288 bytes; retry is possible";
    private static readonly Regex DirectStreamLength = new(@"/Length\s+([0-9]+)\s*(?=/|>>)", RegexOptions.CultureInvariant);
    private static readonly Regex EscapedPdfName = new(@"#[0-9A-Fa-f]{2}", RegexOptions.CultureInvariant);
    private static readonly Regex SingleFlateFilter = new(
        @"/Filter\s+(?:/FlateDecode|\[\s*/FlateDecode\s*\])(?:\s|/|>>|\[)",
        RegexOptions.CultureInvariant);
    private static readonly Regex CMapToken = new(@"<(?<value>[0-9A-Fa-f]+)>|(?<bracket>[\[\]])",
        RegexOptions.CultureInvariant);

    private readonly record struct PdfObjectReference(int Number, int Generation);

    public static (EvidenceProcessingState State, string Reason, string Text) Parse(
        byte[] content,
        IsolatedPdfParserOptions? options = null)
        => Parse(content, "document.pdf", options);

    public static (EvidenceProcessingState State, string Reason, string Text) Parse(
        byte[] content,
        string fileName,
        IsolatedPdfParserOptions? options = null)
        => Parse(content, fileName, options ?? IsolatedPdfParserOptions.Default, startWorker: Process.Start);

    internal static (EvidenceProcessingState State, string Reason, string Text) Parse(
        byte[] content,
        string fileName,
        IsolatedPdfParserOptions options,
        Func<ProcessStartInfo, Process?> startWorker)
    {
        if (content.LongLength > EvidenceIntake.MaxDocumentBytes)
        {
            return (EvidenceProcessingState.RejectedPermanent,
                $"{fileName} is {content.LongLength} bytes; limit is {EvidenceIntake.MaxDocumentBytes}",
                string.Empty);
        }

        try
        {
            var streamCheck = CheckStreamLimits(content);
            if (streamCheck.TextExceeded)
            {
                return Retryable(PdfTextLimitReason);
            }

            if (streamCheck.Exceeded)
            {
                return Retryable(PdfStreamLimitReason);
            }

            if (!streamCheck.Safe)
            {
                return Retryable(PdfReadFailureReason);
            }

            if (CannotIsolateWorker(
                OperatingSystem.IsLinux(),
                options.WorkerFileName is not null,
                Environment.UserName,
                File.Exists(SetprivPath)))
            {
                return Retryable("worker cannot be isolated; retry is possible");
            }

            var workingDirectory = ChooseWorkingDirectory();
            try
            {
                Directory.CreateDirectory(workingDirectory);
                RestrictWorkingDirectory(workingDirectory);
                using var process = startWorker(CreateStartInfo(options, workingDirectory));
                if (process is null)
                {
                    return Retryable("worker could not be started; retry is possible");
                }

                if (OperatingSystem.IsWindows())
                {
                    if (!WindowsJobObject.TryAssign(process, options.MemoryLimitBytes, out var job))
                    {
                        return Terminate(process, "could not apply the memory limit");
                    }

                    using (job)
                    {
                        return Supervise(process, content, options);
                    }
                }

                return Supervise(process, content, options);
            }
            finally
            {
                if (Directory.Exists(workingDirectory))
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(workingDirectory,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }

                    Directory.Delete(workingDirectory, recursive: true);
                }
            }
        }
        catch (OutOfMemoryException)
        {
            return Retryable(PdfReadFailureReason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Retryable(PdfReadFailureReason);
        }
    }

    internal static (EvidenceProcessingState State, string Reason, string Text) Parse(
        byte[] content,
        string fileName,
        Func<byte[], PdfDocument> openDocument) =>
        ParseCore(content, fileName, openDocument, page => page.Text,
            page => page.Letters.Select(letter => letter.Value));

    internal static (EvidenceProcessingState State, string Reason, string Text) Parse(
        byte[] content,
        string fileName,
        Func<byte[], PdfDocument> openDocument,
        Func<Page, string> getPageText) =>
        ParseCore(content, fileName, openDocument, getPageText, page => page.Letters.Select(letter => letter.Value));

    internal static (EvidenceProcessingState State, string Reason, string Text) Parse(
        byte[] content,
        string fileName,
        Func<byte[], PdfDocument> openDocument,
        Func<Page, string> getPageText,
        Func<Page, IEnumerable<string>> getTextParts) =>
        ParseCore(content, fileName, openDocument, getPageText, getTextParts);

    private static PdfDocument OpenDocument(byte[] content) =>
        PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true });

    private static (EvidenceProcessingState State, string Reason, string Text) ParseCore(
        byte[] content,
        string fileName,
        Func<byte[], PdfDocument> openDocument,
        Func<Page, string> getPageText,
        Func<Page, IEnumerable<string>> getTextParts)
    {
        if (content.LongLength > EvidenceIntake.MaxDocumentBytes)
        {
            return (EvidenceProcessingState.RejectedPermanent,
                $"{fileName} is {content.LongLength} bytes; limit is {EvidenceIntake.MaxDocumentBytes}",
                string.Empty);
        }

        try
        {
            var streamCheck = CheckStreamLimits(content);
            if (streamCheck.TextExceeded)
            {
                return Retryable(PdfTextLimitReason);
            }

            if (streamCheck.Exceeded)
            {
                return Retryable(PdfStreamLimitReason);
            }

            if (!streamCheck.Safe)
            {
                return Retryable(PdfReadFailureReason);
            }

            using var pdf = openDocument(content);
            if (pdf.NumberOfPages > EvidenceIntake.MaxPagesPerDocument)
            {
                return Retryable($"{pdf.NumberOfPages} pages; limit is {EvidenceIntake.MaxPagesPerDocument}");
            }

            var text = new System.Text.StringBuilder();
            var pageIndex = 0;
            foreach (var page in pdf.GetPages())
            {
                if (page.ExperimentalAccess.GetAnnotations().Any())
                {
                    return (EvidenceProcessingState.RejectedPermanent, "annotations are not accepted", string.Empty);
                }

                if (pageIndex++ > 0)
                {
                    text.Append(EvidenceIntake.PageSeparator);
                }

                if (!CanMaterializePageText(getTextParts(page), text.Length))
                {
                    return Retryable(PdfStreamLimitReason);
                }

                var pageText = getPageText(page);
                if (pageText.Length > MaxDecodedStreamBytes - text.Length)
                {
                    return Retryable(PdfStreamLimitReason);
                }

                text.Append(pageText);
            }

            return (EvidenceProcessingState.Complete, "accepted", text.ToString());
        }
        catch (OutOfMemoryException)
        {
            return Retryable(PdfReadFailureReason);
        }
        catch (Exception)
        {
            return Retryable(PdfReadFailureReason);
        }
    }

    // Reject ambiguous stream layouts and cap decoded bytes before PdfPig can materialize page content.
    private static (bool Safe, bool Exceeded, bool TextExceeded) CheckStreamLimits(byte[] content)
    {
        var decodedBytes = 0;
        var textStreams = new List<(PdfObjectReference Reference, int Bytes)>();
        var streamRanges = new List<(int Start, int End)>();
        var streamReferences = new HashSet<PdfObjectReference>();
        var offset = 0;
        while ((offset = FindPdfToken(content, "stream", offset)) >= 0)
        {
            var streamStart = offset;
            var objectBoundary = LastIndexOfPdfToken(content, "endobj", streamStart);
            var objectSearchStart = objectBoundary < 0 ? 0 : objectBoundary + "endobj".Length;
            var objectStart = FindPdfToken(content, "obj", objectSearchStart);
            if (objectStart <= objectBoundary)
            {
                offset = streamStart + "stream".Length;
                continue;
            }

            var headerStart = objectStart + "obj".Length;
            var header = Encoding.Latin1.GetString(content, headerStart, streamStart - headerStart);
            if (!TryGetObjectReferenceBefore(content, objectStart, out var streamReference))
            {
                return (false, false, false);
            }

            streamReferences.Add(streamReference);
            if (!header.TrimEnd().EndsWith(">>", StringComparison.Ordinal))
            {
                return (false, false, false);
            }

            if (EscapedPdfName.IsMatch(header))
            {
                return (false, false, false);
            }

            var hasFilter = header.Contains("/Filter", StringComparison.Ordinal);
            var lengthMatch = DirectStreamLength.Match(header);
            var streamLength = -1;
            var hasDirectLength = lengthMatch.Success
                && int.TryParse(lengthMatch.Groups[1].Value, out streamLength)
                && streamLength >= 0;
            if (hasFilter && !SingleFlateFilter.IsMatch(header))
            {
                return (false, false, false);
            }

            if (hasFilter && SingleFlateFilter.IsMatch(header)
                && (CountOccurrences(header, "<<") != 1
                    || CountOccurrences(header, "/Length") != 1
                    || CountOccurrences(header, "/Filter") != 1))
            {
                return (false, false, false);
            }

            if (!hasDirectLength)
            {
                return (false, false, false);
            }

            var dataStart = streamStart + "stream".Length;
            if (dataStart < content.Length && content[dataStart] == '\r') dataStart++;
            if (dataStart < content.Length && content[dataStart] == '\n') dataStart++;
            var length = streamLength;
            if (length > content.Length - dataStart)
            {
                return (false, false, false);
            }

            var textBytes = 0;
            if (hasFilter)
            {
                using var compressed = new MemoryStream(content, dataStart, length, writable: false);
                using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
                using var decoded = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var read = inflater.Read(buffer, 0, Math.Min(buffer.Length, MaxDecodedStreamBytes - decodedBytes + 1));
                    if (read == 0) break;
                    decodedBytes += read;
                    if (decodedBytes > MaxDecodedStreamBytes) return (true, true, false);
                    decoded.Write(buffer, 0, read);
                }

                var decodedContent = decoded.GetBuffer().AsSpan(0, (int)decoded.Length);
                if (!HasBoundedCMapExpansion(decodedContent)) return (false, false, false);
                if (!header.Contains("/Subtype /Image", StringComparison.Ordinal)
                    && HasTextShowingOperator(decodedContent))
                {
                    textBytes = (int)decoded.Length;
                }
            }
            else
            {
                decodedBytes += length;
                if (decodedBytes > MaxDecodedStreamBytes) return (true, true, false);
                var streamContent = content.AsSpan(dataStart, length);
                if (!HasBoundedCMapExpansion(streamContent)) return (false, false, false);
                if (!header.Contains("/Subtype /Image", StringComparison.Ordinal)
                    && HasTextShowingOperator(streamContent))
                {
                    textBytes = length;
                }
            }

            if (header.Contains("/Subtype /Form", StringComparison.Ordinal))
            {
                // Form XObjects can execute repeatedly through Do operators without repeated object references.
                return (false, false, false);
            }
            if (header.Contains("/Type /ObjStm", StringComparison.Ordinal))
            {
                // Compressed dictionaries cannot be reference-counted by the bounded source scanner.
                return (false, false, false);
            }

            var afterData = dataStart + length;
            var end = FindPdfToken(content, "endstream", afterData);
            if (end < 0) return (false, false, false);
            for (var i = afterData; i < end; i++)
            {
                if (!IsPdfWhitespace(content[i])) return (false, false, false);
            }

            streamRanges.Add((dataStart, end + "endstream".Length));
            if (textBytes > 0)
            {
                textStreams.Add((streamReference, textBytes));
            }

            offset = end + "endstream".Length;
        }

        var (referenceCounts, referencesAreSafe) =
            CountIndirectReferences(content, streamRanges, streamReferences);
        if (!referencesAreSafe) return (false, false, false);

        long expandedTextBytes = 0;
        foreach (var (reference, bytes) in textStreams)
        {
            var executions = referenceCounts.TryGetValue(reference, out var count) ? count : 1;
            expandedTextBytes += (long)bytes * executions;
            if (expandedTextBytes > MaxTextStreamBytes)
            {
                return (false, false, true);
            }
        }

        return (true, false, false);
    }

    private static bool TryGetObjectReferenceBefore(
        byte[] content,
        int objectTokenStart,
        out PdfObjectReference reference)
    {
        reference = default;
        var position = objectTokenStart - 1;
        while (position >= 0 && IsPdfWhitespace(content[position])) position--;
        var generationEnd = position + 1;
        while (position >= 0 && content[position] is >= (byte)'0' and <= (byte)'9') position--;
        if (generationEnd == position + 1) return false;
        var generationStart = position + 1;

        while (position >= 0 && IsPdfWhitespace(content[position])) position--;
        var numberEnd = position + 1;
        while (position >= 0 && content[position] is >= (byte)'0' and <= (byte)'9') position--;
        if (numberEnd == position + 1
            || !int.TryParse(Encoding.ASCII.GetString(content, generationStart, generationEnd - generationStart),
                NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || !int.TryParse(Encoding.ASCII.GetString(content, position + 1, numberEnd - position - 1),
                NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        reference = new PdfObjectReference(number, generation);
        return true;
    }

    private static (Dictionary<PdfObjectReference, long> Counts, bool Safe) CountIndirectReferences(
        byte[] content,
        IReadOnlyList<(int Start, int End)> streamRanges,
        IReadOnlySet<PdfObjectReference> streamReferences)
    {
        var counts = new Dictionary<PdfObjectReference, long>();
        var rangeIndex = 0;
        string? previous = null;
        string? beforePrevious = null;
        var readingContentsReference = false;
        var contentsReferencePart = 0;
        var contentsObjectNumber = 0;
        var contentsGeneration = 0;
        var inContentsArray = false;
        var currentObjectIsPage = false;
        var currentObjectHasUnsafeContents = false;
        var readingTypeName = false;
        var referencesAreSafe = true;
        var index = 0;
        while (index < content.Length)
        {
            while (rangeIndex < streamRanges.Count && index >= streamRanges[rangeIndex].End) rangeIndex++;
            if (rangeIndex < streamRanges.Count
                && index >= streamRanges[rangeIndex].Start
                && index < streamRanges[rangeIndex].End)
            {
                index = streamRanges[rangeIndex].End;
                previous = null;
                beforePrevious = null;
                continue;
            }

            var value = content[index];
            if (IsPdfWhitespace(value))
            {
                index++;
                continue;
            }

            if (value == '%')
            {
                while (index < content.Length && content[index] is not (10 or 13)) index++;
                continue;
            }

            if (value == '(')
            {
                if (readingContentsReference)
                {
                    currentObjectHasUnsafeContents = true;
                    readingContentsReference = false;
                }

                SkipLiteralString(content, ref index);
                beforePrevious = previous;
                previous = null;
                continue;
            }

            if (value == '<' && (index + 1 >= content.Length || content[index + 1] != '<'))
            {
                if (readingContentsReference)
                {
                    currentObjectHasUnsafeContents = true;
                    readingContentsReference = false;
                }

                index++;
                while (index < content.Length && content[index] != '>') index++;
                if (index < content.Length) index++;
                beforePrevious = previous;
                previous = null;
                continue;
            }

            if (value == '/')
            {
                index++;
                var nameStart = index;
                while (index < content.Length && !IsPdfDelimiter(content[index])) index++;
                var name = Encoding.ASCII.GetString(content, nameStart, index - nameStart);
                if (name.Contains('#')) referencesAreSafe = false;
                if (readingContentsReference)
                {
                    currentObjectHasUnsafeContents = true;
                    readingContentsReference = false;
                }

                if (readingTypeName)
                {
                    currentObjectIsPage |= name == "Page";
                    readingTypeName = false;
                }

                if (name == "Type") readingTypeName = true;
                if (name == "Contents")
                {
                    readingContentsReference = true;
                    contentsReferencePart = 0;
                }

                beforePrevious = previous;
                previous = null;
                continue;
            }

            if (IsPdfDelimiter(value))
            {
                if (value == (byte)'[' && readingContentsReference)
                {
                    readingContentsReference = false;
                    inContentsArray = true;
                }
                else if (value == (byte)']' && inContentsArray)
                {
                    inContentsArray = false;
                }
                else if (readingContentsReference)
                {
                    currentObjectHasUnsafeContents = true;
                    readingContentsReference = false;
                }

                index += value == (byte)'<' && index + 1 < content.Length && content[index + 1] == (byte)'<'
                    || value == (byte)'>' && index + 1 < content.Length && content[index + 1] == (byte)'>'
                    ? 2
                    : 1;
                beforePrevious = previous;
                previous = null;
                continue;
            }

            var tokenStart = index;
            while (index < content.Length && !IsPdfDelimiter(content[index])) index++;
            var token = Encoding.ASCII.GetString(content, tokenStart, index - tokenStart);
            if (token == "endobj")
            {
                if (currentObjectIsPage && currentObjectHasUnsafeContents) referencesAreSafe = false;
                currentObjectIsPage = false;
                currentObjectHasUnsafeContents = false;
                readingTypeName = false;
                readingContentsReference = false;
                inContentsArray = false;
            }
            else if (token == "obj")
            {
                currentObjectIsPage = false;
                currentObjectHasUnsafeContents = false;
                readingTypeName = false;
                readingContentsReference = false;
                inContentsArray = false;
            }

            if (readingContentsReference)
            {
                if (contentsReferencePart == 0
                    && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out contentsObjectNumber))
                {
                    contentsReferencePart = 1;
                }
                else if (contentsReferencePart == 1
                    && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out contentsGeneration))
                {
                    contentsReferencePart = 2;
                }
                else if (contentsReferencePart == 2 && token == "R")
                {
                    if (!streamReferences.Contains(new PdfObjectReference(contentsObjectNumber, contentsGeneration)))
                    {
                        currentObjectHasUnsafeContents = true;
                    }

                    readingContentsReference = false;
                }
                else
                {
                    currentObjectHasUnsafeContents = true;
                    readingContentsReference = false;
                }
            }

            if (token == "R"
                && int.TryParse(beforePrevious, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && int.TryParse(previous, NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
            {
                var reference = new PdfObjectReference(number, generation);
                if (inContentsArray && !streamReferences.Contains(reference))
                {
                    currentObjectHasUnsafeContents = true;
                }

                counts[reference] = counts.TryGetValue(reference, out var current) ? current + 1 : 1;
            }

            beforePrevious = previous;
            previous = token;
        }

        if (currentObjectIsPage && currentObjectHasUnsafeContents) referencesAreSafe = false;
        return (counts, referencesAreSafe);
    }

    private static void SkipLiteralString(byte[] content, ref int index)
    {
        var depth = 1;
        index++;
        while (index < content.Length && depth > 0)
        {
            if (content[index] == (byte)'\\')
            {
                index += Math.Min(2, content.Length - index);
            }
            else if (content[index++] == (byte)'(')
            {
                depth++;
            }
            else if (content[index - 1] == (byte)')')
            {
                depth--;
            }
        }
    }

    private static bool HasBoundedCMapExpansion(ReadOnlySpan<byte> stream)
    {
        var text = Encoding.Latin1.GetString(stream);
        var clean = text.ToCharArray();
        var index = 0;
        var bodyStart = 0;
        var kind = 0;
        while (index < text.Length)
        {
            var value = text[index];
            if (value == '%')
            {
                while (index < text.Length && text[index] is not ('\r' or '\n')) clean[index++] = ' ';
                continue;
            }

            if (value == '(')
            {
                var depth = 1;
                index++;
                while (index < text.Length && depth > 0)
                {
                    if (text[index] == '\\') index = Math.Min(index + 2, text.Length);
                    else if (text[index++] == '(') depth++;
                    else if (text[index - 1] == ')') depth--;
                }

                if (depth != 0 && kind != 0) return false;
                continue;
            }

            if (value == '<')
            {
                if (index + 1 < text.Length && text[index + 1] == '<') index += 2;
                else
                {
                    index++;
                    while (index < text.Length && text[index] != '>') index++;
                    if (index == text.Length) return kind == 0;
                    index++;
                }

                continue;
            }

            if (value == '/')
            {
                index++;
                while (index < text.Length && !IsPdfDelimiter((byte)text[index])) index++;
                continue;
            }

            if (IsPdfDelimiter((byte)value))
            {
                index++;
                continue;
            }

            var tokenStart = index;
            while (index < text.Length && !IsPdfDelimiter((byte)text[index])) index++;
            var token = text.AsSpan(tokenStart, index - tokenStart);
            var startsChar = token.SequenceEqual("beginbfchar");
            var startsRange = token.SequenceEqual("beginbfrange");
            var endsChar = token.SequenceEqual("endbfchar");
            var endsRange = token.SequenceEqual("endbfrange");
            if (kind == 0)
            {
                if (endsChar || endsRange) return false;
                if (startsChar || startsRange)
                {
                    kind = startsChar ? 1 : 2;
                    bodyStart = index;
                }
            }
            else if (startsChar || startsRange || (endsChar || endsRange) && (endsChar ? kind != 1 : kind != 2))
            {
                return false;
            }
            else if (kind == 1 && endsChar || kind == 2 && endsRange)
            {
                if (!IsBoundedCMapBody(new string(clean, bodyStart, tokenStart - bodyStart), kind)) return false;
                kind = 0;
            }
        }

        return kind == 0;
    }

    private static bool IsBoundedCMapBody(string body, int kind)
    {
        var tokens = CMapToken.Matches(body);
        if (!string.IsNullOrWhiteSpace(CMapToken.Replace(body, string.Empty))) return false;
        if (kind == 1)
        {
            if (tokens.Count % 2 != 0) return false;
            for (var i = 0; i < tokens.Count; i += 2)
            {
                if (!TryGetHexToken(tokens[i], out var source) || !TryGetHexToken(tokens[i + 1], out var target)
                    || !IsBoundedUnicodeMapping(source, source, target, false)) return false;
            }

            return true;
        }

        var tokenIndex = 0;
        while (tokenIndex < tokens.Count)
        {
            if (!TryGetHexToken(tokens[tokenIndex++], out var sourceStart) || tokenIndex >= tokens.Count
                || !TryGetHexToken(tokens[tokenIndex++], out var sourceEnd)
                || sourceStart.Length != sourceEnd.Length || sourceStart.Length == 0
                || sourceStart.Length > 8 || sourceStart.Length % 2 != 0
                || !uint.TryParse(sourceStart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start)
                || !uint.TryParse(sourceEnd, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end)
                || start > end || tokenIndex >= tokens.Count) return false;

            if (tokens[tokenIndex].Groups["bracket"].Value == "[")
            {
                tokenIndex++;
                var hasValue = false;
                while (tokenIndex < tokens.Count && tokens[tokenIndex].Groups["bracket"].Value != "]")
                {
                    if (!TryGetHexToken(tokens[tokenIndex++], out var target)
                        || !IsBoundedUnicodeMapping(sourceStart, sourceEnd, target, false)) return false;
                    hasValue = true;
                }

                if (!hasValue || tokenIndex >= tokens.Count) return false;
                tokenIndex++;
            }
            else if (!TryGetHexToken(tokens[tokenIndex++], out var target)
                || !IsBoundedUnicodeMapping(sourceStart, sourceEnd, target, true)) return false;
        }

        return true;
    }

    private static bool TryGetHexToken(Match token, out string value)
    {
        value = token.Groups["value"].Value;
        return value.Length > 0;
    }

    private static bool IsBoundedUnicodeMapping(string sourceStart, string sourceEnd, string target, bool incrementing)
    {
        var sourceBytes = sourceStart.Length / 2;
        if (sourceBytes == 0 || sourceStart.Length % 2 != 0 || target.Length == 0 || target.Length % 4 != 0
            || target.Length / 4 > sourceBytes) return false;
        if (!incrementing || sourceBytes != 1 || target.Length != 4) return true;
        return ushort.TryParse(target, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first)
            && uint.TryParse(sourceStart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start)
            && uint.TryParse(sourceEnd, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end)
            && first + end - start <= ushort.MaxValue;
    }

    private static bool HasTextShowingOperator(ReadOnlySpan<byte> stream)
    {
        var index = 0;
        while (index < stream.Length)
        {
            var value = stream[index];
            if (IsPdfWhitespace(value))
            {
                index++;
                continue;
            }

            if (value == '%')
            {
                while (index < stream.Length && stream[index] is not (10 or 13)) index++;
                continue;
            }

            if (value == '(')
            {
                var depth = 1;
                index++;
                while (index < stream.Length && depth > 0)
                {
                    if (stream[index] == '\\')
                    {
                        index += Math.Min(2, stream.Length - index);
                    }
                    else
                    {
                        var literalValue = stream[index++];
                        if (literalValue == '(') depth++;
                        else if (literalValue == ')') depth--;
                    }
                }

                continue;
            }

            if (value == '<')
            {
                if (index + 1 < stream.Length && stream[index + 1] == '<')
                {
                    index += 2;
                }
                else
                {
                    index++;
                    while (index < stream.Length && stream[index] != '>') index++;
                    if (index < stream.Length) index++;
                }

                continue;
            }

            if (value == '/')
            {
                index++;
                while (index < stream.Length && !IsPdfDelimiter(stream[index])) index++;
                continue;
            }

            if (value is (byte)'\'' or (byte)'"') return true;
            if (IsPdfDelimiter(value))
            {
                index += value == (byte)'>' && index + 1 < stream.Length
                    && stream[index + 1] == (byte)'>' ? 2 : 1;
                continue;
            }

            var tokenStart = index;
            while (index < stream.Length && !IsPdfDelimiter(stream[index])) index++;
            var token = stream.Slice(tokenStart, index - tokenStart);
            if (token.SequenceEqual("Tj"u8) || token.SequenceEqual("TJ"u8)) return true;
        }

        return false;
    }

    private static bool CanMaterializePageText(IEnumerable<string> textParts, int existingTextLength)
    {
        long maximumTextLength = existingTextLength;
        foreach (var textPart in textParts)
        {
            maximumTextLength += textPart.Length + 4L;
            if (maximumTextLength > MaxDecodedStreamBytes)
            {
                return false;
            }
        }

        return true;
    }

    private static int CountOccurrences(string value, string match)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(match, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += match.Length;
        }

        return count;
    }

    private static int FindPdfToken(byte[] content, string token, int start)
    {
        var tokenBytes = Encoding.ASCII.GetBytes(token);
        for (var index = start; index <= content.Length - tokenBytes.Length; index++)
        {
            if (!HasPdfTokenAt(content, tokenBytes, index)) continue;
            return index;
        }

        return -1;
    }

    private static int LastIndexOfPdfToken(byte[] content, string token, int before)
    {
        var tokenBytes = Encoding.ASCII.GetBytes(token);
        for (var index = Math.Min(before - tokenBytes.Length, content.Length - tokenBytes.Length); index >= 0; index--)
        {
            if (HasPdfTokenAt(content, tokenBytes, index)) return index;
        }

        return -1;
    }

    private static bool HasPdfTokenAt(byte[] content, byte[] token, int index)
    {
        if (index > 0 && !IsPdfDelimiter(content[index - 1])) return false;
        if (index + token.Length < content.Length && !IsPdfDelimiter(content[index + token.Length])) return false;
        for (var i = 0; i < token.Length; i++)
        {
            if (content[index + i] != token[i]) return false;
        }

        return true;
    }

    private static bool IsPdfDelimiter(byte value) =>
        value is 0 or 9 or 10 or 12 or 13 or 32 or 37 or 40 or 41 or 47 or 60 or 62 or 91 or 93 or 123 or 125;

    private static bool IsPdfWhitespace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    /// <summary>
    /// Builds the child's start info: no shell, no inherited environment, redirected stdin/stdout. On Linux the child
    /// runs under <c>prlimit --data</c>, a kernel-enforced cap on its private memory, so a decompression spike is
    /// stopped by the kernel rather than by sampling. The default worker also runs in a network namespace.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(IsolatedPdfParserOptions options, string workingDirectory)
    {
        var fileName = options.WorkerFileName ?? ResolveHost();
        var program = fileName;
        var wrapper = new List<string>();
        if (OperatingSystem.IsLinux())
        {
            var chain = new List<string>();
            if (options.WorkerFileName is null && NetworkNamespaceAvailable.Value)
            {
                // A network namespace with no interfaces: the child has no network access.
                chain.AddRange(new[] { UnsharePath, UnshareFlags(), "--" });
            }

            if (File.Exists(PrlimitPath))
            {
                chain.AddRange(new[] { PrlimitPath, $"--data={options.MemoryLimitBytes}", "--" });
            }

            // Root bypasses directory mode bits, so a root worker could write the read-only directory: drop to nobody.
            if (options.WorkerFileName is null && DropsToNobody())
            {
                chain.AddRange(new[] { SetprivPath, $"--reuid={NobodyUid}", $"--regid={NobodyUid}", "--clear-groups", "--" });
            }

            if (chain.Count > 0)
            {
                program = chain[0];
                wrapper.AddRange(chain.Skip(1));
                wrapper.Add(fileName);
            }
        }

        var startInfo = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        foreach (var argument in wrapper)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (options.WorkerFileName is null)
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(fileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(typeof(IsolatedPdfParser).Assembly.Location);
            }

            startInfo.ArgumentList.Add(PdfWorkerEntry.Switch);
        }
        else
        {
            foreach (var argument in options.WorkerArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        startInfo.Environment.Clear();
        return startInfo;
    }

    /// <summary>
    /// A fresh, empty temp directory for one run. The parent creates it and deletes it after the child exits.
    /// </summary>
    internal static string ChooseWorkingDirectory() =>
        Path.Combine(Path.GetTempPath(), "pdf-worker-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Makes the child's working directory read-only: a Windows deny ACE, or mode 0555 on Unix. On Unix the child runs
    /// as <see cref="NobodyUid"/> (see <see cref="CreateStartInfo"/>) so the mode bits apply to it even when the API is root.
    /// </summary>
    internal static void RestrictWorkingDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            DenyWrite(directory);
        }
        else
        {
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>Denies the child write access to its working directory on Windows.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DenyWrite(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
            System.Security.AccessControl.FileSystemRights.WriteData
                | System.Security.AccessControl.FileSystemRights.AppendData
                | System.Security.AccessControl.FileSystemRights.CreateFiles
                | System.Security.AccessControl.FileSystemRights.CreateDirectories,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    private const string UnsharePath = "/usr/bin/unshare";

    private const string PrlimitPath = "/usr/bin/prlimit";

    private const string SetprivPath = "/usr/bin/setpriv";

    /// <summary>True when the default Linux worker is started as root and can be switched to <see cref="NobodyUid"/>.</summary>
    internal static bool DropsToNobody() => Environment.UserName == "root" && File.Exists(SetprivPath);

    /// <summary>True when the default Linux worker would run as root with no way to drop to <see cref="NobodyUid"/>.</summary>
    internal static bool CannotIsolateWorker(bool linux, bool customWorker, string userName, bool setprivAvailable) =>
        linux && !customWorker && userName == "root" && !setprivAvailable;

    /// <summary>Conventional uid/gid of the unprivileged "nobody" account the worker switches to when the API is root.</summary>
    private const int NobodyUid = 65534;

    /// <summary>
    /// Root creates a plain network namespace: <c>-r</c> would map root into a user namespace where the later switch to
    /// <see cref="NobodyUid"/> has no mapping and fails. Other users need <c>-r</c> to create the namespace at all.
    /// </summary>
    internal static string UnshareFlags() => Environment.UserName == "root" ? "-n" : "-rn";

    /// <summary>True when Linux allows an unprivileged network namespace; checked once per process.</summary>
    private static readonly Lazy<bool> NetworkNamespaceAvailable = new(ProbeNetworkNamespace);

    private static bool ProbeNetworkNamespace()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(UnsharePath))
        {
            return false;
        }

        try
        {
            using var probe = Process.Start(new ProcessStartInfo(UnsharePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { UnshareFlags(), "--", "true" },
            });
            if (probe is null)
            {
                return false;
            }

            _ = probe.StandardError.ReadToEndAsync();
            _ = probe.StandardOutput.ReadToEndAsync();
            return probe.WaitForExit(5000) && probe.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>The dotnet host is used when the app runs under it; otherwise the app's own executable.</summary>
    private static string ResolveHost()
    {
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(dotnetHost) && File.Exists(dotnetHost))
        {
            return dotnetHost;
        }

        return Environment.ProcessPath ?? throw new InvalidOperationException("process path is unavailable");
    }

    private static (EvidenceProcessingState State, string Reason, string Text) Supervise(
        Process process,
        byte[] content,
        IsolatedPdfParserOptions options)
    {
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, options.OutputLimitBytes);
        _ = process.StandardError.ReadToEndAsync();
        _ = WriteInputAsync(process.StandardInput, content);

        var clock = Stopwatch.StartNew();
        while (!process.WaitForExit(PollMilliseconds))
        {
            if (clock.Elapsed > options.WallClockLimit)
            {
                return Terminate(process, $"exceeded the {options.WallClockLimit.TotalSeconds:0} second time limit");
            }

            if (WorkingSetBytes(process) > options.MemoryLimitBytes)
            {
                return Terminate(process, "exceeded the memory limit");
            }

            if (stdout.IsCompleted && stdout.Result.Overflow)
            {
                return Terminate(process, "exceeded the output size limit");
            }
        }

        // The child can exit just after the deadline; the limit applies to the whole run, so check it again here.
        if (clock.Elapsed > options.WallClockLimit)
        {
            return Terminate(process, $"exceeded the {options.WallClockLimit.TotalSeconds:0} second time limit");
        }

        if (!stdout.Wait(TimeSpan.FromSeconds(5)))
        {
            return Retryable("worker output did not close; retry is possible");
        }

        if (stdout.Result.Overflow)
        {
            return Retryable("worker output exceeded the output size limit; retry is possible");
        }

        if (process.ExitCode != 0)
        {
            return Retryable($"worker failed with exit code {process.ExitCode}; retry is possible");
        }

        PdfWorkerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<PdfWorkerResponse>(stdout.Result.Data);
        }
        catch (JsonException)
        {
            response = null;
        }

        if (response is null || !Enum.TryParse<EvidenceProcessingState>(response.State, out var state))
        {
            return Retryable("worker returned an invalid result; retry is possible");
        }

        return (state, response.Reason, response.Text ?? string.Empty);
    }

    private static (EvidenceProcessingState State, string Reason, string Text) Terminate(Process process, string reason)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        return Retryable($"worker {reason}; the document was not accepted, retry is possible");
    }

    private static long WorkingSetBytes(Process process)
    {
        try
        {
            process.Refresh();
            return process.WorkingSet64;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>Reads the child's stdout up to <paramref name="limit"/> bytes; reports overflow instead of buffering more.</summary>
    private static async Task<(byte[] Data, bool Overflow)> ReadBoundedAsync(Stream stream, long limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    return (Array.Empty<byte>(), true);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (IOException)
        {
            return (Array.Empty<byte>(), false);
        }

        return (buffer.ToArray(), false);
    }

    private static async Task WriteInputAsync(StreamWriter stdin, byte[] content)
    {
        try
        {
            await stdin.BaseStream.WriteAsync(content);
            await stdin.BaseStream.FlushAsync();
        }
        catch (IOException)
        {
        }
        finally
        {
            try
            {
                stdin.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    private static (EvidenceProcessingState State, string Reason, string Text) Retryable(string reason) =>
        (EvidenceProcessingState.RejectedRetryable, reason, string.Empty);
}

/// <summary>
/// A cgroup v2 child of the parent's own cgroup with <c>memory.max</c> set, so the kernel kills a worker that
/// exceeds the limit. Null when the host does not delegate a writable cgroup with the memory controller.
/// </summary>
internal sealed class LinuxCgroup : IDisposable
{
    private readonly string path;

    private LinuxCgroup(string path)
    {
        this.path = path;
    }

    public static LinuxCgroup? TryAssign(Process process, long memoryLimitBytes)
    {
        string? path = null;
        try
        {
            var own = File.ReadAllLines("/proc/self/cgroup").FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal));
            if (own is null)
            {
                return null;
            }

            var parent = Path.GetFullPath("/sys/fs/cgroup" + own[3..]);
            path = Path.Combine(parent, "pdf-worker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "memory.max"), memoryLimitBytes.ToString());
            File.WriteAllText(Path.Combine(path, "memory.swap.max"), "0");
            File.WriteAllText(Path.Combine(path, "memory.oom.group"), "1");
            File.WriteAllText(Path.Combine(path, "cgroup.procs"), process.Id.ToString());
            return new LinuxCgroup(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (path is not null)
            {
                Remove(path);
            }

            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            File.WriteAllText(Path.Combine(path, "cgroup.kill"), "1");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        Remove(path);
    }

    private static void Remove(string directory)
    {
        try
        {
            Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A Windows Job Object that caps the committed memory of one worker process. Kill-on-close ensures the worker
/// dies with the parent's handle even if the parent is interrupted.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    internal const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly IntPtr handle;

    private WindowsJobObject(IntPtr handle)
    {
        this.handle = handle;
    }

    /// <summary>Builds the extended limit structure: a per-process memory cap that kills the job on close.</summary>
    internal static ExtendedLimits BuildLimits(long memoryLimitBytes)
    {
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = JobObjectLimitProcessMemory | JobObjectLimitKillOnJobClose;
        limits.ProcessMemoryLimit = (nuint)memoryLimitBytes;
        return limits;
    }

    /// <summary>Creates a job limited to <paramref name="memoryLimitBytes"/> per process and assigns the started child to it.</summary>
    public static bool TryAssign(Process process, long memoryLimitBytes, out WindowsJobObject? job)
    {
        job = null;
        var handle = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var candidate = new WindowsJobObject(handle);
        var limits = BuildLimits(memoryLimitBytes);

        var size = Marshal.SizeOf<ExtendedLimits>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                candidate.Dispose();
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            candidate.Dispose();
            return false;
        }

        job = candidate;
        return true;
    }

    /// <summary>Reads back whether <paramref name="process"/> is in this job and the limits the kernel holds for it.</summary>
    internal (bool InJob, ExtendedLimits Limits) Inspect(Process process)
    {
        var inJob = IsProcessInJob(process.Handle, handle, out var result) && result;
        var size = Marshal.SizeOf<ExtendedLimits>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return QueryInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size, IntPtr.Zero)
                ? (inJob, Marshal.PtrToStructure<ExtendedLimits>(buffer))
                : (inJob, default);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
