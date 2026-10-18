using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Text.Json;

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

    /// <summary>Called immediately before each worker start; tests count calls to prove a rejected input never reaches a worker.</summary>
    public Action? WorkerStarting { get; init; }

    public static IsolatedPdfParserOptions Default { get; } = new();
}

/// <summary>
/// Parses one PDF in a short-lived child process (the application's <c>--pdf-worker</c> entry point). The PDF is
/// written to the child's standard input and the extracted text is read back as JSON from standard output. The
/// parent never opens the PDF itself. The child is killed when it exceeds the memory or wall-clock limit.
/// </summary>
public static class IsolatedPdfParser
{
    private const int PollMilliseconds = 25;

    public static (EvidenceProcessingState State, string Reason, string Text) Parse(
        byte[] content,
        IsolatedPdfParserOptions? options = null)
    {
        options ??= IsolatedPdfParserOptions.Default;
        if (PreflightStreamFilters(content) is { } rejected)
        {
            return rejected;
        }

        if (PreflightUnicodeGlyphExpansion(content) is { } oversizedUnicode)
        {
            return oversizedUnicode;
        }

        if (PreflightPageContentSources(content) is { } oversizedText)
        {
            return oversizedText;
        }

        // Root bypasses the read-only mode of the working directory, so root without a drop to "nobody" fails closed.
        if (CannotIsolateWorker(OperatingSystem.IsLinux(), options.WorkerFileName is not null, Environment.UserName, File.Exists(SetprivPath)))
        {
            return Retryable("worker cannot be isolated from its working directory; the document was not accepted, retry is possible");
        }

        var workingDirectory = ChooseWorkingDirectory();
        try
        {
            Directory.CreateDirectory(workingDirectory);
            RestrictWorkingDirectory(workingDirectory);

            options.WorkerStarting?.Invoke();
            using var process = Process.Start(CreateStartInfo(options, workingDirectory));
            if (process is null)
            {
                return Retryable("worker could not be started; retry is possible");
            }

            // Windows has no prlimit: a Job Object caps the child's committed memory in the kernel.
            WindowsJobObject? memoryJob = null;
            if (OperatingSystem.IsWindows() && !WindowsJobObject.TryAssign(process, options.MemoryLimitBytes, out memoryJob))
            {
                process.Kill();
                return Retryable("worker memory limit could not be enforced; the document was not accepted, retry is possible");
            }

            // Linux: a cgroup v2 limit caps total memory in the kernel where the host delegates one; otherwise
            // prlimit --data plus the working-set check in Supervise enforce the limit.
            using var memoryCgroup = OperatingSystem.IsLinux() ? LinuxCgroup.TryAssign(process, options.MemoryLimitBytes) : null;
            using (memoryJob)
            {
                return Supervise(process, content, options);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return Retryable("worker could not be started; retry is possible");
        }
        finally
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    // The directory was made read-only before the run; the owner must be able to write again to delete it.
                    File.SetUnixFileMode(workingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

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

    /// <summary>Largest decoded size of one Flate stream the parent lets reach a worker.</summary>
    internal const long DecodedStreamLimitBytes = 8L * 1024 * 1024;

    private const long TextSourceLimitBytes = 512L * 1024;
    private const int UnicodeDestinationLimitBytes = 1024;
    private const int CMapBlockStartLimit = 4096;
    private const string TextSourceLimitReason = "PDF text source exceeds 524288 bytes; retry is possible";
    private const string UnreadableDocumentReason = "PdfPig could not open or read the document; retry is possible";

    private static readonly Regex FilterValue = new(@"/Filter\s*(\[[^\]]*\]|/[^\s/<>\[\]()%]+)", RegexOptions.CultureInvariant);
    private static readonly Regex NameWithEscape = new(@"/[^\s/<>\[\]()%]*#[0-9A-Fa-f]{2}[^\s/<>\[\]()%]*", RegexOptions.CultureInvariant);
    private static readonly Regex FirstName = new(@"/[^\s/<>\[\]()%]+", RegexOptions.CultureInvariant);

    private readonly record struct PdfIndirectObject(
        int Number,
        int Generation,
        int BodyStart,
        int DictionaryEnd,
        int BodyEnd,
        int StreamStart,
        int StreamLength,
        bool HasDeclaredStreamLength,
        (int Number, int Generation)? LengthReference);

    /// <summary>Bounds decoded page-content bytes across all page references before a worker can start.</summary>
    private static (EvidenceProcessingState State, string Reason, string Text)? PreflightPageContentSources(byte[] content)
    {
        var objects = ReadIndirectObjects(content);
        if (objects.Count == 0) return null;
        long totalBytes = 0;
        var streamLengths = new Dictionary<(int Number, int Generation), long>();
        foreach (var page in objects.Values)
        {
            var dictionary = DecodeNameEscapes(content.AsSpan(page.BodyStart, page.DictionaryEnd - page.BodyStart));
            var type = FindPdfDictionaryToken(dictionary, "/Type"u8);
            if (type < 0 || !HasNameValue(dictionary, type + "/Type"u8.Length, "/Page"u8)) continue;
            var contents = FindPdfDictionaryToken(dictionary, "/Contents"u8);
            if (contents < 0) continue;
            var valueStart = SkipPdfWhitespaceAndComments(dictionary, contents + "/Contents"u8.Length);
            if (valueStart >= dictionary.Length) return Retryable(UnreadableDocumentReason);
            List<(int Number, int Generation)> references;
            if (dictionary[valueStart] == '[')
            {
                var arrayReferences = ReadArrayReferences(dictionary, valueStart);
                if (arrayReferences is null) return Retryable(UnreadableDocumentReason);
                references = arrayReferences;
            }
            else
            {
                if (!TryReadReference(dictionary, valueStart, out var reference, out _))
                {
                    if (IsPdfToken(dictionary, valueStart, "null"u8)) continue;
                    return Retryable(UnreadableDocumentReason);
                }

                references = new List<(int Number, int Generation)> { reference };
            }
            foreach (var reference in references)
            {
                if (!objects.TryGetValue(reference, out var stream))
                {
                    return Retryable(UnreadableDocumentReason);
                }
                if (stream.StreamStart < 0)
                {
                    return Retryable(UnreadableDocumentReason);
                }
                if (!stream.HasDeclaredStreamLength)
                {
                    return Retryable(UnreadableDocumentReason);
                }
                if (!streamLengths.TryGetValue(reference, out var sourceBytes))
                {
                    sourceBytes = DecodedContentStreamLength(content, stream, out var readable);
                    if (!readable) return Retryable(UnreadableDocumentReason);
                    streamLengths.Add(reference, sourceBytes);
                }
                if (sourceBytes > TextSourceLimitBytes - totalBytes)
                {
                    return Retryable(TextSourceLimitReason);
                }
                totalBytes += sourceBytes;
            }
        }
        return null;
    }

    /// <summary>Rejects oversized Unicode destinations and excessive CMap blocks before a worker can start.</summary>
    private static (EvidenceProcessingState State, string Reason, string Text)? PreflightUnicodeGlyphExpansion(byte[] content)
    {
        var objects = ReadIndirectObjects(content);
        var scannedStreams = new HashSet<(int Number, int Generation)>();
        foreach (var pdfObject in objects.Values)
        {
            var dictionary = DecodeNameEscapes(content.AsSpan(
                pdfObject.BodyStart, pdfObject.DictionaryEnd - pdfObject.BodyStart));
            var key = FindPdfDictionaryToken(dictionary, "/ToUnicode"u8);
            if (key < 0) continue;

            var valueStart = SkipPdfWhitespaceAndComments(dictionary, key + "/ToUnicode"u8.Length);
            if (!TryReadReference(dictionary, valueStart, out var reference, out _)
                || !objects.TryGetValue(reference, out var cmap)
                || cmap.StreamStart < 0
                || !scannedStreams.Add(reference))
            {
                continue;
            }

            var cmapBytes = content.AsSpan(cmap.StreamStart, cmap.StreamLength).ToArray();
            var cmapDictionary = Encoding.Latin1.GetString(
                content, cmap.BodyStart, cmap.DictionaryEnd - cmap.BodyStart);
            var filter = FilterValue.Match(cmapDictionary);
            if (filter.Success)
            {
                var filters = FirstName.Matches(filter.Groups[1].Value)
                    .Select(match => match.Value)
                    .ToArray();
                if (filters.Length == 0) continue;
                if (filters[0] is "/FlateDecode" or "/Fl")
                {
                    try
                    {
                        using var input = new MemoryStream(cmapBytes, writable: false);
                        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                        using var output = new MemoryStream();
                        var buffer = new byte[81920];
                        int read;
                        while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (output.Length > DecodedStreamLimitBytes - read)
                            {
                                return Retryable(UnreadableDocumentReason);
                            }
                            output.Write(buffer, 0, read);
                        }
                        cmapBytes = output.ToArray();
                    }
                    catch (InvalidDataException)
                    {
                        continue;
                    }
                }
                else
                {
                    continue;
                }
            }

            if (CMapExceedsUnicodeBounds(cmapBytes))
            {
                return Retryable(UnreadableDocumentReason);
            }
        }

        return null;
    }

    /// <summary>Scans CMap tokens once; comments and literal strings cannot supply operators or destinations.</summary>
    private static bool CMapExceedsUnicodeBounds(ReadOnlySpan<byte> cmap)
    {
        const int noBlock = 0;
        const int bfcharBlock = 1;
        const int bfrangeBlock = 2;
        var activeBlock = noBlock;
        var hexOperandIndex = 0;
        var blockStarts = 0;

        for (var position = 0; position < cmap.Length;)
        {
            var value = cmap[position];
            if (IsPdfWhitespace(value))
            {
                position++;
                continue;
            }

            if (value == '%')
            {
                while (position < cmap.Length && cmap[position] is not (byte)'\r' and not (byte)'\n')
                {
                    position++;
                }
                continue;
            }

            if (value == '(')
            {
                position = SkipLiteralString(cmap, position);
                continue;
            }

            if (value == '<')
            {
                if (position + 1 < cmap.Length && cmap[position + 1] == '<')
                {
                    position += 2;
                    continue;
                }

                var destinationOperand = activeBlock == bfcharBlock
                    ? hexOperandIndex % 2 == 1
                    : activeBlock == bfrangeBlock && hexOperandIndex % 3 == 2;
                position++;
                var hexDigits = 0;
                while (position < cmap.Length && cmap[position] != '>')
                {
                    if (Uri.IsHexDigit((char)cmap[position])
                        && ++hexDigits > UnicodeDestinationLimitBytes * 2
                        && destinationOperand)
                    {
                        return true;
                    }
                    position++;
                }
                if (position < cmap.Length) position++;

                if (activeBlock != noBlock)
                {
                    hexOperandIndex++;
                }
                continue;
            }

            if (value == '/')
            {
                position++;
                while (position < cmap.Length && !IsPdfDelimiterOrWhitespace(cmap[position])) position++;
                continue;
            }

            if (IsPdfDelimiterOrWhitespace(value))
            {
                position++;
                continue;
            }

            var tokenStart = position;
            while (position < cmap.Length && !IsPdfDelimiterOrWhitespace(cmap[position])) position++;
            var token = cmap[tokenStart..position];
            if (token.SequenceEqual("beginbfchar"u8) || token.SequenceEqual("beginbfrange"u8))
            {
                blockStarts++;
                if (blockStarts > CMapBlockStartLimit) return true;
                if (activeBlock == noBlock)
                {
                    activeBlock = token.SequenceEqual("beginbfchar"u8) ? bfcharBlock : bfrangeBlock;
                    hexOperandIndex = 0;
                }
            }
            else if ((activeBlock == bfcharBlock && token.SequenceEqual("endbfchar"u8))
                || (activeBlock == bfrangeBlock && token.SequenceEqual("endbfrange"u8)))
            {
                activeBlock = noBlock;
                hexOperandIndex = 0;
            }
        }

        return false;
    }

    private static Dictionary<(int Number, int Generation), PdfIndirectObject> ReadIndirectObjects(byte[] content)
    {
        var objects = new Dictionary<(int Number, int Generation), PdfIndirectObject>();
        var position = 0;
        while (position < content.Length)
        {
            var found = content.AsSpan(position).IndexOf("obj"u8);
            if (found < 0) break;
            var objectKeyword = position + found;
            position = objectKeyword + "obj"u8.Length;
            if (!TryReadObjectHeader(content, objectKeyword, out var number, out var generation, out var bodyStart))
            {
                continue;
            }
            var endObject = FindPdfToken(content, "endobj"u8, bodyStart);
            if (endObject < 0) break;
            var streamKeyword = FindPdfToken(content, "stream"u8, bodyStart, endObject);
            var dictionaryEnd = streamKeyword < 0 ? endObject : streamKeyword;
            var streamStart = -1;
            var streamLength = 0;
            var hasDeclaredStreamLength = false;
            (int Number, int Generation)? lengthReference = null;
            if (streamKeyword >= 0)
            {
                streamStart = streamKeyword + "stream"u8.Length;
                if (streamStart < content.Length && content[streamStart] == '\r') streamStart++;
                if (streamStart < content.Length && content[streamStart] == '\n') streamStart++;

                var dictionary = content.AsSpan(bodyStart, streamKeyword - bodyStart);
                hasDeclaredStreamLength = TryReadStreamLength(dictionary, out var declaredLength, out lengthReference)
                    && declaredLength <= content.Length - streamStart;
                var declaredEnd = hasDeclaredStreamLength ? streamStart + declaredLength : streamStart;
                var endStream = FindPdfToken(content, "endstream"u8, hasDeclaredStreamLength ? declaredEnd : streamStart);
                if (endStream >= 0)
                {
                    if (hasDeclaredStreamLength)
                    {
                        streamLength = declaredLength;
                    }
                    else
                    {
                        var dataEnd = endStream;
                        if (dataEnd > streamStart && content[dataEnd - 1] == '\n') dataEnd--;
                        if (dataEnd > streamStart && content[dataEnd - 1] == '\r') dataEnd--;
                        streamLength = dataEnd - streamStart;
                    }
                    endObject = FindPdfToken(content, "endobj"u8, endStream + "endstream"u8.Length);
                    if (endObject < 0) break;
                }
                else
                {
                    streamStart = -1;
                }
            }
            objects[(number, generation)] = new PdfIndirectObject(
                number, generation, bodyStart, dictionaryEnd, endObject, streamStart, streamLength,
                hasDeclaredStreamLength, lengthReference);
            position = endObject + "endobj"u8.Length;
        }
        ResolveIndirectStreamLengths(content, objects);
        return objects;
    }

    private static bool TryReadStreamLength(
        ReadOnlySpan<byte> dictionary,
        out int length,
        out (int Number, int Generation)? lengthReference)
    {
        length = 0;
        lengthReference = null;
        var key = FindPdfDictionaryToken(dictionary, "/Length"u8);
        if (key < 0) return false;
        var cursor = SkipPdfWhitespaceAndComments(dictionary, key + "/Length"u8.Length);
        if (!TryReadInteger(dictionary, ref cursor, out length)) return false;
        cursor = SkipPdfWhitespaceAndComments(dictionary, cursor);
        if (cursor < dictionary.Length && IsDigit(dictionary[cursor]))
        {
            if (!TryReadInteger(dictionary, ref cursor, out var generation)) return false;
            cursor = SkipPdfWhitespaceAndComments(dictionary, cursor);
            if (IsPdfToken(dictionary, cursor, "R"u8))
            {
                lengthReference = (length, generation);
            }
            return false;
        }

        return cursor == dictionary.Length || IsPdfDelimiterOrWhitespace(dictionary[cursor]);
    }

    /// <summary>
    /// Returns a copy of a dictionary with <c>#xx</c> escapes in names decoded (<c>/Ty#70e</c> becomes <c>/Type</c>), so key
    /// lookups see the names PDF readers see. An escape that would produce whitespace or a delimiter stays escaped.
    /// </summary>
    private static byte[] DecodeNameEscapes(ReadOnlySpan<byte> source)
    {
        var decoded = new List<byte>(source.Length);
        var inName = false;
        for (var i = 0; i < source.Length; i++)
        {
            var b = source[i];
            if (b == '/') { inName = true; decoded.Add(b); continue; }
            if (inName && IsPdfDelimiterOrWhitespace(b)) inName = false;
            if (inName && b == '#' && i + 2 < source.Length + 0 && Uri.IsHexDigit((char)source[i + 1]) && Uri.IsHexDigit((char)source[i + 2]))
            {
                var value = (byte)Convert.ToInt32(Encoding.ASCII.GetString(source.Slice(i + 1, 2)), 16);
                if (value != '#' && value != 0 && !IsPdfDelimiterOrWhitespace(value))
                {
                    decoded.Add(value);
                    i += 2;
                    continue;
                }
            }

            decoded.Add(b);
        }

        return decoded.ToArray();
    }

    private static int FindPdfDictionaryToken(ReadOnlySpan<byte> source, ReadOnlySpan<byte> token)
    {
        var dictionaryDepth = 0;
        var arrayDepth = 0;
        var expectKey = false;
        for (var position = 0; position < source.Length;)
        {
            if (source[position] == '%')
            {
                while (position < source.Length && source[position] is not (byte)'\r' and not (byte)'\n') position++;
                continue;
            }

            if (source[position] == '(')
            {
                position = SkipLiteralString(source, position);
                if (dictionaryDepth == 1 && arrayDepth == 0 && !expectKey) expectKey = true;
                continue;
            }

            if (source[position] == '<')
            {
                if (position + 1 < source.Length && source[position + 1] == '<')
                {
                    dictionaryDepth++;
                    if (dictionaryDepth == 1) expectKey = true;
                    position += 2;
                }
                else
                {
                    position++;
                    while (position < source.Length && source[position] != '>') position++;
                    if (position < source.Length) position++;
                    if (dictionaryDepth == 1 && arrayDepth == 0 && !expectKey) expectKey = true;
                }

                continue;
            }

            if (source[position] == '>' && position + 1 < source.Length && source[position + 1] == '>')
            {
                dictionaryDepth--;
                if (dictionaryDepth == 1 && arrayDepth == 0) expectKey = true;
                position += 2;
                continue;
            }

            if (source[position] == '[')
            {
                arrayDepth++;
                position++;
                continue;
            }

            if (source[position] == ']')
            {
                arrayDepth--;
                if (dictionaryDepth == 1 && arrayDepth == 0) expectKey = true;
                position++;
                continue;
            }

            if (source[position] == '/')
            {
                var after = position + token.Length;
                if (dictionaryDepth == 1
                    && arrayDepth == 0
                    && expectKey
                    && after <= source.Length
                    && source.Slice(position, token.Length).SequenceEqual(token)
                    && (after == source.Length || IsPdfDelimiterOrWhitespace(source[after])))
                {
                    return position;
                }

                if (dictionaryDepth == 1 && arrayDepth == 0)
                {
                    expectKey = !expectKey;
                }

                position++;
                while (position < source.Length && !IsPdfDelimiterOrWhitespace(source[position])) position++;
                continue;
            }

            if (dictionaryDepth == 1 && arrayDepth == 0 && !expectKey
                && !IsPdfWhitespace(source[position]))
            {
                expectKey = true;
            }

            position++;
        }

        return -1;
    }

    private static int SkipPdfWhitespaceAndComments(ReadOnlySpan<byte> source, int position)
    {
        while (position < source.Length)
        {
            if (IsPdfWhitespace(source[position]))
            {
                position++;
            }
            else if (source[position] == '%')
            {
                while (position < source.Length && source[position] is not (byte)'\r' and not (byte)'\n') position++;
            }
            else
            {
                break;
            }
        }

        return position;
    }

    private static void ResolveIndirectStreamLengths(
        byte[] content,
        Dictionary<(int Number, int Generation), PdfIndirectObject> objects)
    {
        foreach (var (key, stream) in objects.ToArray())
        {
            if (stream.LengthReference is not { } reference) continue;
            if (!objects.TryGetValue(reference, out var lengthObject)
                || lengthObject.StreamStart >= 0
                || !TryReadIntegerObject(content, lengthObject, out var length)
                || length > content.Length - stream.StreamStart
                || FindPdfToken(content, "endstream"u8, stream.StreamStart + length) < 0)
            {
                objects[key] = stream with { StreamStart = -1 };
                continue;
            }

            objects[key] = stream with { StreamLength = length, HasDeclaredStreamLength = true };
        }
    }

    private static bool TryReadIntegerObject(byte[] content, PdfIndirectObject pdfObject, out int value)
    {
        var body = content.AsSpan(pdfObject.BodyStart, pdfObject.BodyEnd - pdfObject.BodyStart);
        var cursor = SkipWhitespace(body, 0);
        if (!TryReadInteger(body, ref cursor, out value)) return false;
        cursor = SkipWhitespace(body, cursor);
        return cursor == body.Length;
    }

    private static bool TryReadObjectHeader(
        ReadOnlySpan<byte> content,
        int objectKeyword,
        out int number,
        out int generation,
        out int bodyStart)
    {
        number = 0;
        generation = 0;
        bodyStart = 0;
        if (objectKeyword + 3 < content.Length && !IsPdfDelimiterOrWhitespace(content[objectKeyword + 3])) return false;
        var cursor = objectKeyword;
        while (cursor > 0 && IsPdfWhitespace(content[cursor - 1])) cursor--;
        var generationEnd = cursor;
        while (cursor > 0 && IsDigit(content[cursor - 1])) cursor--;
        if (cursor == generationEnd || cursor == 0 || !IsPdfWhitespace(content[cursor - 1])) return false;
        if (!TryParsePositiveInteger(content.Slice(cursor, generationEnd - cursor), allowZero: true, out generation)) return false;
        cursor--;
        while (cursor > 0 && IsPdfWhitespace(content[cursor - 1])) cursor--;
        var numberEnd = cursor;
        while (cursor > 0 && IsDigit(content[cursor - 1])) cursor--;
        if (cursor == numberEnd || (cursor > 0 && !IsPdfDelimiterOrWhitespace(content[cursor - 1]))) return false;
        if (!TryParsePositiveInteger(content.Slice(cursor, numberEnd - cursor), allowZero: false, out number)) return false;

        bodyStart = objectKeyword + 3;
        return true;
    }

    private static List<(int Number, int Generation)>? ReadArrayReferences(ReadOnlySpan<byte> dictionary, int start)
    {
        var references = new List<(int Number, int Generation)>();
        for (var position = start + 1; position < dictionary.Length;)
        {
            position = SkipWhitespace(dictionary, position);
            if (position >= dictionary.Length) return null;
            if (dictionary[position] == ']') return references;
            if (TryReadReference(dictionary, position, out var reference, out var afterReference))
            {
                references.Add(reference);
                position = afterReference;
            }
            else
            {
                position++;
            }
        }
        return null;
    }

    private static bool TryReadReference(
        ReadOnlySpan<byte> source,
        int start,
        out (int Number, int Generation) reference,
        out int afterReference)
    {
        reference = default;
        afterReference = start;
        var cursor = SkipWhitespace(source, start);
        if (!TryReadInteger(source, ref cursor, out var number)) return false;
        if (cursor >= source.Length || !IsPdfWhitespace(source[cursor])) return false;
        cursor = SkipWhitespace(source, cursor);
        if (!TryReadInteger(source, ref cursor, out var generation)) return false;
        if (cursor >= source.Length || !IsPdfWhitespace(source[cursor])) return false;
        cursor = SkipWhitespace(source, cursor);
        if (!IsPdfToken(source, cursor, "R"u8)) return false;
        reference = (number, generation);
        afterReference = cursor + 1;
        return true;
    }

    private static long DecodedContentStreamLength(
        byte[] content,
        PdfIndirectObject stream,
        out bool readable)
    {
        readable = true;
        var dictionary = Encoding.Latin1.GetString(
            content, stream.BodyStart, stream.DictionaryEnd - stream.BodyStart);
        var filter = FilterValue.Match(dictionary);
        if (!filter.Success)
        {
            var source = content.AsSpan(stream.StreamStart, stream.StreamLength);
            return ContainsTextShowingOperator(source)
                ? Math.Min(stream.StreamLength, TextSourceLimitBytes + 1)
                : 0;
        }
        var filters = FirstName.Matches(filter.Groups[1].Value).Select(match => match.Value).ToArray();
        if (filters.Length == 0)
        {
            readable = false;
            return 0;
        }

        var decoded = content.AsSpan(stream.StreamStart, stream.StreamLength).ToArray();
        for (var index = 0; index < filters.Length; index++)
        {
            if (!TryDecodeContentFilter(
                filters[index], decoded, DecodedStreamLimitBytes, out decoded, out var exceededLimit))
            {
                readable = false;
                return 0;
            }
            if (exceededLimit) return TextSourceLimitBytes + 1;
        }

        return ContainsTextShowingOperator(decoded)
            ? Math.Min(decoded.Length, TextSourceLimitBytes + 1)
            : 0;
    }

    private static bool TryDecodeContentFilter(
        string filter,
        byte[] source,
        long maximumBytes,
        out byte[] decoded,
        out bool exceededLimit)
    {
        decoded = Array.Empty<byte>();
        exceededLimit = false;
        using var output = new MemoryStream();
        var overflow = false;

        bool WriteByte(byte value)
        {
            if (output.Length >= maximumBytes)
            {
                overflow = true;
                return false;
            }

            output.WriteByte(value);
            return true;
        }

        if (filter is "/FlateDecode" or "/Fl")
        {
            try
            {
                using var zlib = new ZLibStream(new MemoryStream(source), CompressionMode.Decompress);
                var buffer = new byte[81920];
                int read;
                while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (var index = 0; index < read; index++)
                    {
                        if (!WriteByte(buffer[index]))
                        {
                            exceededLimit = overflow;
                            return true;
                        }
                    }
                }
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
        else if (filter is "/ASCIIHexDecode" or "/AHx")
        {
            var highNibble = -1;
            foreach (var value in source)
            {
                if (IsPdfWhitespace(value)) continue;
                if (value == '>') break;
                var nibble = HexValue(value);
                if (nibble < 0) return false;
                if (highNibble < 0)
                {
                    highNibble = nibble;
                }
                else
                {
                    if (!WriteByte((byte)((highNibble << 4) | nibble)))
                    {
                        exceededLimit = overflow;
                        return true;
                    }
                    highNibble = -1;
                }
            }

            if (highNibble >= 0 && !WriteByte((byte)(highNibble << 4)))
            {
                exceededLimit = overflow;
                return true;
            }
        }
        else if (filter is "/ASCII85Decode" or "/A85")
        {
            ulong group = 0;
            var groupSize = 0;
            foreach (var value in source)
            {
                if (IsPdfWhitespace(value)) continue;
                if (value == '~')
                {
                    break;
                }
                if (value == 'z')
                {
                    if (groupSize != 0) return false;
                    for (var index = 0; index < 4; index++)
                    {
                        if (!WriteByte(0))
                        {
                            exceededLimit = overflow;
                            return true;
                        }
                    }
                    continue;
                }
                if (value is < (byte)'!' or > (byte)'u') return false;
                group = group * 85 + (uint)(value - '!');
                if (++groupSize == 5)
                {
                    if (group > uint.MaxValue) return false;
                    for (var shift = 24; shift >= 0; shift -= 8)
                    {
                        if (!WriteByte((byte)(group >> shift)))
                        {
                            exceededLimit = overflow;
                            return true;
                        }
                    }
                    group = 0;
                    groupSize = 0;
                }
            }

            if (groupSize == 1) return false;
            if (groupSize > 1)
            {
                var outputBytes = groupSize - 1;
                for (var index = groupSize; index < 5; index++) group = group * 85 + 84;
                if (group > uint.MaxValue) return false;
                for (var index = 0; index < outputBytes; index++)
                {
                    var shift = 24 - index * 8;
                    if (!WriteByte((byte)(group >> shift)))
                    {
                        exceededLimit = overflow;
                        return true;
                    }
                }
            }
        }
        else
        {
            return false;
        }

        decoded = output.ToArray();
        return true;
    }

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        _ => -1,
    };

    private static bool ContainsTextShowingOperator(ReadOnlySpan<byte> content)
    {
        for (var position = 0; position < content.Length;)
        {
            var value = content[position];
            if (IsPdfWhitespace(value))
            {
                position++;
                continue;
            }

            if (value == '%')
            {
                while (position < content.Length
                    && content[position] != (byte)'\r'
                    && content[position] != (byte)'\n') position++;
                continue;
            }

            if (value == '(')
            {
                position = SkipLiteralString(content, position);
                continue;
            }

            if (value == '<')
            {
                if (position + 1 < content.Length && content[position + 1] == '<')
                {
                    position += 2;
                }
                else
                {
                    position++;
                    while (position < content.Length && content[position] != '>') position++;
                    if (position < content.Length) position++;
                }

                continue;
            }

            if (value == '/')
            {
                position++;
                while (position < content.Length && !IsPdfDelimiterOrWhitespace(content[position])) position++;
                continue;
            }

            if (value == (byte)'\'' || value == (byte)'"') return true;
            if (value == (byte)'[' || value == (byte)']' || value == (byte)'{'
                || value == (byte)'}' || value == (byte)')' || value == (byte)'>')
            {
                position++;
                continue;
            }

            var tokenStart = position;
            while (position < content.Length
                && !IsPdfDelimiterOrWhitespace(content[position])
                && content[position] != (byte)'\''
                && content[position] != (byte)'"')
            {
                position++;
            }

            var token = content[tokenStart..position];
            if (token.SequenceEqual("Tj"u8) || token.SequenceEqual("TJ"u8)) return true;
        }

        return false;
    }

    private static int SkipLiteralString(ReadOnlySpan<byte> content, int position)
    {
        var depth = 0;
        while (position < content.Length)
        {
            var value = content[position++];
            if (value == '\\')
            {
                if (position < content.Length) position++;
            }
            else if (value == '(')
            {
                depth++;
            }
            else if (value == ')' && --depth == 0)
            {
                break;
            }
        }

        return position;
    }

    private static int FindPdfToken(ReadOnlySpan<byte> source, ReadOnlySpan<byte> token, int start, int limit = -1)
    {
        if (limit < 0 || limit > source.Length) limit = source.Length;
        while (start < limit)
        {
            var found = source[start..limit].IndexOf(token);
            if (found < 0) return -1;
            var index = start + found;
            var after = index + token.Length;
            if ((index == 0 || IsPdfDelimiterOrWhitespace(source[index - 1]))
                && (after == source.Length || IsPdfDelimiterOrWhitespace(source[after])))
            {
                return index;
            }

            start = after;
        }

        return -1;
    }

    private static bool HasNameValue(ReadOnlySpan<byte> source, int start, ReadOnlySpan<byte> value)
    {
        start = SkipPdfWhitespaceAndComments(source, start);
        return start + value.Length <= source.Length
            && source.Slice(start, value.Length).SequenceEqual(value)
            && (start + value.Length == source.Length || IsPdfDelimiterOrWhitespace(source[start + value.Length]));
    }

    private static bool IsPdfToken(ReadOnlySpan<byte> source, int start, ReadOnlySpan<byte> token) =>
        start + token.Length <= source.Length
        && source.Slice(start, token.Length).SequenceEqual(token)
        && (start + token.Length == source.Length || IsPdfDelimiterOrWhitespace(source[start + token.Length]));

    private static int SkipWhitespace(ReadOnlySpan<byte> source, int position)
    {
        while (position < source.Length && IsPdfWhitespace(source[position])) position++;
        return position;
    }

    private static bool TryReadInteger(ReadOnlySpan<byte> source, ref int position, out int value)
    {
        var start = position;
        while (position < source.Length && IsDigit(source[position])) position++;
        return TryParsePositiveInteger(source[start..position], allowZero: true, out value);
    }

    private static bool TryParsePositiveInteger(ReadOnlySpan<byte> source, bool allowZero, out int value)
    {
        value = 0;
        if (source.IsEmpty) return false;
        foreach (var digit in source)
        {
            if (!IsDigit(digit) || value > (int.MaxValue - (digit - '0')) / 10) return false;
            value = value * 10 + digit - '0';
        }

        return allowZero || value > 0;
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private static bool IsPdfWhitespace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsPdfDelimiterOrWhitespace(byte value) =>
        IsPdfWhitespace(value) || value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'['
            or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>
    /// Before any worker starts, inflates each Flate stream up to <see cref="DecodedStreamLimitBytes"/> and rejects the
    /// document when one expands further. The scan visits every byte of the input once. A filter name spelled with a
    /// <c>#</c> escape is not resolved here, so the document is treated as unreadable.
    /// </summary>
    internal static (EvidenceProcessingState State, string Reason, string Text)? PreflightStreamFilters(byte[] content)
    {
        var streamKeyword = "stream"u8;
        var endKeyword = "endstream"u8;
        var position = 0;
        var dictionaryFloor = 0;
        while (position < content.Length)
        {
            var found = content.AsSpan(position).IndexOf(streamKeyword);
            if (found < 0) break;
            var keyword = position + found;
            position = keyword + streamKeyword.Length;
            if (keyword >= 3 && content.AsSpan(keyword - 3, 3).SequenceEqual("end"u8)) continue;

            var dictionaryStart = content.AsSpan(dictionaryFloor, keyword - dictionaryFloor).LastIndexOf("obj"u8);
            dictionaryStart = dictionaryStart < 0 ? dictionaryFloor : dictionaryFloor + dictionaryStart + 3;
            var dictionary = Encoding.Latin1.GetString(content, dictionaryStart, keyword - dictionaryStart);

            var dataStart = position;
            if (dataStart < content.Length && content[dataStart] == '\r') dataStart++;
            if (dataStart < content.Length && content[dataStart] == '\n') dataStart++;
            var end = content.AsSpan(dataStart).IndexOf(endKeyword);
            var dataEnd = end < 0 ? content.Length : dataStart + end;
            position = dataEnd;
            dictionaryFloor = dataEnd;

            if (NameWithEscape.Matches(dictionary).Any(m => IsFlateOrFilterName(Unescape(m.Value))))
            {
                return Retryable("PdfPig could not open or read the document; retry is possible");
            }

            var filter = FilterValue.Match(dictionary);
            if (!filter.Success) continue;
            var first = FirstName.Match(filter.Groups[1].Value);
            if (!first.Success || first.Value is not ("/FlateDecode" or "/Fl")) continue;

            if (InflatedBytesExceedLimit(content, dataStart, dataEnd - dataStart))
            {
                return Retryable($"PDF stream content exceeds {DecodedStreamLimitBytes} decoded bytes; retry is possible");
            }
        }

        return null;
    }

    private static bool IsFlateOrFilterName(string name) => name is "/Filter" or "/FlateDecode" or "/Fl";

    private static string Unescape(string name) =>
        Regex.Replace(name, "#([0-9A-Fa-f]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    private static bool InflatedBytesExceedLimit(byte[] content, int offset, int length)
    {
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(content, offset, length), CompressionMode.Decompress);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > DecodedStreamLimitBytes) return true;
            }
        }
        catch (InvalidDataException)
        {
            // Damaged data is the worker's to reject.
        }

        return false;
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
