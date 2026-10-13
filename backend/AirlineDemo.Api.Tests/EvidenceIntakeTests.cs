using System.Text;
using AirlineDemo.Api.Evidence;

namespace AirlineDemo.Api.Tests;

public class EvidenceIntakeTests
{
    private static byte[] Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Fixtures", "PdfCorpus")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return File.ReadAllBytes(Path.Combine(dir!, "Fixtures", "PdfCorpus", name));
    }

    /// <summary>Builds a synthetic text PDF with the given page count, optionally padded with trailing comment bytes to an exact size.</summary>
    private static byte[] TextPdf(int pages, int padToBytes = 0)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        int fontId = 3 + 2 * pages;
        sb.Append("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        sb.Append("2 0 obj << /Type /Pages /Count ").Append(pages).Append(" /Kids [");
        for (int i = 0; i < pages; i++)
        {
            sb.Append(3 + 2 * i).Append(" 0 R ");
        }

        sb.Append("] >> endobj\n");
        for (int i = 0; i < pages; i++)
        {
            int pageId = 3 + 2 * i;
            int contentId = pageId + 1;
            sb.Append(pageId).Append(" 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents ")
                .Append(contentId).Append(" 0 R /Resources << /Font << /F1 ").Append(fontId).Append(" 0 R >> >> >> endobj\n");
            var content = $"BT /F1 12 Tf 72 720 Td (synthetic page {i + 1}) Tj ET";
            sb.Append(contentId).Append(" 0 obj << /Length ").Append(content.Length).Append(" >> stream\n")
                .Append(content).Append("\nendstream endobj\n");
        }

        sb.Append(fontId).Append(" 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj\n");
        sb.Append("trailer << /Root 1 0 R >>\n%%EOF\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        if (bytes.Length >= padToBytes)
        {
            return bytes;
        }

        var padded = new byte[padToBytes];
        Array.Fill(padded, (byte)'%');
        Array.Copy(bytes, padded, bytes.Length);
        return padded;
    }

    private static EvidenceDocument Doc(string name, byte[] content) => new(name, content);

    [Fact]
    public void ValidTextPdfWithOrdinaryWordsIsAcceptedAndStoredAsVersionOne()
    {
        var store = new EvidenceBasisStore();

        var result = EvidenceIntake.Process([Doc("valid-text.pdf", Fixture("valid-text.pdf"))], store);

        Assert.True(result.State == EvidenceProcessingState.Complete, result.Reason);
        var version = Assert.Single(result.Versions);
        Assert.Equal(1, version.Version);
        Assert.Contains("Annots", version.Text);
        Assert.Contains("Image", version.Text);
        Assert.Contains("BI", version.Text);
        Assert.Equal(version, store.Latest("valid-text.pdf"));
    }

    [Theory]
    [InlineData("annotated.pdf")]
    [InlineData("image.pdf")]
    [InlineData("embedded-files.pdf")]
    [InlineData("acroform.pdf")]
    public void ContentViolatingOneRuleIsRejectedPermanentlyWithNoEvidence(string file)
    {
        var store = new EvidenceBasisStore();

        var result = EvidenceIntake.Process([Doc(file, Fixture(file))], store);

        Assert.Equal(EvidenceProcessingState.RejectedPermanent, result.State);
        Assert.Empty(result.Versions);
        Assert.Null(store.Latest(file));
    }

    [Theory]
    [InlineData("truncated.pdf")]
    [InlineData("corrupt.pdf")]
    public void UnreadablePdfIsRejectedRetryableWithNoEvidence(string file)
    {
        var store = new EvidenceBasisStore();

        var result = EvidenceIntake.Process([Doc(file, Fixture(file))], store);

        Assert.Equal(EvidenceProcessingState.RejectedRetryable, result.State);
        Assert.Empty(result.Versions);
        Assert.Null(store.Latest(file));
    }

    [Fact]
    public void NestedArchiveIsRejectedByTypeBeforeParsing()
    {
        var store = new EvidenceBasisStore();

        var result = EvidenceIntake.Process([Doc("nested-archive.zip", Fixture("nested-archive.zip"))], store);

        Assert.Equal(EvidenceProcessingState.RejectedPermanent, result.State);
        Assert.Null(store.Latest("nested-archive.zip"));
    }

    [Fact]
    public void TwelveDocumentsAreAcceptedAndThirteenAreRejectedWithoutPartialBasis()
    {
        var store = new EvidenceBasisStore();
        var twelve = Enumerable.Range(1, 12).Select(i => Doc($"doc-{i}.pdf", TextPdf(1))).ToList();
        var thirteen = Enumerable.Range(1, 13).Select(i => Doc($"doc-{i}.pdf", TextPdf(1))).ToList();

        Assert.Equal(EvidenceProcessingState.Complete, EvidenceIntake.Process(twelve, store).State);
        Assert.Equal(EvidenceProcessingState.RejectedPermanent, EvidenceIntake.Process(thirteen, new EvidenceBasisStore()).State);
        Assert.Empty(new EvidenceBasisStore().History("doc-1.pdf"));
    }

    [Fact]
    public void DocumentAtTenMegabytesIsAcceptedAndOneByteOverIsRejected()
    {
        var atLimit = TextPdf(1, (int)EvidenceIntake.MaxDocumentBytes);
        var overLimit = TextPdf(1, (int)EvidenceIntake.MaxDocumentBytes + 1);

        Assert.Equal(EvidenceProcessingState.Complete, EvidenceIntake.Process([Doc("at-limit.pdf", atLimit)], new EvidenceBasisStore()).State);

        var store = new EvidenceBasisStore();
        var result = EvidenceIntake.Process([Doc("over-limit.pdf", overLimit)], store);
        Assert.Equal(EvidenceProcessingState.RejectedPermanent, result.State);
        Assert.Null(store.Latest("over-limit.pdf"));
    }

    [Fact]
    public void DocumentWithTwoHundredPagesIsAcceptedAndTwoHundredOneIsRejected()
    {
        Assert.Equal(EvidenceProcessingState.Complete,
            EvidenceIntake.Process([Doc("two-hundred.pdf", TextPdf(200))], new EvidenceBasisStore()).State);

        var store = new EvidenceBasisStore();
        var result = EvidenceIntake.Process([Doc("two-hundred-one.pdf", TextPdf(201))], store);
        Assert.Equal(EvidenceProcessingState.RejectedPermanent, result.State);
        Assert.Null(store.Latest("two-hundred-one.pdf"));
    }

    [Fact]
    public void PackageWithOneRejectedDocumentCommitsNothing()
    {
        var store = new EvidenceBasisStore();
        var package = new[]
        {
            Doc("good.pdf", Fixture("valid-text.pdf")),
            Doc("bad.pdf", Fixture("annotated.pdf")),
        };

        var result = EvidenceIntake.Process(package, store);

        Assert.Equal(EvidenceProcessingState.RejectedPermanent, result.State);
        Assert.Null(store.Latest("good.pdf"));
        Assert.Null(store.Latest("bad.pdf"));
    }

    [Fact]
    public void NewContentIsAppendedAsImmutableVersionAndWitnessMustMatchDigest()
    {
        var store = new EvidenceBasisStore();
        var first = EvidenceIntake.Process([Doc("doc.pdf", Fixture("valid-text.pdf"))], store).Versions[0];
        var second = EvidenceIntake.Process([Doc("doc.pdf", TextPdf(1))], store).Versions[0];

        Assert.Equal(2, second.Version);
        Assert.NotEqual(first.Digest, second.Digest);
        Assert.Equal(first, store.History("doc.pdf")[0]);

        Assert.True(store.Verify("doc.pdf", 1, first.Digest));
        Assert.False(store.Verify("doc.pdf", 1, second.Digest));
        Assert.False(store.Verify("doc.pdf", 1, "0000"));
        Assert.False(store.Verify("doc.pdf", 3, second.Digest));
    }
}
