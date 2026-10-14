using System.Text.Json;

namespace AirlineDemo.Api.Evidence.PdfWorker;

public sealed record PdfWorkerResponse(string State, string Reason, string Text);

/// <summary>
/// Child-process entry point. Reads one PDF from standard input and writes a JSON <see cref="PdfWorkerResponse"/>
/// to standard output. The working-set check here is a second guard alongside the parent's supervision.
/// </summary>
public static class PdfWorkerEntry
{
    public const string Switch = "--pdf-worker";

    public static int Run()
    {
        var limit = IsolatedPdfParserOptions.Default.MemoryLimitBytes;
        LimitOwnDataSegment(limit);
        StartMemoryWatchdog(limit);
        using var stdin = Console.OpenStandardInput();
        var response = Execute(stdin, WithinMemoryLimit);
        using var stdout = Console.OpenStandardOutput();
        JsonSerializer.Serialize(stdout, response);
        stdout.Flush();
        return 0;
    }

    internal static PdfWorkerResponse Execute(Stream stdin, Func<bool> withinMemoryLimit)
    {
        // The PDF library swallows allocation failures while decoding streams, so an out-of-memory
        // exception anywhere during parsing marks the document as over the memory limit.
        var outOfMemory = false;
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is OutOfMemoryException)
            {
                outOfMemory = true;
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            using var input = new MemoryStream();
            stdin.CopyTo(input);

            var outcome = EvidenceIntake.ExtractText(input.ToArray(), withinMemoryLimit);
            if (outOfMemory)
            {
                return new PdfWorkerResponse(
                    EvidenceProcessingState.RejectedRetryable.ToString(),
                    "exceeded the memory limit; retry is possible",
                    string.Empty);
            }

            return new PdfWorkerResponse(outcome.State.ToString(), outcome.Reason, outcome.Text);
        }
        catch (OutOfMemoryException)
        {
            return new PdfWorkerResponse(
                EvidenceProcessingState.RejectedRetryable.ToString(),
                "exceeded the memory limit; retry is possible",
                string.Empty);
        }
        catch (Exception)
        {
            return new PdfWorkerResponse(
                EvidenceProcessingState.RejectedRetryable.ToString(),
                "worker failed to read the document; retry is possible",
                string.Empty);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }
    }

    /// <summary>
    /// Linux: the worker caps its own private memory with RLIMIT_DATA before it reads any input, so the kernel
    /// refuses allocations over the limit even when no cgroup or prlimit wrapper is available.
    /// </summary>
    private static void LimitOwnDataSegment(long limitBytes)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            var limit = new RLimit { Current = (ulong)limitBytes, Maximum = (ulong)limitBytes };
            SetRLimit(RLimitData, ref limit);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Other platforms and a last resort on Linux: a thread that exits the worker as soon as its working set passes the limit.</summary>
    private static void StartMemoryWatchdog(long limitBytes)
    {
        var watchdog = new Thread(() =>
        {
            while (true)
            {
                if (Environment.WorkingSet > limitBytes)
                {
                    Environment.FailFast("worker exceeded the memory limit");
                }

                Thread.Sleep(5);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        watchdog.Start();
    }

    private const int RLimitData = 2;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Current;
        public ulong Maximum;
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setrlimit")]
    private static extern int SetRLimit(int resource, ref RLimit limit);

    private static bool WithinMemoryLimit() =>
        Environment.WorkingSet <= IsolatedPdfParserOptions.Default.MemoryLimitBytes;
}
