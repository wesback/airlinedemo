using System.Diagnostics;
using System.Text.Json;
using AirlineDemo.Api.Evidence.PdfWorker;

namespace AirlineDemo.Api.Evidence;

public sealed class IsolatedPdfParserOptions
{
    public long MemoryLimitBytes { get; init; } = 512L * 1024 * 1024;

    public TimeSpan WallClockLimit { get; init; } = TimeSpan.FromSeconds(30);

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
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pdf-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workingDirectory);
            if (!OperatingSystem.IsWindows())
            {
                // Read-only for the child: it cannot create files in its working directory.
                File.SetUnixFileMode(workingDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }

            using var process = Process.Start(CreateStartInfo(options, workingDirectory));
            if (process is null)
            {
                return Retryable("worker could not be started; retry is possible");
            }

            return Supervise(process, content, options);
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

    /// <summary>Builds the child's start info: no shell, no inherited environment, redirected stdin/stdout.</summary>
    internal static ProcessStartInfo CreateStartInfo(IsolatedPdfParserOptions options, string workingDirectory)
    {
        var fileName = options.WorkerFileName ?? ResolveHost();
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

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
        var stdout = process.StandardOutput.ReadToEndAsync();
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
        }

        if (!stdout.Wait(TimeSpan.FromSeconds(5)))
        {
            return Retryable("worker output did not close; retry is possible");
        }

        if (process.ExitCode != 0)
        {
            return Retryable($"worker failed with exit code {process.ExitCode}; retry is possible");
        }

        PdfWorkerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<PdfWorkerResponse>(stdout.Result);
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
