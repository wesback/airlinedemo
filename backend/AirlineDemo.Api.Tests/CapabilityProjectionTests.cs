using AirlineDemo.Api.Dashboard;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Reviews;
using Xunit;

namespace AirlineDemo.Api.Tests;

public sealed class CapabilityProjectionTests
{
    private const string TraceUrl = "/api/runs/run-1/trace";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ai agent")]
    [InlineData("Robot")]
    public void MissingOrUnrecognizedLabelsNormalizeToUnknown(string? raw)
    {
        Assert.Equal(CapabilityLabels.Unknown, CapabilityLabels.Normalize(raw));
    }

    [Fact]
    public void KnownLabelsPassThroughUnchanged()
    {
        Assert.Equal(CapabilityLabels.Human, CapabilityLabels.Normalize(CapabilityLabels.Human));
    }

    [Fact]
    public void OnlyRecordsInvestigationIsLabeledAiAgentAndLinksToTrace()
    {
        var investigation = CapabilityProjection.Activity(1, "records-investigation", "agent", "found", TraceUrl);
        var review = CapabilityProjection.Activity(2, "technical-review", "reviewer", "accepted", TraceUrl);
        var intake = CapabilityProjection.Activity(3, "intake", "system", "received", TraceUrl);
        var unknown = CapabilityProjection.Activity(4, "other", "system", "?", TraceUrl);

        Assert.Equal(CapabilityLabels.AiAgent, investigation.Label);
        Assert.Equal(TraceUrl, investigation.TraceUrl);
        Assert.Equal(CapabilityLabels.Human, review.Label);
        Assert.Null(review.TraceUrl);
        Assert.Equal(CapabilityLabels.Deterministic, intake.Label);
        Assert.Null(intake.TraceUrl);
        Assert.Equal(CapabilityLabels.Unknown, unknown.Label);
        Assert.Null(unknown.TraceUrl);
    }

    [Fact]
    public void InvestigationWithoutTraceHasNoLink()
    {
        var activity = CapabilityProjection.Activity(1, "records-investigation", "agent", "found", " ");
        Assert.Equal(CapabilityLabels.AiAgent, activity.Label);
        Assert.Null(activity.TraceUrl);
    }

    [Fact]
    public void EveryCapabilityCarriesExactlyOneKnownLabel()
    {
        var cards = CapabilityProjection.Build(CapabilityProjection.ReviewerRole, 2, TraceUrl);
        Assert.All(cards, card => Assert.Contains(card.Label, CapabilityLabels.Known));
        Assert.Equal(CapabilityLabels.AiAgent, Find(cards, "records-investigation").Label);
    }

    [Fact]
    public void DesignOnlyCardsExposeNoItemsCountsOrActions()
    {
        var designCards = CapabilityProjection.Build(CapabilityProjection.ReviewerRole, 5, TraceUrl)
            .Where(card => card.DesignOnly)
            .ToList();

        Assert.NotEmpty(designCards);
        foreach (var card in designCards)
        {
            Assert.Equal(CapabilityLabels.DesignOnly, card.Label);
            Assert.Null(card.OpenItems);
            Assert.Null(card.NextDecisionOwner);
            Assert.Empty(card.Actions);
            Assert.Empty(card.Evidence);
            Assert.NotNull(card.Design);
            Assert.False(string.IsNullOrWhiteSpace(card.Design!.Purpose));
            Assert.False(string.IsNullOrWhiteSpace(card.Design.IntendedAutonomy));
            Assert.False(string.IsNullOrWhiteSpace(card.Design.HumanAuthority));
            Assert.Equal(CapabilityProjection.NotImplemented, card.Design.Notice);
        }
    }

    [Fact]
    public void ReviewerRoleGetsOnlyItsPermittedDecisions()
    {
        var card = Find(CapabilityProjection.Build(CapabilityProjection.ReviewerRole, 2, TraceUrl), "technical-review");

        Assert.Equal(new[] { "accept", "reject" }, card.Actions.Select(a => a.Id));
        Assert.Equal(2, card.OpenItems);
    }

    [Fact]
    public void ForgedDecisionFromNonReviewerRoleIsRejectedByBackend()
    {
        var path = Path.Combine(Path.GetTempPath(), $"capability-{Guid.NewGuid():N}.json");
        try
        {
            var store = new CaseStateStore(path);
            var result = TechnicalReviewPolicy.Decide(store, [], "records-analyst", "forged-actor", "case-a",
                new TechnicalReviewCommand(1, 0, "accept", null), "trace-1");

            Assert.Equal(TechnicalReviewOutcome.Forbidden, result.Outcome);
            Assert.Null(result.Decision);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RoleWithNoAllowedDecisionSeesNoDecisionAction()
    {
        foreach (var role in new[] { "presenter", "unknown-role", "" })
        {
            var cards = CapabilityProjection.Build(role, 2, TraceUrl);
            Assert.Empty(Find(cards, "technical-review").Actions);
        }
        Assert.Empty(CapabilityProjection.PermittedDecisions(null));
    }

    [Fact]
    public void TraceEvidenceAppearsOnlyWhenTraceExists()
    {
        var withTrace = Find(CapabilityProjection.Build("presenter", 0, TraceUrl), "records-investigation");
        var withoutTrace = Find(CapabilityProjection.Build("presenter", 0, null), "records-investigation");

        Assert.Single(withTrace.Evidence);
        Assert.Equal(TraceUrl, withTrace.Evidence[0].Url);
        Assert.Empty(withoutTrace.Evidence);
    }

    private static CapabilityCard Find(IEnumerable<CapabilityCard> cards, string id) =>
        cards.Single(card => card.Id == id);
}
