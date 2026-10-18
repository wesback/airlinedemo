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

    private static byte[] SharedContentPdf(int pages, string content, bool indirectContentsArray = false)
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Add(string value)
        {
            offsets.Add(pdf.Length);
            pdf.Append(offsets.Count).Append(" 0 obj ").Append(value).Append('\n');
        }

        var arrayId = pages + 3;
        var contentId = indirectContentsArray ? pages + 4 : arrayId;
        var fontId = contentId + 1;
        var firstNonPageId = pages + 3;
        var kids = string.Join(" ", Enumerable.Range(3, pages).Select(pageId => $"{pageId} 0 R"));
        Add("<< /Type /Catalog /Pages 2 0 R >> endobj");
        Add($"<< /Type /Pages /Count {pages} /Kids [{kids}] >> endobj");
        for (var pageId = 3; pageId < firstNonPageId; pageId++)
        {
            var contentsReference = indirectContentsArray ? arrayId : contentId;
            Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contentsReference} 0 R /Resources << /Font << /F1 {fontId} 0 R >> >> >> endobj");
        }

        if (indirectContentsArray)
        {
            Add($"[{contentId} 0 R] endobj");
        }

        Add($"<< /Length {content.Length} >> stream\n{content}\nendstream endobj");
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

    /// <summary>A one-page PDF with a Flate stream containing the requested number of decoded whitespace bytes.</summary>
    private static byte[] FlatePdf(int decodedBytes, string filter = "/Filter /FlateDecode")
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var chunk = new byte[8192];
            Array.Fill(chunk, (byte)' ');
            while (decodedBytes > 0)
            {
                var count = Math.Min(decodedBytes, chunk.Length);
                zlib.Write(chunk, 0, count);
                decodedBytes -= count;
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

    /// <summary>A one-page PDF whose content stream expands beyond the parser's decoded-stream limit.</summary>
    private static byte[] FlateBomb(string filter = "/Filter /FlateDecode") =>
        FlatePdf(9 * 1024 * 1024, filter);

    private static void Write(MemoryStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes);
    }

    private static byte[] SingleStreamPdf(byte[] data, string dictionaryEntries = "")
    {
        using var pdf = new MemoryStream();
        Write(pdf, "%PDF-1.4\n");
        Write(pdf, $"1 0 obj << /Length {data.Length}{dictionaryEntries} >> stream\n");
        pdf.Write(data);
        Write(pdf, "\nendstream endobj\n");
        return pdf.ToArray();
    }

    // Worker extraction accepts plain text and rejects unsupported embedded content.
    [Fact]
    public void PdfWorkerExtractsTextAndRejectsUnsupportedContent()
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
        Assert.Equal(EvidenceProcessingState.RejectedPermanent, form.State);
        Assert.Equal("embedded files or form fields are not accepted", form.Reason);
        Assert.Equal(string.Empty, form.Text);
        Assert.Equal(EvidenceProcessingState.RejectedPermanent, embedded.State);
        Assert.Equal("embedded files or form fields are not accepted", embedded.Reason);
        Assert.Equal(string.Empty, embedded.Text);
        Assert.Equal(EvidenceProcessingState.RejectedPermanent, image.State);
        Assert.Equal("images are not accepted", image.Reason);
        Assert.Equal(string.Empty, image.Text);
    }

    [Fact]
    public void PdfWorkerParsesValidDocument()
    {
        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "valid-text.pdf");

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
    public void UnicodeGlyphExpansionIsRejectedBeforeOpeningPdfPig()
    {
        var target = string.Concat(Enumerable.Repeat("0041", 1024));
        byte[] Compress(byte[] data)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(data);
            }

            return compressed.ToArray();
        }

        var cmaps = new[]
        {
            ($"1 beginbfchar\n<41> <{target}>\nendbfchar", ""),
            ($"1 beginbfchar\n<41> <{target}>\nendbfchar", " /Filter /FlateDecode"),
            ($"1 beginbfrange\n<41> <41> <{target}>\nendbfrange", ""),
            ($"1 beginbfrange\n<41> <41> <{target}>\nendbfrange", " /Filter /FlateDecode"),
            ($"1 beginbfchar\n% endbfchar\n<41> <{target}>\nendbfchar", ""),
            ("1 " + string.Concat(Enumerable.Repeat("beginbfchar\n", 10_000)), ""),
            ("1 " + string.Concat(Enumerable.Repeat("beginbfrange\n", 10_000)), ""),
        };

        foreach (var (cmap, filter) in cmaps)
        {
            var data = Encoding.ASCII.GetBytes(cmap);
            var opens = 0;
            var outcome = IsolatedPdfParser.Parse(
                SingleStreamPdf(filter.Length == 0 ? data : Compress(data), filter),
                "expanded-glyphs.pdf",
                _ =>
                {
                    opens++;
                    return PdfDocument.Open(MinimalPdf(1), new ParsingOptions { UseLenientParsing = true });
                });

            Assert.Equal(0, opens);
            Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
            Assert.Equal("PdfPig could not open or read the document; retry is possible", outcome.Reason);
            Assert.Equal(string.Empty, outcome.Text);
        }

        var validOpens = 0;
        var valid = IsolatedPdfParser.Parse(
            SingleStreamPdf(Encoding.ASCII.GetBytes("1 beginbfchar\n<41> <0041>\nendbfchar")),
            "bounded-cmap.pdf",
            _ =>
            {
                validOpens++;
                return PdfDocument.Open(MinimalPdf(1), new ParsingOptions { UseLenientParsing = true });
            });

        Assert.Equal(1, validOpens);
        Assert.Equal(EvidenceProcessingState.Complete, valid.State);
        Assert.Equal("page 1", valid.Text);
    }

    [Fact]
    public void LargeTextStreamIsRejectedBeforeOpeningPdfPig()
    {
        var content = Encoding.ASCII.GetBytes($"({new string('x', 512 * 1024)}) Tj");
        var opens = 0;
        var outcome = IsolatedPdfParser.Parse(
            SingleStreamPdf(content),
            "large-text.pdf",
            _ =>
            {
                opens++;
                throw new InvalidOperationException("Oversized page text must not reach PdfPig.");
            });

        Assert.Equal(0, opens);
        Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
        Assert.Equal("PDF text source exceeds 524288 bytes; retry is possible", outcome.Reason);
        Assert.Equal(string.Empty, outcome.Text);
    }

    [Fact]
    public void RepeatedPageContentReferencesAreBoundedBeforeOpeningPdfPig()
    {
        var content = $"BT /F1 12 Tf 72 720 Td ({new string('x', 500 * 1024)}) Tj ET";
        var boundedOpens = 0;
        var bounded = IsolatedPdfParser.Parse(
            SharedContentPdf(1, content),
            "single-reference.pdf",
            _ =>
            {
                boundedOpens++;
                return PdfDocument.Open(MinimalPdf(1), new ParsingOptions { UseLenientParsing = true });
            },
            _ => "bounded text",
            _ => Array.Empty<string>());

        Assert.Equal(1, boundedOpens);
        Assert.Equal(EvidenceProcessingState.Complete, bounded.State);
        Assert.Equal("bounded text", bounded.Text);

        var repeatedOpens = 0;
        var repeated = IsolatedPdfParser.Parse(
            SharedContentPdf(200, content),
            "repeated-content.pdf",
            _ =>
            {
                repeatedOpens++;
                throw new InvalidOperationException("Repeated content must not reach PdfPig.");
            });

        Assert.Equal(0, repeatedOpens);
        Assert.Equal(EvidenceProcessingState.RejectedRetryable, repeated.State);
        Assert.Equal("PDF text source exceeds 524288 bytes; retry is possible", repeated.Reason);
        Assert.Equal(string.Empty, repeated.Text);

        var indirectArrayOpens = 0;
        var indirectArray = IsolatedPdfParser.Parse(
            SharedContentPdf(200, content, indirectContentsArray: true),
            "indirect-content-array.pdf",
            _ =>
            {
                indirectArrayOpens++;
                throw new InvalidOperationException("Indirect content arrays must not reach PdfPig.");
            });

        Assert.Equal(0, indirectArrayOpens);
        Assert.Equal(EvidenceProcessingState.RejectedRetryable, indirectArray.State);
        Assert.Equal("PdfPig could not open or read the document; retry is possible", indirectArray.Reason);
        Assert.Equal(string.Empty, indirectArray.Text);
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
    [InlineData("/Filter /FlateDecode", "PDF stream content exceeds 8388608 decoded bytes; retry is possible")]
    [InlineData("/Filter /Flate#44ecode", "PdfPig could not open or read the document; retry is possible")]
    [InlineData("/Fil#74er /FlateDecode", "PdfPig could not open or read the document; retry is possible")]
    public void StreamFilterPreflightRejectsBeforeStartingWorker(string filter, string expectedReason)
    {
        var workerStarts = 0;
        var input = FlateBomb(filter);
        Assert.True(input.Length <= EvidenceIntake.MaxDocumentBytes);
        var outcome = IsolatedPdfParser.Parse(
            input,
            "escaped-filter.pdf",
            IsolatedPdfParserOptions.Default,
            startInfo =>
            {
                workerStarts++;
                return Process.Start(startInfo);
            });

        Assert.Equal(0, workerStarts);
        Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
        Assert.Equal(expectedReason, outcome.Reason);
        Assert.Equal(string.Empty, outcome.Text);
    }

    [Fact]
    public void StreamFilterPreflightAcceptsExactDecodedLimitAndStartsWorkerOnce()
    {
        var workerStarts = 0;
        var input = FlatePdf(8 * 1024 * 1024);
        Assert.True(input.Length <= EvidenceIntake.MaxDocumentBytes);
        var outcome = IsolatedPdfParser.Parse(
            input,
            "exact-decoded-limit.pdf",
            IsolatedPdfParserOptions.Default,
            startInfo =>
            {
                workerStarts++;
                return Process.Start(startInfo);
            });

        Assert.Equal(1, workerStarts);
        Assert.Equal(EvidenceProcessingState.Complete, outcome.State);
        Assert.Equal("accepted", outcome.Reason);
        Assert.Equal("\n", outcome.Text);
    }

    [Fact]
    public void HungWorker_IsKilledByTimeLimit()
    {
        var options = new IsolatedPdfParserOptions
        {
            WallClockLimit = TimeSpan.FromMilliseconds(1),
        };

        var clock = Stopwatch.StartNew();
        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "valid-text.pdf", options);
        clock.Stop();

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
        Assert.Equal("worker exceeded the 0 second time limit; the document was not accepted, retry is possible", outcome.Reason);
        Assert.Equal(string.Empty, outcome.Text);
    }

    [Fact]
    public void MemoryLimit_KillsWorkerAndReturnsRetryable()
    {
        var options = new IsolatedPdfParserOptions
        {
            MemoryLimitBytes = 1,
        };
        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "valid-text.pdf", options);

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

    // The worker's temporary directory is removed after parsing.
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

    // Each worker run receives a fresh temporary directory which is removed afterwards.
    [Fact]
    public void Parse_RunsEachWorkerInADistinctEmptyTemporaryDirectory()
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "pdf-worker-*").ToHashSet();
        var first = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "first.pdf");
        var second = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "second.pdf");

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

    // The parent refuses worker output larger than the configured limit.
    [Fact]
    public void OversizedWorkerOutput_IsKilledAndReturnsRetryable()
    {
        var options = new IsolatedPdfParserOptions
        {
            OutputLimitBytes = 1,
        };

        var outcome = IsolatedPdfParser.Parse(Fixture("valid-text.pdf"), "valid-text.pdf", options);

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, outcome.State);
        Assert.Equal(string.Empty, outcome.Text);
    }

    // Intake commits only successful worker extraction.
    [Fact]
    public void Intake_RoutesPdfThroughIsolatedParser_AndStoresNothingWhenWorkerIsKilled()
    {
        var store = new EvidenceBasisStore();
        var success = EvidenceIntake.Process(
            [new EvidenceDocument("valid.pdf", Fixture("valid-text.pdf"))],
            store);

        Assert.Equal(EvidenceProcessingState.Complete, success.State);
        Assert.NotNull(store.Latest("valid.pdf"));

        var failed = EvidenceIntake.Process(
            [new EvidenceDocument("bad.pdf", Fixture("corrupt.pdf"))],
            store);

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
