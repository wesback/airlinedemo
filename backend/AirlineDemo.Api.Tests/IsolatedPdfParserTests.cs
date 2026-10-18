using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Evidence.PdfWorker;

namespace AirlineDemo.Api.Tests;

[Collection("IsolatedPdfParser")]
public class IsolatedPdfParserTests
{
    private static byte[] Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Fixtures", "PdfCorpus")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return File.ReadAllBytes(Path.Combine(dir!, "Fixtures", "PdfCorpus", name));
    }

    /// <summary>A one-page PDF whose content stream inflates to 600 MB of zeros (above the 512 MB limit) from a few hundred kilobytes.</summary>
    private static byte[] FlateBomb(string filter = "/FlateDecode", long decodedBytes = 600L * 1024 * 1024)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)' ');
            for (var remaining = decodedBytes; remaining > 0; remaining -= chunk.Length)
            {
                zlib.Write(chunk, 0, (int)Math.Min(chunk.Length, remaining));
            }
        }

        var body = compressed.ToArray();
        var pdf = new MemoryStream();
        var offsets = new List<long>();
        void Object(string header)
        {
            offsets.Add(pdf.Position);
            Write(pdf, header);
        }

        Write(pdf, "%PDF-1.4\n");
        Object("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        Object("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        Object("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >> endobj\n");
        Object($"4 0 obj << /Length {body.Length} {(filter.StartsWith("/Fil") ? filter : "/Filter " + filter)} >> stream\n");
        pdf.Write(body);
        Write(pdf, "\nendstream endobj\n");

        // A real cross-reference table with exact 20-byte entries, so PdfPig can locate every object.
        var xrefOffset = pdf.Position;
        var xref = new StringBuilder("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            xref.Append($"{offset:D10} 00000 n \n");
        }

        xref.Append($"trailer << /Root 1 0 R /Size 5 >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        Write(pdf, xref.ToString());
        return pdf.ToArray();
    }

    private static void Write(MemoryStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes);
    }

    /// <summary>
    /// Runs the application's own worker entry point through the dotnet host, without the network namespace wrapper,
    /// so these tests never skip. The parent still applies the memory, time and output limits and the empty environment.
    /// </summary>
    private static IsolatedPdfParserOptions RealWorker(long? memoryLimitBytes = null) => new()
    {
        WorkerFileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
        WorkerArguments = new[] { typeof(IsolatedPdfParser).Assembly.Location, PdfWorkerEntry.Switch },
        MemoryLimitBytes = memoryLimitBytes ?? 512L * 1024 * 1024,
    };

    // AC-1: the parent hands the PDF to a child worker and reads JSON back. Covered by the valid, corrupt and hang tests.
    [Fact]
    public void ValidPdf_IsExtractedByChildProcess()
    {
        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), RealWorker());

        Assert.Equal(EvidenceProcessingState.Complete, outcome.State);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Text));
    }

    [Fact]
    public void CorruptPdf_IsRejectedAsRetryable()
    {
        var outcome = IsolatedPdfParser.Parse(Fixture("corrupt.pdf"), RealWorker());

        Assert.NotEqual(EvidenceProcessingState.Complete, outcome.State);
        Assert.Equal(string.Empty, outcome.Text);
    }

    // AC-3: a flate bomb fails within the limits and the next valid PDF is still processed.
    [Fact]
    public void FlateBomb_FailsWithinLimits_AndServiceStillProcessesValidPdf()
    {
        var clock = Stopwatch.StartNew();
        var bomb = IsolatedPdfParser.Parse(FlateBomb(), RealWorker());
        clock.Stop();

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, bomb.State);
        Assert.True(
            bomb.Reason.Contains("memory limit") || bomb.Reason.Contains("time limit") || bomb.Reason.Contains("decoded bytes"),
            $"bomb was not stopped by a limit: {bomb.Reason}");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"bomb took {clock.Elapsed}");

        var valid = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), RealWorker());
        Assert.Equal(EvidenceProcessingState.Complete, valid.State);
    }

    private const string BombReason = "PDF stream content exceeds 8388608 decoded bytes; retry is possible";
    private const string UnreadableReason = "PdfPig could not open or read the document; retry is possible";

    private static IsolatedPdfParserOptions CountingWorker(int[] starts) => new()
    {
        WorkerFileName = "/bin/sh",
        WorkerArguments = new[] { "-c", "cat > /dev/null" },
        WorkerStarting = () => starts[0]++,
    };

    // AC-1: a Flate stream that decodes past 8,388,608 bytes is refused before a worker starts, in all three spellings.
    [Theory]
    [InlineData("/FlateDecode", BombReason)]
    [InlineData("/Flate#44ecode", UnreadableReason)]
    [InlineData("/Fil#74er /FlateDecode", UnreadableReason)]
    public void StreamFilterPreflightRejectsBeforeStartingWorker(string filter, string reason)
    {
        var starts = new[] { 0 };
        var input = FlateBomb(filter, 9L * 1024 * 1024);
        Assert.True(input.Length <= 10_485_760);

        var outcome = IsolatedPdfParser.Parse(input, CountingWorker(starts));

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
        Assert.Equal(reason, outcome.Reason);
        Assert.Equal(string.Empty, outcome.Text);
        Assert.Equal(0, starts[0]);
    }

    // AC-2: exactly 8,388,608 decoded bytes is accepted and starts the worker once.
    [Fact]
    public void StreamFilterPreflightAcceptsExactDecodedLimitAndStartsWorkerOnce()
    {
        var starts = new[] { 0 };

        IsolatedPdfParser.Parse(FlateBomb(decodedBytes: 8L * 1024 * 1024), CountingWorker(starts));

        Assert.Equal(1, starts[0]);
    }

    // One byte over the limit is refused.
    [Fact]
    public void StreamFilterPreflightRefusesOneByteOverTheLimit()
    {
        var starts = new[] { 0 };

        var outcome = IsolatedPdfParser.Parse(FlateBomb(decodedBytes: 8L * 1024 * 1024 + 1), CountingWorker(starts));

        Assert.Equal(BombReason, outcome.Reason);
        Assert.Equal(0, starts[0]);
    }

    // AC-2: exceeding the 30 second wall-clock limit kills the child (tested here with a 2 second limit).
    [Fact]
    public void HungWorker_IsKilledByTimeLimit()
    {
        var options = new IsolatedPdfParserOptions
        {
            WorkerFileName = "/bin/sh",
            WorkerArguments = new[] { "-c", "sleep 60" },
            WallClockLimit = TimeSpan.FromSeconds(2),
        };

        var clock = Stopwatch.StartNew();
        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), options);
        clock.Stop();

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
        Assert.Contains("time limit", outcome.Reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"hang took {clock.Elapsed}");
    }

    // AC-2: exceeding the memory limit kills the child and returns a retryable failure with no evidence.
    [Fact]
    public void MemoryLimit_KillsWorkerAndReturnsRetryable()
    {
        // A 1-byte cap stops the child before it can read the PDF: it must not complete.
        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), RealWorker(memoryLimitBytes: 1));

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
        Assert.Equal(string.Empty, outcome.Text);
    }

    // AC-2: the memory cap is a kernel limit on the child (Linux), so removing it from the start info fails this test.
    [Fact]
    public void Child_IsStartedWithHardMemoryLimit()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var workingDirectory = Path.Combine(Path.GetTempPath(), "pdf-worker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var startInfo = IsolatedPdfParser.CreateStartInfo(new IsolatedPdfParserOptions(), workingDirectory);

            Assert.Contains($"--data={512L * 1024 * 1024}", startInfo.ArgumentList);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    // Root must not use a user namespace (-r): the later switch to uid 65534 would be unmapped and every PDF would fail.
    [Fact]
    public void UnshareFlags_UseNoUserNamespaceForRoot()
    {
        var expected = Environment.UserName == "root" ? "-n" : "-rn";

        Assert.Equal(expected, IsolatedPdfParser.UnshareFlags());
    }

    // Windows memory cap: the Job Object limit is built without any Windows call, so this runs on every platform.
    [Fact]
    public void JobObjectLimits_CapProcessMemoryAndKillOnClose()
    {
        var limits = WindowsJobObject.BuildLimits(512L * 1024 * 1024);

        Assert.NotEqual(0u, limits.Basic.LimitFlags & WindowsJobObject.JobObjectLimitProcessMemory);
        Assert.Equal((nuint)(512L * 1024 * 1024), limits.ProcessMemoryLimit);
    }

    // Windows: TryAssign must put the started child in a job whose kernel limits hold the cap; removing it fails here.
    [Fact]
    public void JobObject_TryAssign_AppliesMemoryCapToChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c more")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        try
        {
            Assert.True(WindowsJobObject.TryAssign(child, 256L * 1024 * 1024, out var job));
            using (job)
            {
                var (inJob, limits) = job!.Inspect(child);

                Assert.True(inJob);
                Assert.NotEqual(0u, limits.Basic.LimitFlags & WindowsJobObject.JobObjectLimitProcessMemory);
                Assert.Equal((nuint)(256L * 1024 * 1024), limits.ProcessMemoryLimit);
            }
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
        }
    }

    // Unix: the worker's working directory must not be one the worker owns (it could chmod it) nor writable by others.
    [Fact]
    public void WorkingDirectory_IsFreshPerRunUnderTempPath()
    {
        var first = IsolatedPdfParser.ChooseWorkingDirectory();
        var second = IsolatedPdfParser.ChooseWorkingDirectory();

        Assert.StartsWith(Path.GetTempPath(), first);
        Assert.Contains("pdf-worker-", first);
        Assert.NotEqual(first, second);
        Assert.False(Directory.Exists(first));
    }

    // AC-4: the temporary working directory is deleted after the worker run, whatever its outcome.
    [Fact]
    public void Parse_DeletesItsTemporaryWorkingDirectoryAfterTheRun()
    {
        static HashSet<string> WorkerDirectories() =>
            Directory.GetDirectories(Path.GetTempPath(), "pdf-worker-*").ToHashSet();

        var before = WorkerDirectories();

        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), RealWorker());

        Assert.Equal(EvidenceProcessingState.Complete, outcome.State);
        Assert.Empty(WorkerDirectories().Except(before).ToList());
    }

    // AC-4: each Parse run starts its worker in a distinct, empty, temporary directory that is gone afterwards.
    [Fact]
    public void Parse_RunsEachWorkerInADistinctEmptyTemporaryDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var record = Path.Combine(Path.GetTempPath(), "pdf-cwd-" + Guid.NewGuid().ToString("N"));
        var recorded = new List<(string Directory, string Listing)>();
        try
        {
            for (var run = 0; run < 2; run++)
            {
                var options = new IsolatedPdfParserOptions
                {
                    WorkerFileName = "/bin/sh",
                    WorkerArguments = new[] { "-c", "{ pwd -P; ls -A; } > \"$0\"", record },
                };

                IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), options);

                var lines = File.ReadAllLines(record);
                recorded.Add((lines[0], string.Join("|", lines.Skip(1))));
                File.Delete(record);
            }
        }
        finally
        {
            File.Delete(record);
        }

        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd('/');
        foreach (var (directory, listing) in recorded)
        {
            Assert.Contains("pdf-worker-", directory);
            Assert.StartsWith(Path.GetFullPath(tempRoot).TrimEnd('/'), Path.GetFullPath(directory));
            Assert.Equal(string.Empty, listing);
            Assert.False(Directory.Exists(directory));
        }

        Assert.NotEqual(recorded[0].Directory, recorded[1].Directory);
    }

    [Theory]
    [InlineData(true, false, "root", false, true)]
    [InlineData(true, false, "app", false, false)]
    [InlineData(true, false, "root", true, false)]
    [InlineData(true, true, "root", false, false)]
    [InlineData(false, false, "root", false, false)]
    public void WorkerIsolationRefusal_OnlyAppliesToRootWithoutDrop(bool linux, bool customWorker, string user, bool setprivAvailable, bool expected)
    {
        Assert.Equal(expected, IsolatedPdfParser.CannotIsolateWorker(linux, customWorker, user, setprivAvailable));
    }

    // AC-2 (output bound): a child that writes more than OutputLimitBytes is killed and nothing is buffered without limit.
    [Fact]
    public void OversizedWorkerOutput_IsKilledAndReturnsRetryable()
    {
        var options = new IsolatedPdfParserOptions
        {
            WorkerFileName = "/bin/sh",
            WorkerArguments = new[] { "-c", "head -c 1000000 /dev/zero" },
            OutputLimitBytes = 1024,
        };

        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), options);

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
        Assert.Contains("output size limit", outcome.Reason);
        Assert.Equal(string.Empty, outcome.Text);
    }

    // AC-1: intake routes every PDF through IsolatedPdfParser; a hung worker stops intake before anything is stored.
    [Fact]
    public void Intake_RoutesPdfThroughIsolatedParser_AndStoresNothingWhenWorkerIsKilled()
    {
        var options = new IsolatedPdfParserOptions
        {
            WorkerFileName = "/bin/sh",
            WorkerArguments = new[] { "-c", "sleep 60" },
            WallClockLimit = TimeSpan.FromSeconds(2),
        };
        var store = new EvidenceBasisStore();
        var documents = new[] { new EvidenceDocument("valid.pdf", Fixture("valid-text.pdf")) };

        var result = EvidenceIntake.Process(documents, store, options);

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, result.State);
        Assert.Contains("time limit", result.Reason);
        Assert.Null(store.Latest("valid.pdf"));
    }

    // AC-1: the worker entry point turns stdin bytes into a JSON-ready response.
    [Fact]
    public void WorkerEntry_ExtractsValidPdfAndRejectsCorruptPdf()
    {
        var valid = PdfWorkerEntry.Execute(new MemoryStream(Fixture("valid-text.pdf")), () => true);
        var corrupt = PdfWorkerEntry.Execute(new MemoryStream(Fixture("corrupt.pdf")), () => true);

        Assert.Equal(EvidenceProcessingState.Complete.ToString(), valid.State);
        Assert.False(string.IsNullOrWhiteSpace(valid.Text));
        Assert.Equal(EvidenceProcessingState.RejectedRetryable.ToString(), corrupt.State);
        Assert.Equal(string.Empty, corrupt.Text);
    }

    [Fact]
    public void WorkerEntry_StopsWhenMemoryLimitIsExceeded()
    {
        var response = PdfWorkerEntry.Execute(new MemoryStream(Fixture("valid-text.pdf")), () => false);

        Assert.Equal(EvidenceProcessingState.RejectedRetryable.ToString(), response.State);
        Assert.Contains("memory limit", response.Reason);
        Assert.Equal(string.Empty, response.Text);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool DeniesWrite(string directory)
    {
        var rules = new DirectoryInfo(directory).GetAccessControl()
            .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>();
        var write = System.Security.AccessControl.FileSystemRights.WriteData
            | System.Security.AccessControl.FileSystemRights.CreateFiles;
        return rules.Any(r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Deny
            && (r.FileSystemRights & write) == write);
    }

    // AC-4: a started child sees no inherited variable. /usr/bin/env prints the environment it receives, so this
    // fails if the Environment.Clear() in CreateStartInfo is removed.
    [Fact]
    public void Started_Child_ReceivesNoInheritedEnvironment()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            return;
        }

        const string sentinel = "PDF_WORKER_ENV_PROBE";
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pdf-worker-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            Environment.SetEnvironmentVariable(sentinel, "inherited");
            // Windows: cmd.exe /c set prints the environment it receives (full path, since PATH is cleared).
            var options = OperatingSystem.IsWindows()
                ? new IsolatedPdfParserOptions
                {
                    WorkerFileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    WorkerArguments = new[] { "/c", "set" },
                }
                : new IsolatedPdfParserOptions { WorkerFileName = "/usr/bin/env" };
            var startInfo = IsolatedPdfParser.CreateStartInfo(options, workingDirectory);

            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(10_000));

            Assert.DoesNotContain(sentinel, output);
            if (OperatingSystem.IsLinux())
            {
                Assert.Equal(string.Empty, output.Trim());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinel, null);
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    // AC-4: the child starts with an empty environment and a working directory of its own (no secrets, no store access).
    [Fact]
    public void Child_StartsWithEmptyEnvironmentAndReadOnlyWorkingDirectory()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pdf-worker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            IsolatedPdfParser.RestrictWorkingDirectory(workingDirectory);
            if (OperatingSystem.IsLinux())
            {
                var mode = File.GetUnixFileMode(workingDirectory);
                Assert.Equal(0, (int)(mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.UserWrite)));
            }

            if (OperatingSystem.IsWindows())
            {
                Assert.True(DeniesWrite(workingDirectory));
            }

            // The sentinel is inherited by ProcessStartInfo from this process; only an explicit Clear removes it.
            const string sentinel = "PDF_WORKER_TEST_SENTINEL";
            Environment.SetEnvironmentVariable(sentinel, "inherited");
            var startInfo = IsolatedPdfParser.CreateStartInfo(new IsolatedPdfParserOptions(), workingDirectory);

            Assert.False(startInfo.Environment.ContainsKey(sentinel));
            Assert.Empty(startInfo.Environment);
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.RedirectStandardInput);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.Equal(workingDirectory, startInfo.WorkingDirectory);
            Assert.Contains(PdfWorkerEntry.Switch, startInfo.ArgumentList);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDF_WORKER_TEST_SENTINEL", null);
            Directory.Delete(workingDirectory, recursive: true);
        }
    }
}
