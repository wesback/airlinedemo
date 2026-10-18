using System.Text;
using AirlineDemo.Api.Evidence;

namespace AirlineDemo.Api.Tests;

[Collection("IsolatedPdfParser")]
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

    /// <summary>
    /// Builds a synthetic text PDF with the given page count and a valid cross-reference table. When padToBytes is
    /// larger than the document, comment bytes are inserted after the header so the file is exactly padToBytes long.
    /// </summary>
    private static byte[] TextPdf(int pages, int padToBytes = 0)
    {
        var unpadded = BuildTextPdf(pages, 0);
        return padToBytes > unpadded.Length ? BuildTextPdf(pages, padToBytes - unpadded.Length) : unpadded;
    }

    private static byte[] BuildTextPdf(int pages, int padBytes)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        if (padBytes > 0)
        {
            sb.Append('%', padBytes - 1).Append('\n');
        }

        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj ").Append(body).Append('\n');
        }

        int fontId = 3 + 2 * pages;
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + 2 * i} 0 R"));
        Add("<< /Type /Catalog /Pages 2 0 R >> endobj");
        Add($"<< /Type /Pages /Count {pages} /Kids [{kids}] >> endobj");
        for (int i = 0; i < pages; i++)
        {
            int contentId = 4 + 2 * i;
            Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contentId} 0 R /Resources << /Font << /F1 {fontId} 0 R >> >> >> endobj");
            var content = $"BT /F1 12 Tf 72 720 Td (synthetic page {i + 1}) Tj ET";
            Add($"<< /Length {content.Length} >> stream\n{content}\nendstream endobj");
        }

        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj");
        int xrefPosition = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        sb.Append("trailer << /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefPosition.ToString("D10")).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
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
        var twelve = Enumerable.Range(1, 12).Select(i => Doc($"doc-{i}.pdf", TextPdf(1))).ToList();
        var thirteen = Enumerable.Range(1, 13).Select(i => Doc($"doc-{i}.pdf", TextPdf(1))).ToList();

        var acceptedStore = new EvidenceBasisStore();
        Assert.Equal(EvidenceProcessingState.Complete, EvidenceIntake.Process(twelve, acceptedStore).State);
        Assert.NotNull(acceptedStore.Latest("doc-1.pdf"));

        var rejectedStore = new EvidenceBasisStore();
        Assert.Equal(EvidenceProcessingState.RejectedPermanent, EvidenceIntake.Process(thirteen, rejectedStore).State);
        Assert.Empty(rejectedStore.History("doc-1.pdf"));
        Assert.Null(rejectedStore.Latest("doc-13.pdf"));
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
    public void M09PackageAtFiftyMegabytesIsAcceptedAndOneByteOverIsRejectedWithoutBasis()
    {
        var tenMb = (int)EvidenceIntake.MaxDocumentBytes;
        var atLimit = Enumerable.Range(1, 5).Select(i => Doc($"big-{i}.pdf", TextPdf(1, tenMb))).ToList();
        Assert.Equal(EvidenceIntake.MaxPackageBytes, atLimit.Sum(d => d.Content.LongLength));
        Assert.Equal(EvidenceProcessingState.Complete, EvidenceIntake.Process(atLimit, new EvidenceBasisStore()).State);

        var overLimit = new List<EvidenceDocument>(atLimit) { Doc("extra.pdf", new byte[1]) };
        var store = new EvidenceBasisStore();
        var result = EvidenceIntake.Process(overLimit, store);
        Assert.Equal(EvidenceProcessingState.RejectedPermanent, result.State);
        Assert.Empty(result.Versions);
        Assert.Empty(store.History("big-1.pdf"));
        Assert.Null(store.Latest("extra.pdf"));
    }

    [Fact]
    public void DocumentWithTwoHundredPagesIsAcceptedAndTwoHundredOneIsRetryablyRejected()
    {
        Assert.Equal(EvidenceProcessingState.Complete,
            EvidenceIntake.Process([Doc("two-hundred.pdf", TextPdf(200))], new EvidenceBasisStore()).State);

        var store = new EvidenceBasisStore();
        var result = EvidenceIntake.Process([Doc("two-hundred-one.pdf", TextPdf(201))], store);
        Assert.Equal(EvidenceProcessingState.RejectedRetryable, result.State);
        Assert.Empty(result.Versions);
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
