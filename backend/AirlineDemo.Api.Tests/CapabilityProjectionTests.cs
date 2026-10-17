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
        Assert.Equal(CapabilityLabels.Simulated, CapabilityLabels.Normalize(CapabilityLabels.Simulated));
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
        Assert.Equal(CapabilityLabels.Simulated, review.Label);
        Assert.Null(review.TraceUrl);
        Assert.Equal(CapabilityLabels.Unknown, intake.Label);
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
    public void AC022_ProjectsExactlySixCanonicalCapabilities()
    {
        var legacyCards = CapabilityProjection.Build(CapabilityProjection.ReviewerRole, 2, TraceUrl);
        var stateCards = CapabilityProjection.Build("case-a", CapabilityProjection.ReviewerRole, EmptyState(),
            DateTimeOffset.UnixEpoch).Capabilities;
        var expected = new[]
        {
            ("records-investigation", CapabilityLabels.AiAgent, "L4", "L3"),
            ("utilisation-reserves", CapabilityLabels.RulesBased, "L3", "L2"),
            ("contract-compliance", CapabilityLabels.RulesBased, "L3", "L2"),
            ("transition-planning", CapabilityLabels.DesignOnly, "L2", "design-only"),
            ("credit-sanctions-monitoring", CapabilityLabels.DesignOnly, "L2", "design-only"),
            ("portfolio-supervisor", CapabilityLabels.DesignOnly, CapabilityLabels.Unknown, "design-only"),
        };

        AssertCanonicalTable(legacyCards, expected);
        AssertCanonicalTable(stateCards, expected);
    }

    [Fact]
    public void AC020_DesignOnlyCardsHaveNoWorkflowState()
    {
        var cards = CapabilityProjection.Build(CapabilityProjection.ReviewerRole, 5, TraceUrl)
            .Concat(CapabilityProjection.Build("case-a", CapabilityProjection.ReviewerRole, EmptyState(),
                DateTimeOffset.UnixEpoch).Capabilities);
        var designCards = cards.Where(card => card.DesignOnly).ToArray();
        Assert.Equal(6, designCards.Length);

        foreach (var card in designCards)
        {
            Assert.Contains(card.Id, new[] { "transition-planning", "credit-sanctions-monitoring", "portfolio-supervisor" });
            Assert.Equal(CapabilityLabels.DesignOnly, card.Label);
            Assert.True(card.DesignOnly);
            Assert.Null(card.OpenItems);
            Assert.Null(card.NextDecisionOwner);
            Assert.Empty(card.Actions);
            Assert.Empty(card.Evidence);
            Assert.NotNull(card.Design);
            Assert.False(string.IsNullOrWhiteSpace(card.Design!.Purpose));
            Assert.False(string.IsNullOrWhiteSpace(card.Design.IntendedAutonomy));
            Assert.False(string.IsNullOrWhiteSpace(card.Design.HumanAuthority));
            Assert.Equal("Not implemented in this demo", card.Design.Notice);
        }
    }

    [Fact]
    public void AC020_UsesExactLabelsAndUnknownFallback()
    {
        var capabilityLabels = new[]
        {
            ("records-investigation", CapabilityLabels.AiAgent),
            ("records-policy", CapabilityLabels.RulesBased),
            ("evidence-request-dispatch", CapabilityLabels.RulesBased),
            ("reserves-calculation", CapabilityLabels.RulesBased),
            ("reserve-calculation", CapabilityLabels.RulesBased),
            ("obligation-status", CapabilityLabels.RulesBased),
            ("notice-drafting", CapabilityLabels.RulesBased),
            ("create-work-item", CapabilityLabels.RulesBased),
            ("work-item-creation", CapabilityLabels.RulesBased),
            ("change-work-item-state", CapabilityLabels.RulesBased),
            ("work-item-state-change", CapabilityLabels.RulesBased),
            ("mock-partner-inbox", CapabilityLabels.Simulated),
            ("mock-partner-response", CapabilityLabels.Simulated),
            ("technical-review", CapabilityLabels.Simulated),
            ("finance-review", CapabilityLabels.Simulated),
            ("legal-review", CapabilityLabels.Simulated),
            ("transition-planning", CapabilityLabels.DesignOnly),
            ("credit-sanctions-monitoring", CapabilityLabels.DesignOnly),
            ("portfolio-supervisor", CapabilityLabels.DesignOnly),
            ("invoice-issuance", CapabilityLabels.DesignOnly),
            ("formal-notice-dispatch", CapabilityLabels.DesignOnly),
            ("not-a-capability", CapabilityLabels.Unknown),
        };
        foreach (var (input, expected) in capabilityLabels)
        {
            Assert.Equal(expected, CapabilityProjection.LabelForCapability(input));
            Assert.Equal(expected, CapabilityProjection.Activity(1, input, "reviewer-1", "accepted", null).Label);
        }

        var auditLabels = new[]
        {
            ("technical-review.decided", CapabilityLabels.Simulated),
            ("reserve.decided", CapabilityLabels.Simulated),
            ("obligation.legal-decided", CapabilityLabels.Simulated),
            ("mock-response.accepted", CapabilityLabels.Simulated),
            ("create-work-item", CapabilityLabels.RulesBased),
            ("change-work-item-state", CapabilityLabels.RulesBased),
            ("unlisted.action", CapabilityLabels.Unknown),
        };
        foreach (var (input, expected) in auditLabels)
        {
            Assert.Equal(expected, CapabilityProjection.LabelForAuditAction(input));
        }

        var overrideAttempt = CapabilityProjection.Activity(2, "unknown-source", "human-reviewer", "AI agent", null);
        Assert.Equal(CapabilityLabels.Unknown, overrideAttempt.Label);
        Assert.Equal("human-reviewer", overrideAttempt.Actor);
    }

    [Fact]
    public void TechnicalReviewDecisionsRemainSeparateFromCapabilities()
    {
        var cards = CapabilityProjection.Build(CapabilityProjection.ReviewerRole, 2, TraceUrl);
        Assert.DoesNotContain(cards, card => card.Id == "technical-review");
        Assert.Equal(new[] { "accept", "reject" },
            CapabilityProjection.PermittedDecisions(CapabilityProjection.ReviewerRole).Select(action => action.Id));
        Assert.Empty(CapabilityProjection.PermittedDecisions("records-analyst"));

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
            Assert.DoesNotContain(cards, card => card.Actions.Count > 0);
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

    private static CaseState EmptyState() =>
        new("case-a", "owner", 0, [], [], [], new Dictionary<string, string>(), [], []);

    private static void AssertCanonicalTable(
        IReadOnlyList<CapabilityCard> cards,
        IReadOnlyList<(string Id, string Label, string CaseStudy, string Demo)> expected)
    {
        Assert.Equal(expected.Select(row => row.Id), cards.Select(card => card.Id));
        Assert.Equal(expected.Select(row => row.Label), cards.Select(card => card.Label));
        Assert.Equal(expected.Select(row => row.CaseStudy), cards.Select(card => card.CaseStudyAutonomy));
        Assert.Equal(expected.Select(row => row.Demo), cards.Select(card => card.DemoAutonomy));
        Assert.DoesNotContain(cards, card => card.Id is "technical-review" or "invoice-issuance" or "formal-notice-dispatch");
    }
}
