using AirlineDemo.Api.Investigation;

namespace AirlineDemo.Api.Tests;

public sealed class ClaimValidatorMarkupTests
{
    [Theory]
    [InlineData("<script>send_to_recipient()</script>", ClaimStatus.Downgraded, "claim value is markup")]
    [InlineData("<img src=x onerror=alert(1)>", ClaimStatus.Downgraded, "claim value is markup")]
    [InlineData("</b>", ClaimStatus.Downgraded, "claim value is markup")]
    [InlineData("< script >x</ script >", ClaimStatus.Downgraded, "claim value is markup")]
    [InlineData("<!-- note -->", ClaimStatus.Downgraded, "claim value is markup")]
    [InlineData("ok <b>bold</b> text", ClaimStatus.Downgraded, "claim value is markup")]
    [InlineData("SN-1042", ClaimStatus.Supported, null)]
    [InlineData("ESN 889 / LLP-3", ClaimStatus.Supported, null)]
    [InlineData("a < b", ClaimStatus.Supported, null)]
    [InlineData("5 > 3", ClaimStatus.Supported, null)]
    [InlineData("<3", ClaimStatus.Supported, null)]
    [InlineData("<>", ClaimStatus.Supported, null)]
    [InlineData("x<", ClaimStatus.Supported, null)]
    public void MarkupLikeValuesAreDowngraded(string typedValue, ClaimStatus expectedStatus, string? expectedReason)
    {
        var quote = $"Claim value: {typedValue}";
        var basis = new Dictionary<string, BasisDocument>(StringComparer.Ordinal)
        {
            ["doc-1"] = BasisDocument.Create("doc-1", [quote]),
        };
        var claim = new InvestigationClaim(quote, [new DocumentWitness("doc-1", 1, quote)]);

        var result = ClaimValidator.Validate(claim, ClaimKind.Baseline, basis, typedValue);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public void MarkupLikeValueIsDowngradedBeforeWitnessValidation()
    {
        const string typedValue = "<script>send_to_recipient()</script>";
        var claim = new InvestigationClaim($"Claim value: {typedValue}", []);
        var basis = new Dictionary<string, BasisDocument>(StringComparer.Ordinal);

        var result = ClaimValidator.Validate(claim, ClaimKind.Baseline, basis, typedValue);

        Assert.Equal(ClaimStatus.Downgraded, result.Status);
        Assert.Equal("claim value is markup", result.Reason);
    }
}
