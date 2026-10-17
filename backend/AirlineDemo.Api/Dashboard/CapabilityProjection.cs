using System.Text.Json;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Dashboard;

public static class CapabilityLabels
{
    public const string AiAgent = "AI agent";
    public const string RulesBased = "Rules-based";
    public const string Simulated = "Simulated";
    public const string DesignOnly = "Design-only";
    public const string Unknown = "Unknown";

    public static readonly IReadOnlyList<string> Known = [AiAgent, RulesBased, Simulated, DesignOnly];

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
    DesignOnlyDetails? Design,
    string CaseStudyAutonomy = "",
    string DemoAutonomy = "");

public sealed record ActivityEvent(int Sequence, string Actor, string Label, string Outcome, string? TraceUrl);

public sealed record DashboardQueueItem(
    string ItemId,
    string CapabilityId,
    string State,
    string Owner,
    long AgeSeconds,
    string Label);

public sealed record DashboardActivityEvent(long Sequence, string Actor, string Label, int? BasisVersion, string Outcome);

public sealed record DashboardProjection(
    string CaseId,
    IReadOnlyList<DecisionAction> AllowedDecisions,
    IReadOnlyList<DashboardQueueItem> Queue,
    IReadOnlyList<CapabilityCard> Capabilities,
    IReadOnlyList<DashboardActivityEvent> Activity);

public static class CapabilityProjection
{
    public const string ReviewerRole = "technical-reviewer";
    public const string NotImplemented = "Not implemented in this demo";

    private static readonly DecisionAction Accept = new("accept", "Accept finding");
    private static readonly DecisionAction Reject = new("reject", "Reject finding");

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<DecisionAction>> DecisionsByRole =
        new Dictionary<string, IReadOnlyList<DecisionAction>>(StringComparer.Ordinal)
        {
            [ReviewerRole] = [Accept, Reject],
        };

    private static readonly CapabilityDefinition[] Definitions =
    [
        new("records-investigation", "Records investigation (records digitisation)", CapabilityLabels.AiAgent,
            "L4", "L3"),
        new("utilisation-reserves", "Utilisation and reserves", CapabilityLabels.RulesBased, "L3", "L2"),
        new("contract-compliance", "Contract compliance", CapabilityLabels.RulesBased, "L3", "L2"),
        new("transition-planning", "Transition planning", CapabilityLabels.DesignOnly,
            "L2", "design-only",
            "Plan the return-to-service transition for an aircraft asset.",
            "Proposes transition steps for human review only.",
            "A named human approves every transition step."),
        new("credit-sanctions-monitoring", "Credit and sanctions monitoring", CapabilityLabels.DesignOnly,
            "L2", "design-only",
            "Monitor counterparties for credit and sanctions exposure.",
            "Flags exposure for human review only.",
            "A named human decides on any hold or release."),
        new("portfolio-supervisor", "Portfolio supervisor", CapabilityLabels.DesignOnly,
            CapabilityLabels.Unknown, "design-only",
            "Supervise work across a portfolio of cases.",
            "Would route work between agents and reviewers only.",
            "A named human owns portfolio priorities."),
    ];

    public static IReadOnlyList<DecisionAction> PermittedDecisions(string? role) =>
        role is not null && DecisionsByRole.TryGetValue(role, out var decisions) ? decisions : [];

    public static DashboardProjection Build(string caseId, string? role, CaseState state, DateTimeOffset now)
    {
        var queue = (state.WorkItems ?? [])
            .Where(item => item.State == "open" && item.Owner == role)
            .Select(item => new DashboardQueueItem(item.ItemId, item.CapabilityId, item.State, item.Owner,
                Math.Max(0, (long)Math.Floor((now - item.CreatedAt).TotalSeconds)), LabelForCapability(item.CapabilityId)))
            .ToList();
        var traces = (state.Traces ?? [])
            .Where(trace => trace.CaseId == caseId)
            .Select(trace => new EvidenceLink("Investigation trace",
                $"/api/cases/{Uri.EscapeDataString(caseId)}/dashboard/traces/{Uri.EscapeDataString(trace.RunId)}/{Uri.EscapeDataString(trace.InvestigationId)}"))
            .ToList();
        var cards = Definitions.Select(definition =>
        {
            var isDesignOnly = definition.Label == CapabilityLabels.DesignOnly;
            var items = queue.Where(item => item.CapabilityId == definition.Id).ToList();
            return new CapabilityCard(
                definition.Id, definition.Name, definition.Label, isDesignOnly,
                isDesignOnly ? null : items.Count,
                isDesignOnly ? null : NextDecisionItem(items)?.Owner,
                definition.Id == "records-investigation" ? traces : [],
                [], isDesignOnly
                    ? new DesignOnlyDetails(definition.Purpose!, definition.IntendedAutonomy!, definition.HumanAuthority!, NotImplemented)
                    : null,
                definition.CaseStudyAutonomy, definition.DemoAutonomy);
        }).ToList();
        var activity = state.Audit.OrderBy(item => item.Sequence)
            .Select(item => new DashboardActivityEvent(item.Sequence, item.Actor, LabelForAuditAction(item.Action),
                BasisVersion(item.Detail), item.Action))
            .ToList();

        return new DashboardProjection(caseId, PermittedDecisions(role), queue, cards, activity);
    }

