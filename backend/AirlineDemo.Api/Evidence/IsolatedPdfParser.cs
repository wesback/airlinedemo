using System.Diagnostics;
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
        // Without a drop to "nobody" the worker owns its working directory and could undo the read-only mode: fail closed.
        if (OperatingSystem.IsLinux() && options.WorkerFileName is null && !DropsToNobody())
        {
            return Retryable("worker cannot be isolated from its working directory; the document was not accepted, retry is possible");
        }

        var workingDirectory = Path.Combine(Path.GetTempPath(), "pdf-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workingDirectory);
            RestrictWorkingDirectory(workingDirectory);

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
                Directory.Delete(workingDirectory, recursive: false);
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
    internal static ProcessStartInfo CreateStartInfo(IsolatedPdfParserOptions options, string workingDirectory)
    {
        var fileName = options.WorkerFileName ?? ResolveHost();
        var program = fileName;
        var wrapper = new List<string>();
        if (OperatingSystem.IsLinux())
        {
            var chain = new List<string>();
            if (options.WorkerFileName is null && NetworkNamespaceAvailable.Value)
            {
                // A new user and network namespace with no interfaces: the child has no network access.
                chain.AddRange(new[] { UnsharePath, "-rn", "--" });
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

    /// <summary>Conventional uid/gid of the unprivileged "nobody" account the worker switches to when the API is root.</summary>
    private const int NobodyUid = 65534;

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
                ArgumentList = { "-rn", "--", "true" },
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
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly IntPtr handle;

    private WindowsJobObject(IntPtr handle)
    {
        this.handle = handle;
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
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = JobObjectLimitProcessMemory | JobObjectLimitKillOnJobClose;
        limits.ProcessMemoryLimit = (nuint)memoryLimitBytes;

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

    public void Dispose()
    {
        CloseHandle(handle);
    }

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
    private struct BasicLimits
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
    private struct ExtendedLimits
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
