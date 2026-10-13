using System.Text;
using AirlineDemo.Api.Evidence;

namespace AirlineDemo.Api.Tests;

public sealed class EvidenceIntakeTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "PdfCorpus", name);

    private static EvidenceDocument Fixture(string name) =>
        new(name, File.ReadAllBytes(FixturePath(name)));

    private static byte[] PdfWithPages(int pages)
    {
        var kids = string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{i + 3} 0 R"));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>",
        };
        for (var i = 0; i < pages; i++)
        {
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        }

        var sb = new StringBuilder("%PDF-1.4\n");
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
        return Encoding.ASCII.GetBytes(sb.ToString());
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

    [Fact]
    public void DocumentAtByteLimitIsNotRejectedForSize()
    {
        var result = EvidenceIntake.Process([new("at-limit.pdf", PaddedPdf(EvidenceLimits.MaxDocumentBytes))]);

        Assert.NotEqual(EvidenceFailureKind.LimitExceeded, result.Failure?.Kind);
    }

    [Fact]
    public void DocumentOverByteLimitIsRejectedBeforeParsing()
    {
        var result = EvidenceIntake.Process([new("over-limit.pdf", PaddedPdf(EvidenceLimits.MaxDocumentBytes + 1))]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.LimitExceeded, result.Failure!.Kind);
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
        Assert.Null(result.Content);
    }

    [Fact]
    public void DocumentOverPageLimitIsRejected()
    {
        var result = EvidenceIntake.Process([new("long.pdf", PdfWithPages(EvidenceLimits.MaxPagesPerDocument + 1))]);

        Assert.Equal(EvidenceProcessingState.Failed, result.State);
        Assert.Equal(EvidenceFailureKind.LimitExceeded, result.Failure!.Kind);
        Assert.Null(result.Content);
    }

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
        var result = EvidenceIntake.Process([Fixture("active-javascript.pdf")]);

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

    private static string EvidenceIntake_Sha(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
}