    public static string LabelForCapability(string capabilityId) =>
        Definitions.FirstOrDefault(definition => definition.Id == capabilityId)?.Label ?? capabilityId switch
        {
            "records-policy" or "evidence-request-dispatch" or "reserves-calculation" or "reserve-calculation" or
                "obligation-status" or "notice-drafting" or "create-work-item" or "work-item-creation" or
                "change-work-item-state" or "work-item-state-change" =>
                CapabilityLabels.RulesBased,
            "mock-partner-inbox" or "mock-partner-response" or "technical-review" or "finance-review" or
                "legal-review" => CapabilityLabels.Simulated,
            "invoice-issuance" or "formal-notice-dispatch" => CapabilityLabels.DesignOnly,
            _ => CapabilityLabels.Unknown,
        };

    internal static DashboardQueueItem? NextDecisionItem(IEnumerable<DashboardQueueItem> items) =>
        items.OrderByDescending(item => item.AgeSeconds)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal).FirstOrDefault();

    public static string LabelForAuditAction(string action) => action switch
    {
        "technical-review.decided" or "reserve.decided" or "obligation.legal-decided" or "mock-response.accepted" =>
            CapabilityLabels.Simulated,
        "create-work-item" or "change-work-item-state" => CapabilityLabels.RulesBased,
        _ => CapabilityLabels.Unknown,
    };

    public static IReadOnlyList<CapabilityCard> Build(string? role, int openReviewItems, string? investigationTraceUrl)
    {
        var traceLinks = TraceLinks(investigationTraceUrl);
        return Definitions.Select(definition => CreateCard(definition,
            definition.Id == "records-investigation" ? traceLinks : [])).ToArray();
    }

    public static ActivityEvent Activity(int sequence, string source, string actor, string outcome, string? traceUrl)
    {
        var label = LabelForCapability(source);
        var link = label == CapabilityLabels.AiAgent && !string.IsNullOrWhiteSpace(traceUrl) ? traceUrl : null;
        return new ActivityEvent(sequence, actor, label, outcome, link);
    }

    private static IReadOnlyList<EvidenceLink> TraceLinks(string? url) =>
        string.IsNullOrWhiteSpace(url) ? [] : [new EvidenceLink("Investigation trace", url)];

    private static CapabilityCard CreateCard(CapabilityDefinition definition, IReadOnlyList<EvidenceLink> evidence)
    {
        var isDesignOnly = definition.Label == CapabilityLabels.DesignOnly;
        return new CapabilityCard(definition.Id, definition.Name, definition.Label, isDesignOnly, null, null,
            evidence, [], isDesignOnly
                ? new DesignOnlyDetails(definition.Purpose, definition.IntendedAutonomy, definition.HumanAuthority, NotImplemented)
                : null,
            definition.CaseStudyAutonomy, definition.DemoAutonomy);
    }

    private static int? BasisVersion(string detail)
    {
        try
        {
            using var document = JsonDocument.Parse(detail);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var property = document.RootElement.EnumerateObject()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, "basisVersion", StringComparison.OrdinalIgnoreCase));
            return property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var version)
                ? version
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CapabilityDefinition(
        string Id,
        string Name,
        string Label,
        string CaseStudyAutonomy,
        string DemoAutonomy,
        string Purpose = "",
        string IntendedAutonomy = "",
        string HumanAuthority = "");
}
