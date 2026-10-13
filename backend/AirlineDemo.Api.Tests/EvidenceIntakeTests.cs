using System.IO.Compression;
using System.Text;
using AirlineDemo.Api.Evidence;

namespace AirlineDemo.Api.Tests;

public sealed class EvidenceIntakeTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "PdfCorpus", name);

    private static EvidenceDocument Fixture(string name) =>
        new(name, File.ReadAllBytes(FixturePath(name)));

    // Synthetic text PDF with one text-bearing content stream per page. Leading whitespace pads the
    // file to padToBytes; offsets are computed after padding, so the cross-reference table stays valid.
    private const string SyntheticContent = "BT /F1 12 Tf 72 720 Td (Synthetic page text) Tj ET";

    // asciiHex encodes each content stream with ASCIIHexDecode so raw bytes hide its operators.
    private static byte[] PdfWithPages(int pages, long padToBytes = 0, string content = SyntheticContent, bool asciiHex = false, string catalogExtra = "", string pageExtra = "", string extraObject = "", bool flate = false, string resources = "")
    {
        var pad = 0;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var bytes = BuildPdf(pages, pad, content, asciiHex, catalogExtra, pageExtra, extraObject, flate, resources);
            if (padToBytes == 0 || bytes.LongLength == padToBytes)
            {
                return bytes;
            }

            pad += (int)(padToBytes - bytes.LongLength);
        }

        throw new InvalidOperationException("Could not pad the synthetic PDF to the requested size.");
    }

    private static byte[] BuildPdf(int pages, int pad, string content, bool asciiHex, string catalogExtra, string pageExtra, string extraObject, bool flate, string resources)
    {
        var kids = string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{4 + (2 * i)} 0 R"));
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra}>>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (var i = 0; i < pages; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {5 + (2 * i)} 0 R /Resources << /Font << /F1 3 0 R >> {resources} >> {pageExtra} >>");
            var body = asciiHex ? Convert.ToHexString(Encoding.ASCII.GetBytes(content)) + ">" : content;
            var filter = asciiHex ? " /Filter /ASCIIHexDecode" : flate ? " /Filter /FlateDecode" : string.Empty;
            objects.Add($"<< /Length {body.Length}{filter} >>\nstream\n{body}\nendstream");
        }

        if (extraObject.Length > 0)
        {
            objects.Add(extraObject);
        }

        var sb = new StringBuilder("%PDF-1.4\n");
        sb.Append(' ', pad);
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] PaddedPdf(long size)
    {
        var header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        var footer = Encoding.ASCII.GetBytes("\n%%EOF\n");
        var bytes = new byte[size];
        Array.Copy(header, bytes, header.Length);
        Array.Copy(footer, 0, bytes, bytes.Length - footer.Length, footer.Length);
        return bytes;
    }

    [Fact]
    public void ValidSyntheticTextPdfCompletesWithDigest()
    {
        var result = EvidenceIntake.Process([Fixture("valid-text.pdf")]);

        Assert.Equal(EvidenceProcessingState.Complete, result.State);
        Assert.Null(result.Failure);
        Assert.Contains("Synthetic return readiness note", result.Content);
        Assert.Equal(EvidenceIntake_Sha(result.Content!), result.ContentDigest);
    }

    [Fact]
    public void PackageAtDocumentCountLimitIsAccepted()
    {
        var docs = Enumerable.Range(0, EvidenceLimits.MaxDocuments).Select(_ => Fixture("valid-text.pdf")).ToList();

        var result = EvidenceIntake.Process(docs);

        Assert.Equal(EvidenceProcessingState.Complete, result.State);
    }

    // AC M-09: a package over the document-count limit is rejected before parsing, with no content.
    [Fact]
    public void PackageOverDocumentCountLimitIsRejectedWithoutContent()
    {
        var docs = Enumerable.Range(0, EvidenceLimits.MaxDocuments + 1).Select(_ => Fixture("valid-text.pdf")).ToList();

        var result = EvidenceIntake.Process(docs);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.LimitExceeded, result.Failure!.Kind);
        Assert.Null(result.Content);
        Assert.True(result.Failure.Retryable);
    }

    // AC M-09: inputs exactly at the byte, package-byte, and page limits are accepted.
    [Fact]
    public void DocumentAtByteLimitIsAccepted()
    {
        var pdf = PdfWithPages(1, EvidenceLimits.MaxDocumentBytes);

        var result = EvidenceIntake.Process([new("at-limit.pdf", pdf)]);

        Assert.Equal(EvidenceLimits.MaxDocumentBytes, pdf.LongLength);
        Assert.Equal(EvidenceProcessingState.Complete, result.State);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void PackageAtTotalByteLimitIsAccepted()
    {
        var docs = Enumerable.Range(0, 5)
            .Select(i => new EvidenceDocument($"part-{i}.pdf", PdfWithPages(1, EvidenceLimits.MaxDocumentBytes)))
            .ToList();

        var result = EvidenceIntake.Process(docs);

        Assert.Equal(EvidenceLimits.MaxPackageBytes, docs.Sum(d => d.Bytes.LongLength));
        Assert.Equal(EvidenceProcessingState.Complete, result.State);
    }

    [Fact]
    public void DocumentAtPageLimitIsAccepted()
    {
        var result = EvidenceIntake.Process([new("at-page-limit.pdf", PdfWithPages(EvidenceLimits.MaxPagesPerDocument))]);

        Assert.Equal(EvidenceProcessingState.Complete, result.State);
        Assert.Contains($"({EvidenceLimits.MaxPagesPerDocument} pages)", result.Content);
    }

    // AC: image payloads are rejected even when valid PDF whitespace separates /Subtype and /Image.
    [Theory]
    [InlineData("/Subtype\n/Image")]
    [InlineData("/Subtype\t/Image")]
    [InlineData("/Subtype\r\n\f/Image")]
    [InlineData("/Subtype/Image")]
    [InlineData("/Subtype%comment\n/Image")]
    [InlineData("/Subtype %c1\r\n%c2\n/Image")]
    public void ImagePayloadIsRejectedRegardlessOfWhitespaceOrComments(string subtype)
    {
        var pdf = Encoding.ASCII.GetBytes($"%PDF-1.4\n1 0 obj << /Type /XObject {subtype} >> endobj\n%%EOF\n");

        var result = EvidenceIntake.Process([new("image.pdf", pdf)]);

        // Raw-token matching was removed: stub bytes that PdfPig cannot parse fail as Corrupt (retryable, no evidence).
        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.Corrupt, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: object streams can hide actions in compressed data, so they are rejected rather than inspected.
    [Fact]
    public void ObjectStreamIsRejectedRetryably()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.5\n1 0 obj << /Type /ObjStm /N 1 /First 4 /Length 0 >> stream\n\nendstream endobj\n%%EOF\n");

        var result = EvidenceIntake.Process([new("objstm.pdf", pdf)]);

        // Object streams are rejected by their decoded /Type name before PdfPig inflates them (UnsupportedFormat, retryable).
        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.UnsupportedFormat, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    [Fact]
    public void DocumentOverByteLimitIsRejectedBeforeParsing()
    {
        var result = EvidenceIntake.Process([new("over-limit.pdf", PaddedPdf(EvidenceLimits.MaxDocumentBytes + 1))]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.LimitExceeded, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    [Fact]
    public void PackageOverTotalByteLimitIsRejected()
    {
        var docs = Enumerable.Range(0, 6)
            .Select(i => new EvidenceDocument($"part-{i}.pdf", PaddedPdf(EvidenceLimits.MaxDocumentBytes)))
            .ToList();

        var result = EvidenceIntake.Process(docs);

        Assert.Equal(EvidenceFailureKind.LimitExceeded, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    [Fact]
    public void DocumentOverPageLimitIsRejected()
    {
        var result = EvidenceIntake.Process([new("long.pdf", PdfWithPages(EvidenceLimits.MaxPagesPerDocument + 1))]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.LimitExceeded, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: a catalog key outside the allowlist is rejected on its own, even when it is not a known active-content key.
    [Fact]
    public void UnlistedCatalogKeyIsRejectedRetryably()
    {
        var result = EvidenceIntake.Process([new("catalog-key.pdf", PdfWithPages(1, catalogExtra: "/Custom 1 0 R "))]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: each prohibited active-content key on a page is rejected on its own, one PDF per rule.
    [Theory]
    [InlineData("/OpenAction << /S /JavaScript >> ")]
    [InlineData("/Names << >> ")]
    [InlineData("/AcroForm << >> ")]
    [InlineData("/EmbeddedFiles << >> ")]
    [InlineData("/JavaScript << >> ")]
    [InlineData("/Launch << >> ")]
    public void ProhibitedPageKeyIsRejectedRetryably(string pageExtra)
    {
        var result = EvidenceIntake.Process([new("page-key.pdf", PdfWithPages(1, pageExtra: pageExtra))]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: an associated file on the catalog or a page is rejected, even when the page has no annotations.
    [Theory]
    [InlineData("/AF [] ", "")]
    [InlineData("", "/AF [] ")]
    public void AssociatedFileIsRejectedOnCatalogOrPage(string catalogExtra, string pageExtra)
    {
        var result = EvidenceIntake.Process([new("assoc.pdf", PdfWithPages(1, catalogExtra: catalogExtra, pageExtra: pageExtra))]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: an indirect /Names dictionary is resolved, so an embedded-files name tree behind it is rejected.
    [Fact]
    public void EmbeddedFilesBehindIndirectNamesIsRejected()
    {
        var pdf = PdfWithPages(1, catalogExtra: "/Names 6 0 R ", extraObject: "<< /EmbeddedFiles << /Names [(a.txt) 7 0 R] >> >>");

        var result = EvidenceIntake.Process([new("indirect-names.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: decoded text is capped, so a small compressed-looking stream cannot expand without bound.
    [Fact]
    public void ExpandedTextOverCharacterLimitIsRejectedRetryably()
    {
        var showText = string.Concat(Enumerable.Repeat("(" + new string('a', 100) + ") Tj ", 20_000));
        var pdf = PdfWithPages(1, content: "BT /F1 12 Tf 72 720 Td " + showText + " ET", asciiHex: true);

        var result = EvidenceIntake.Process([new("expanded.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.LimitExceeded, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC M-07: a failed (truncated) intake is never a complete package and creates no basis.
    [Fact]
    public void TruncatedPdfFailsRetryablyAndCreatesNoBasis()
    {
        var store = new EvidenceBasisStore();

        var result = EvidenceIntake.Process([Fixture("truncated.pdf")]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.Truncated, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Throws<InvalidOperationException>(() => store.Append("case-1", result));
        Assert.Empty(store.Versions("case-1"));
    }

    // AC M-07: a failed (malformed) intake yields no digest and is never evidence.
    [Fact]
    public void MalformedPdfFailsRetryablyWithoutEvidence()
    {
        var result = EvidenceIntake.Process([Fixture("malformed.pdf")]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.Corrupt, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.ContentDigest);
    }

    // AC: active-content payloads fail visibly and retryably and never become evidence.
    [Fact]
    public void ActiveJavaScriptPdfIsRejectedRetryably()
    {
        // Built in-test with a valid xref: the catalog /OpenAction is parsed by PdfPig and rejected.
        var pdf = PdfWithPages(1, 0, catalogExtra: "/OpenAction << /S /JavaScript /JS (app.alert\\('synthetic'\\)) >> ");

        var result = EvidenceIntake.Process([new("active-javascript.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: PDF name hex-escapes (e.g. /J#61vaScript) must not hide active content.
    [Fact]
    public void HexEscapedJavaScriptNameIsRejected()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Names << /J#61vaScript 2 0 R >> >> endobj\n%%EOF\n");

        var result = EvidenceIntake.Process([new("escaped.pdf", pdf)]);

        // Raw-token matching was removed: this stub does not parse, so it fails as Corrupt (retryable, no evidence).
        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.Corrupt, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: a script carried by a page annotation action is rejected even when the catalog is clean.
    [Fact]
    public void JavaScriptPageAnnotationIsRejectedRetryably()
    {
        var pdf = PdfWithPages(1, 0, pageExtra: "/Annots [<< /Type /Annot /Subtype /Link /A << /S /JavaScript /JS (app.alert\\('x'\\)) >> >>]");

        var result = EvidenceIntake.Process([new("annot-js.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: JavaScript chained through an annotation action's /Next entry is rejected, not accepted as evidence.
    [Fact]
    public void JavaScriptChainedThroughAnnotationNextIsRejectedRetryably()
    {
        var pdf = PdfWithPages(1, 0, pageExtra: "/Annots [<< /Type /Annot /Subtype /Link /A << /S /GoTo /D [0 /Fit] /Next << /S /JavaScript /JS (app.alert('x')) >> >> >>]");

        var result = EvidenceIntake.Process([new("annot-next-js.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: a Launch action on a page annotation, direct or chained through /Next, is rejected as executable content.
    [Fact]
    public void LaunchAnnotationActionIsRejectedRetryably()
    {
        var direct = PdfWithPages(1, 0, pageExtra: "/Annots [<< /Type /Annot /Subtype /Link /A << /S /Launch /F (synthetic.txt) >> >>]");
        var chained = PdfWithPages(1, 0, pageExtra: "/Annots [<< /Type /Annot /Subtype /Link /A << /S /GoTo /D [0 /Fit] /Next << /S /Launch /F (synthetic.txt) >> >> >>]");

        foreach (var (name, pdf) in new[] { ("annot-launch.pdf", direct), ("annot-next-launch.pdf", chained) })
        {
            var result = EvidenceIntake.Process([new(name, pdf)]);

            Assert.Equal(EvidenceProcessingState.Failed, result.State);
            Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
            Assert.True(result.Failure.Retryable);
            Assert.Null(result.Content);
        }
    }

    // AC: a Flate content stream that inflates past the per-stream limit is rejected before any text is extracted.
    [Fact]
    public void FlateBombContentStreamIsRejectedBeforeExtraction()
    {
        var inflated = new byte[EvidenceLimits.MaxDecodedBytesPerStream + 1];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(inflated);
        }

        var pdf = PdfWithPages(1, 0, content: Encoding.Latin1.GetString(compressed.ToArray()), flate: true);

        var result = EvidenceIntake.Process([new("flate-bomb.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.LimitExceeded, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: a page-level additional action (/AA) carrying JavaScript is rejected, not accepted as complete evidence.
    [Fact]
    public void PageAdditionalActionJavaScriptIsRejectedRetryably()
    {
        var pdf = PdfWithPages(1, 0, pageExtra: "/AA << /O << /S /JavaScript /JS (app.alert('x')) >> >>");

        var result = EvidenceIntake.Process([new("page-aa.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: form fields (AcroForm) and form widget annotations are ignored or rejected, never counted as evidence.
    [Fact]
    public void AcroFormIsRejectedRetryably()
    {
        var pdf = PdfWithPages(1, 0, catalogExtra: "/AcroForm << /Fields [] >> ");

        var result = EvidenceIntake.Process([new("form.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // Ordinary text that contains structural marker words is accepted; only real object streams are rejected.
    [Fact]
    public void TextContainingObjStmAndXRefNamesIsAccepted()
    {
        var pdf = PdfWithPages(1, 0, content: "BT /F1 12 Tf 72 720 Td (/ObjStm /XRef) Tj ET");

        var result = EvidenceIntake.Process([new("text-objstm.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Complete, result.State);
        Assert.Null(result.Failure);
    }

    // AC: a valid text PDF whose page references a /Form XObject is rejected retryably, with no evidence.
    [Fact]
    public void FormXObjectOnPageIsRejectedRetryably()
    {
        // The page's /Resources references a valid Form XObject (object 6); the text itself is otherwise ordinary.
        var pdf = PdfWithPages(
            1,
            resources: "/XObject << /X1 6 0 R >>",
            extraObject: "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Length 0 >>\nstream\n\nendstream");

        var result = EvidenceIntake.Process([new("form-xobject.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    [Fact]
    public void WidgetFormAnnotationIsRejectedRetryably()
    {
        var pdf = PdfWithPages(1, 0, pageExtra: "/Annots [<< /Type /Annot /Subtype /Widget /FT /Tx >>]");

        var result = EvidenceIntake.Process([new("widget.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: embedded files are rejected through the catalog name tree and through a file attachment annotation.
    [Fact]
    public void EmbeddedFilesNameTreeIsRejectedRetryably()
    {
        var pdf = PdfWithPages(1, 0, catalogExtra: "/Names << /EmbeddedFiles << /Names [] >> >> ");

        var result = EvidenceIntake.Process([new("embedded.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    [Fact]
    public void FileAttachmentAnnotationIsRejectedRetryably()
    {
        var pdf = PdfWithPages(1, 0, pageExtra: "/Annots [<< /Type /Annot /Subtype /FileAttachment /FS << /Type /Filespec /F (a.txt) >> >>]");

        var result = EvidenceIntake.Process([new("attachment.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: nested archives are rejected visibly and retryably.
    [Fact]
    public void NestedArchiveIsRejectedRetryably()
    {
        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0 };

        var result = EvidenceIntake.Process([new("bundle.zip", zip)]);

        Assert.Equal(EvidenceFailureKind.UnsupportedFormat, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC M-10: new content is a new immutable version; prior content is never overwritten, and a
    // witness whose digest differs from the stored basis is rejected.
    [Fact]
    public void StoredVersionsAreAppendOnlyAndWitnessMustMatch()
    {
        var store = new EvidenceBasisStore();
        var first = EvidenceIntake.Process([Fixture("valid-text.pdf")]);
        var second = EvidenceIntake.Process([Fixture("valid-text.pdf"), Fixture("valid-text.pdf")]);

        var v1 = store.Append("case-1", first);
        var v2 = store.Append("case-1", second);

        Assert.Equal(1, v1.Version);
        Assert.Equal(2, v2.Version);
        Assert.Equal(first.Content, store.Versions("case-1")[0].Content);
        Assert.Equal(first.ContentDigest, store.Versions("case-1")[0].ContentDigest);

        store.VerifyWitness("case-1", 1, first.ContentDigest!);
        Assert.Throws<EvidenceWitnessMismatchException>(() =>
            store.VerifyWitness("case-1", 1, second.ContentDigest!));
    }

    // AC: inline images (BI ... ID ... EI) carry no /Subtype /Image key and must still be rejected.
    [Fact]
    public void InlineImagePayloadIsRejectedRetryably()
    {
        var pdf = PdfWithPages(1, 0, "BT /F1 12 Tf 72 720 Td (Text) Tj ET BI /W 1 /H 1 /BPC 8 /CS /G ID \u0000 EI");

        var result = EvidenceIntake.Process([new("inline.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // Ordinary text that contains the token BI is not an inline image and must be accepted.
    [Fact]
    public void TextContainingBiTokenIsAccepted()
    {
        var pdf = PdfWithPages(1, 0, "BT /F1 12 Tf 72 720 Td (BI report, see EI notes) Tj ET");

        var result = EvidenceIntake.Process([new("text-bi.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Complete, result.State);
        Assert.NotNull(result.ContentDigest);
    }

    // The inline image sits in an encoded stream, so the raw bytes carry no BI token; decoded content must still reject it.
    [Fact]
    public void EncodedInlineImagePayloadIsRejectedRetryably()
    {
        var pdf = PdfWithPages(1, 0, "BT /F1 12 Tf 72 720 Td (Text) Tj ET BI /W 1 /H 1 /BPC 8 /CS /G ID \u0000 EI", asciiHex: true);

        var result = EvidenceIntake.Process([new("encoded-inline.pdf", pdf)]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.ActiveContent, result.Failure!.Kind);
        Assert.True(result.Failure.Retryable);
        Assert.Null(result.Content);
    }

    // AC: a witness/basis digest must match its content; a fabricated completed result must not be stored.
    [Fact]
    public void BasisWithDigestMismatchingContentIsRejectedAndNotStored()
    {
        var store = new EvidenceBasisStore();
        var forged = new EvidenceIntakeResult(
            EvidenceProcessingState.Complete,
            "forged content",
            EvidenceIntake_Sha("different content"),
            null);

        Assert.Throws<InvalidOperationException>(() => store.Append("case-1", forged));
        Assert.Empty(store.Versions("case-1"));
    }

    private static string EvidenceIntake_Sha(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
}
