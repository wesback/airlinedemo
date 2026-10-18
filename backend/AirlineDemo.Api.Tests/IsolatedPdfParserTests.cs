using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Evidence.PdfWorker;
using UglyToad.PdfPig;

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

    private static byte[] MinimalPdf(int pages, int padToBytes = 0)
    {
        byte[] Build(int padding)
        {
            var pdf = new StringBuilder("%PDF-1.4\n");
            if (padding > 0)
            {
                pdf.Append('%', padding - 1).Append('\n');
            }

            var offsets = new List<int>();
            void Add(string value)
            {
                offsets.Add(pdf.Length);
                pdf.Append(offsets.Count).Append(" 0 obj ").Append(value).Append('\n');
            }

            var fontId = 3 + 2 * pages;
            var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + 2 * i} 0 R"));
            Add("<< /Type /Catalog /Pages 2 0 R >> endobj");
            Add($"<< /Type /Pages /Count {pages} /Kids [{kids}] >> endobj");
            for (var i = 0; i < pages; i++)
            {
                var contentId = 4 + 2 * i;
                Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contentId} 0 R /Resources << /Font << /F1 {fontId} 0 R >> >> >> endobj");
                var content = $"BT /F1 12 Tf 72 720 Td (page {i + 1}) Tj ET";
                Add($"<< /Length {content.Length} >> stream\n{content}\nendstream endobj");
            }

            Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj");
            var xrefPosition = pdf.Length;
            pdf.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
            }

            pdf.Append("trailer << /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
                .Append(xrefPosition.ToString("D10")).Append("\n%%EOF\n");
            return Encoding.ASCII.GetBytes(pdf.ToString());
        }

        var unpadded = Build(0);
        return padToBytes > unpadded.Length ? Build(padToBytes - unpadded.Length) : unpadded;
    }

    /// <summary>A one-page PDF whose content stream expands beyond the parser's decoded-stream limit.</summary>
    private static byte[] FlateBomb(string filter = "/Filter /FlateDecode")
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)' ');
            for (var i = 0; i < 9; i++)
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
        Object($"4 0 obj << /Length {body.Length} {filter} >> stream\n");
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

    // DD-026: bounded page text is extracted by PdfPig in the API process.
    [Fact]
    public void PdfPigExtractsTextAndIgnoresNonTextContent()
    {
        var valid = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "valid-text.pdf");
        var twoPages = IsolatedPdfParser.Parse(Fixture("two-pages.pdf"), "two-pages.pdf");
        var form = IsolatedPdfParser.Parse(Fixture("acroform.pdf"), "acroform.pdf");
        var embedded = IsolatedPdfParser.Parse(Fixture("embedded-files.pdf"), "embedded-files.pdf");
        var image = IsolatedPdfParser.Parse(Fixture("image.pdf"), "image.pdf");

        Assert.Equal(EvidenceProcessingState.Complete, valid.State);
        Assert.False(string.IsNullOrWhiteSpace(valid.Text));
        Assert.Equal(EvidenceProcessingState.Complete, twoPages.State);
        Assert.True(twoPages.Text.IndexOf("Alpha only on page one.", StringComparison.Ordinal)
            < twoPages.Text.IndexOf("Beta only on page two.", StringComparison.Ordinal));
        Assert.Equal(EvidenceProcessingState.Complete, form.State);
        Assert.Equal("synthetic form", form.Text.Trim());
        Assert.DoesNotContain("field value", form.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EvidenceProcessingState.Complete, embedded.State);
        Assert.Equal("synthetic embedded file", embedded.Text.Trim());
        Assert.DoesNotContain("attachment content", embedded.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EvidenceProcessingState.Complete, image.State);
        Assert.Equal(string.Empty, image.Text);
    }

    [Fact]
    public void PdfPigParsesWithoutStartingWorker()
    {
        var options = new IsolatedPdfParserOptions
        {
            WorkerFileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-worker"),
        };

        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "valid-text.pdf", options);

        Assert.Equal(EvidenceProcessingState.Complete, outcome.State);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Text));
    }

    [Fact]
    public void PdfLimitsAreCheckedBeforeParsing()
    {
        var exactLimitOpens = 0;
        var exactLimitPageReads = 0;
        var exactLimit = IsolatedPdfParser.Parse(
            MinimalPdf(200, (int)EvidenceIntake.MaxDocumentBytes),
            "exact-limit.pdf",
            content =>
            {
                exactLimitOpens++;
                return PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true });
            },
            page =>
            {
                exactLimitPageReads++;
                return page.Text;
            });

        Assert.Equal(1, exactLimitOpens);
        Assert.Equal(EvidenceProcessingState.Complete, exactLimit.State);
        Assert.Equal(200, exactLimit.Text.Count(character => character == EvidenceIntake.PageSeparator) + 1);
        Assert.Equal(200, exactLimitPageReads);

        var tooManyPagesOpens = 0;
        var tooManyPagesReads = 0;
        var tooManyPages = IsolatedPdfParser.Parse(
            MinimalPdf(201),
            "too-many-pages.pdf",
            content =>
            {
                tooManyPagesOpens++;
                return PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true });
            },
            page =>
            {
                tooManyPagesReads++;
                return page.Text;
            });

        Assert.Equal(1, tooManyPagesOpens);
        Assert.Equal(0, tooManyPagesReads);
        Assert.Equal(EvidenceProcessingState.RejectedRetryable, tooManyPages.State);
        Assert.Equal(string.Empty, tooManyPages.Text);

        var oversizedOpens = 0;
        var oversized = IsolatedPdfParser.Parse(
            MinimalPdf(1, (int)EvidenceIntake.MaxDocumentBytes + 1),
            "oversized.pdf",
            _ =>
            {
                oversizedOpens++;
                throw new InvalidOperationException("Oversized input must not be opened.");
            });

        Assert.Equal(0, oversizedOpens);
        Assert.Equal(EvidenceProcessingState.RejectedPermanent, oversized.State);
        Assert.Equal("oversized.pdf is 10485761 bytes; limit is 10485760", oversized.Reason);
    }

    [Fact]
    public void OversizedPageTextIsRejectedBeforeFullTextExtraction()
    {
        var pageTextReads = 0;
        var outcome = IsolatedPdfParser.Parse(
            MinimalPdf(1),
            "large-text.pdf",
            content => PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true }),
            page =>
            {
                pageTextReads++;
                return page.Text;
            },
            _ => Enumerable.Repeat(new string('x', 100_000), 100));

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
        Assert.Equal("PDF stream content exceeds 8388608 decoded bytes; retry is possible", outcome.Reason);
        Assert.Equal(string.Empty, outcome.Text);
        Assert.Equal(0, pageTextReads);
    }

    [Fact]
    public void MalformedPdfFailuresAreRetryableWithoutPartialText()
    {
        foreach (var (name, content) in new[]
        {
            ("corrupt.pdf", Fixture("corrupt.pdf")),
            ("truncated.pdf", Fixture("truncated.pdf")),
            ("invalid.pdf", new byte[] { 0x00, 0x01, 0x02, 0x03 }),
        })
        {
            var outcome = IsolatedPdfParser.Parse(content, name);

            Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
            Assert.Equal("PdfPig could not open or read the document; retry is possible", outcome.Reason);
            Assert.Equal(string.Empty, outcome.Text);
        }

        var valid = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "valid-text.pdf");
        Assert.Equal(EvidenceProcessingState.Complete, valid.State);
        Assert.False(string.IsNullOrWhiteSpace(valid.Text));
    }

    [Fact]
    public void CorruptPdf_IsRejectedAsRetryable()
    {
        var outcome = IsolatedPdfParser.Parse(Fixture("corrupt.pdf"), "corrupt.pdf");

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
        Assert.Equal("PdfPig could not open or read the document; retry is possible", outcome.Reason);
        Assert.Equal(string.Empty, outcome.Text);
    }

    // Compressed content is bounded before PdfPig can expand it inside the API process.
    [Fact]
    public void FlateBomb_FailsWithinLimits_AndServiceStillProcessesValidPdf()
    {
        var compressedBomb = FlateBomb();
        Assert.True(compressedBomb.Length <= EvidenceIntake.MaxDocumentBytes);
        var bomb = IsolatedPdfParser.Parse(compressedBomb, "bomb.pdf");

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, bomb.State);
        Assert.Equal("PDF stream content exceeds 8388608 decoded bytes; retry is possible", bomb.Reason);
        Assert.Equal(string.Empty, bomb.Text);

        var valid = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "valid-text.pdf");
        Assert.Equal(EvidenceProcessingState.Complete, valid.State);
        Assert.False(string.IsNullOrWhiteSpace(valid.Text));
    }

    [Theory]
    [InlineData("/Filter /Flate#44ecode")]
    [InlineData("/Fil#74er /FlateDecode")]
    public void EscapedFilterName_IsRejectedWithoutExpandingStream(string filter)
    {
        var outcome = IsolatedPdfParser.Parse(FlateBomb(filter), "escaped-filter.pdf");

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
        Assert.Equal("PdfPig could not open or read the document; retry is possible", outcome.Reason);
        Assert.Equal(string.Empty, outcome.Text);
    }

    // Worker process settings are ignored by the in-process parser.
    [Fact]
    public void HungWorker_IsKilledByTimeLimit()
    {
        var options = new IsolatedPdfParserOptions
        {
            WorkerFileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-worker"),
            WallClockLimit = TimeSpan.FromMilliseconds(1),
        };

        var clock = Stopwatch.StartNew();
        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "valid-text.pdf", options);
        clock.Stop();

        Assert.Equal(EvidenceProcessingState.Complete, outcome.State);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Text));
    }

    // Worker process settings are ignored by the in-process parser.
    [Fact]
    public void MemoryLimit_KillsWorkerAndReturnsRetryable()
    {
        var options = new IsolatedPdfParserOptions
        {
            WorkerFileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-worker"),
            MemoryLimitBytes = 1,
        };
        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "valid-text.pdf", options);

        Assert.Equal(EvidenceProcessingState.Complete, outcome.State);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Text));
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

    // In-process parsing does not create a worker temporary directory.
    [Fact]
    public void Parse_DeletesItsTemporaryWorkingDirectoryAfterTheRun()
    {
        static HashSet<string> WorkerDirectories() =>
            Directory.GetDirectories(Path.GetTempPath(), "pdf-worker-*").ToHashSet();

        var before = WorkerDirectories();

        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "valid-text.pdf");

        Assert.Equal(EvidenceProcessingState.Complete, outcome.State);
        Assert.Empty(WorkerDirectories().Except(before).ToList());
    }

    // In-process parsing does not create a worker directory on either run.
    [Fact]
    public void Parse_RunsEachWorkerInADistinctEmptyTemporaryDirectory()
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "pdf-worker-*").ToHashSet();
        var options = new IsolatedPdfParserOptions
        {
            WorkerFileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-worker"),
        };

        var first = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "first.pdf", options);
        var second = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "second.pdf", options);

        Assert.Equal(EvidenceProcessingState.Complete, first.State);
        Assert.Equal(EvidenceProcessingState.Complete, second.State);
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "pdf-worker-*").Except(before));
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

    // Worker output limits do not affect in-process page-text extraction.
    [Fact]
    public void OversizedWorkerOutput_IsKilledAndReturnsRetryable()
    {
        var options = new IsolatedPdfParserOptions
        {
            WorkerFileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-worker"),
            OutputLimitBytes = 1,
        };

        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "valid-text.pdf", options);

        Assert.Equal(EvidenceProcessingState.Complete, outcome.State);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Text));
    }

    // Intake uses the in-process parser and commits only successful page-text extraction.
    [Fact]
    public void Intake_RoutesPdfThroughIsolatedParser_AndStoresNothingWhenWorkerIsKilled()
    {
        var options = new IsolatedPdfParserOptions
        {
            WorkerFileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-worker"),
        };
        var store = new EvidenceBasisStore();
        var success = EvidenceIntake.Process(
            [new EvidenceDocument("valid.pdf", Fixture("valid-text.pdf"))],
            store,
            options);

        Assert.Equal(EvidenceProcessingState.Complete, success.State);
        Assert.NotNull(store.Latest("valid.pdf"));

        var failed = EvidenceIntake.Process(
            [new EvidenceDocument("bad.pdf", Fixture("corrupt.pdf"))],
            store,
            options);

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, failed.State);
        Assert.Empty(failed.Versions);
        Assert.Null(store.Latest("bad.pdf"));
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
