using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Evidence.PdfWorker;

namespace AirlineDemo.Api.Tests;

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
    private static byte[] FlateBomb()
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)' ');
            for (var i = 0; i < 600; i++)
            {
                zlib.Write(chunk);
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
        Object($"4 0 obj << /Length {body.Length} /Filter /FlateDecode >> stream\n");
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
            bomb.Reason.Contains("memory limit") || bomb.Reason.Contains("time limit"),
            $"bomb was not stopped by a limit: {bomb.Reason}");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"bomb took {clock.Elapsed}");

        var valid = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), RealWorker());
        Assert.Equal(EvidenceProcessingState.Complete, valid.State);
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

            var startInfo = IsolatedPdfParser.CreateStartInfo(new IsolatedPdfParserOptions(), workingDirectory);

            Assert.Empty(startInfo.Environment);
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.RedirectStandardInput);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.Equal(workingDirectory, startInfo.WorkingDirectory);
            Assert.Contains(PdfWorkerEntry.Switch, startInfo.ArgumentList);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }
}
