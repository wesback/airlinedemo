namespace AirlineDemo.Api.Dashboard;

public static class CapabilityLabels
{
    public const string AiAgent = "AI agent";
    public const string Deterministic = "Deterministic workflow";
    public const string Human = "Human decision";
    public const string DesignOnly = "Design only";
    public const string Unknown = "Unknown";

    public static readonly IReadOnlyList<string> Known = [AiAgent, Deterministic, Human, DesignOnly];

    public static string Normalize(string? raw) =>
        raw is not null && Known.Contains(raw, StringComparer.Ordinal) ? raw : Unknown;
}

public sealed record DecisionAction(string Id, string Label);

public sealed record EvidenceLink(string Label, string Url);

public sealed record DesignOnlyDetails(string Purpose, string IntendedAutonomy, string HumanAuthority, string Notice);

public sealed record CapabilityCard(
    string Id,
    string Name,
    string Label,
    bool DesignOnly,
    int? OpenItems,
    string? NextDecisionOwner,
    IReadOnlyList<EvidenceLink> Evidence,
    IReadOnlyList<DecisionAction> Actions,
    DesignOnlyDetails? Design);

public sealed record ActivityEvent(int Sequence, string Actor, string Label, string Outcome, string? TraceUrl);

public static class CapabilityProjection
{
    public const string ReviewerRole = "technical-reviewer";
    public const string NotImplemented = "Not implemented in this demo";

    private const string InvestigationSource = "records-investigation";
    private const string ReviewSource = "technical-review";
    private const string IntakeSource = "intake";

    private static readonly DecisionAction Accept = new("accept", "Accept finding");
    private static readonly DecisionAction Reject = new("reject", "Reject finding");

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<DecisionAction>> DecisionsByRole =
        new Dictionary<string, IReadOnlyList<DecisionAction>>(StringComparer.Ordinal)
        {
            [ReviewerRole] = [Accept, Reject],
        };

    public static IReadOnlyList<DecisionAction> PermittedDecisions(string? role) =>
        role is not null && DecisionsByRole.TryGetValue(role, out var decisions) ? decisions : [];

    public static IReadOnlyList<CapabilityCard> Build(string? role, int openReviewItems, string? investigationTraceUrl)
    {
        var traceLinks = TraceLinks(investigationTraceUrl);
        return
        [
            new("records-investigation", "Records investigation", CapabilityLabels.AiAgent, false, null, null, traceLinks, [], null),
            new("technical-review", "Technical review", CapabilityLabels.Human, false,
                Math.Max(0, openReviewItems), "Technical reviewer", [], PermittedDecisions(role), null),
            DesignOnly("transition-planning", "Transition planning",
                "Plan the return-to-service transition for an aircraft asset.",
                "Proposes transition steps for human review only.",
                "A named human approves every transition step."),
            DesignOnly("credit-sanctions-monitoring", "Credit and sanctions monitoring",
                "Monitor counterparties for credit and sanctions exposure.",
                "Flags exposure for human review only.",
                "A named human decides on any hold or release."),
            DesignOnly("portfolio-supervisor", "Portfolio supervisor",
                "Supervise work across a portfolio of cases.",
                "Would route work between agents and reviewers only.",
                "A named human owns portfolio priorities."),
            DesignOnly("invoice-issuance", "Invoice issuance",
                "Issue invoices for settled obligations.",
                "Would draft invoices only; none are issued in this demo.",
                "A named finance human approves each invoice."),
            DesignOnly("formal-notice-dispatch", "Formal notice dispatch",
                "Dispatch formal notices to counterparties.",
                "Would prepare notices only; none are dispatched in this demo.",
                "A named legal human approves each notice."),
        ];
    }

    public static ActivityEvent Activity(int sequence, string source, string actor, string outcome, string? traceUrl)
    {
        var label = source switch
        {
            InvestigationSource => CapabilityLabels.AiAgent,
            ReviewSource => CapabilityLabels.Human,
            IntakeSource => CapabilityLabels.Deterministic,
            _ => CapabilityLabels.Unknown,
        };
        var link = label == CapabilityLabels.AiAgent && !string.IsNullOrWhiteSpace(traceUrl) ? traceUrl : null;
        return new ActivityEvent(sequence, actor, label, outcome, link);
    }

    private static IReadOnlyList<EvidenceLink> TraceLinks(string? url) =>
        string.IsNullOrWhiteSpace(url) ? [] : [new EvidenceLink("Investigation trace", url)];

    private static CapabilityCard DesignOnly(string id, string name, string purpose, string autonomy, string authority) =>
        new(id, name, CapabilityLabels.DesignOnly, true, null, null, [], [],
            new DesignOnlyDetails(purpose, autonomy, authority, NotImplemented));
}
