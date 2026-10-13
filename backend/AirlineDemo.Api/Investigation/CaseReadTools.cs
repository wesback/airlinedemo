using Microsoft.Extensions.AI;

namespace AirlineDemo.Api.Investigation;

/// <summary>Read-only view of case data. Every lookup takes the case it belongs to, so a caller can only read that case.</summary>
public interface ICaseReadSource
{
    IReadOnlyList<string> GetInventory(string caseId);
    string? GetApprovedRequirements(string caseId);
    string? GetSourcePage(string caseId, string documentId, int page);
    IReadOnlyList<string> SearchWithinBasis(string caseId, string query);
    string? GetProcessingStatus(string caseId);
}

/// <summary>
/// The five case-scoped read tools offered to the investigator. The case is bound at construction and is never a
/// tool argument, so the model cannot name another case. There are no write, approval, recipient or dispatch tools.
/// </summary>
public sealed class CaseReadTools(string caseId, ICaseReadSource source, IInvestigationLimits limits)
{
    public const string InventoryTool = "get_case_inventory";
    public const string RequirementsTool = "get_approved_requirements";
    public const string SourcePageTool = "get_source_page";
    public const string SearchTool = "search_within_basis";
    public const string StatusTool = "get_processing_status";

    private const int MaxQueryLength = 200;

    private IReadOnlyList<AIFunction>? _functions;

    public IReadOnlyList<AIFunction> Functions => _functions ??=
    [
        AIFunctionFactory.Create(GetInventoryAsync, new AIFunctionFactoryOptions { Name = InventoryTool, Description = "List document ids in this case." }),
        AIFunctionFactory.Create(GetRequirementsAsync, new AIFunctionFactoryOptions { Name = RequirementsTool, Description = "Return the approved requirements for this case." }),
        AIFunctionFactory.Create(GetSourcePageAsync, new AIFunctionFactoryOptions { Name = SourcePageTool, Description = "Return one source page of a document in this case." }),
        AIFunctionFactory.Create(SearchAsync, new AIFunctionFactoryOptions { Name = SearchTool, Description = "Search text within this case's basis documents." }),
        AIFunctionFactory.Create(GetStatusAsync, new AIFunctionFactoryOptions { Name = StatusTool, Description = "Return processing status for this case." }),
    ];

    private async Task<string> GetInventoryAsync(CancellationToken cancellationToken)
    {
        await limits.BeforeToolCallAsync(cancellationToken);
        var ids = source.GetInventory(caseId);
        return ids.Count == 0 ? "no documents" : string.Join('\n', ids);
    }

    private async Task<string> GetRequirementsAsync(CancellationToken cancellationToken)
    {
        await limits.BeforeToolCallAsync(cancellationToken);
        var requirements = source.GetApprovedRequirements(caseId);
        return requirements is null ? "no approved requirements" : Untrusted(requirements);
    }

    private async Task<string> GetSourcePageAsync(string documentId, int page, CancellationToken cancellationToken)
    {
        await limits.BeforeToolCallAsync(cancellationToken);
        var text = source.GetSourcePage(caseId, documentId, page);
        return text is null ? "denied: document page is not part of this case" : Untrusted(text);
    }

    private async Task<string> SearchAsync(string query, CancellationToken cancellationToken)
    {
        await limits.BeforeToolCallAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaxQueryLength)
        {
            return "denied: query must be 1 to 200 characters";
        }

        var hits = source.SearchWithinBasis(caseId, query);
        return hits.Count == 0 ? "no matches" : Untrusted(string.Join('\n', hits));
    }

    // Document-derived tool output reaches the model only inside the untrusted-data delimiter (M-05).
    private static string Untrusted(string text) =>
        $"{BoundedInvestigator.UntrustedBegin}\n{text}\n{BoundedInvestigator.UntrustedEnd}";

    private async Task<string> GetStatusAsync(CancellationToken cancellationToken)
    {
        await limits.BeforeToolCallAsync(cancellationToken);
        return source.GetProcessingStatus(caseId) ?? "unknown";
    }
}
