using System.Text.Json;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Investigation;

/// <summary>
/// The only tools an investigation may call. Each one reads the case bound to <see cref="CaseScope"/>. No tool takes a
/// case identifier, so a model cannot name another case.
/// </summary>
public sealed class CaseReadTools(CaseStateStore store, CaseScope scope)
{
    public const string ListInventory = "list_inventory";
    public const string GetApprovedRequirements = "get_approved_requirements";
    public const string GetSourcePage = "get_source_page";
    public const string SearchWithinBasis = "search_within_basis";
    public const string GetProcessingStatus = "get_processing_status";

    public static readonly IReadOnlyList<string> Names =
        [ListInventory, GetApprovedRequirements, GetSourcePage, SearchWithinBasis, GetProcessingStatus];

    private const int MaxSearchHits = 20;

    public string Invoke(string name, IReadOnlyDictionary<string, string> arguments) => name switch
    {
        ListInventory => Json(Inventory()),
        GetApprovedRequirements => Json(ApprovedRequirements()),
        GetSourcePage => Json(SourcePage(arguments)),
        SearchWithinBasis => Json(Search(arguments)),
        GetProcessingStatus => Json(Status()),
        _ => throw new ArgumentException("tool is not declared", nameof(name)),
    };

    private CaseState State() => store.Read(scope);

    private object Inventory() =>
        State().Basis.Select(b => new { b.Version, b.ContentDigest, b.BasisDigest }).ToList();

    private object ApprovedRequirements()
    {
        var latest = State().Basis.LastOrDefault();
        return latest is null
            ? new { Version = 0, Text = string.Empty }
            : new { latest.Version, latest.ContentDigest, Text = latest.Content };
    }

    private object SourcePage(IReadOnlyDictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue("version", out var raw) || !int.TryParse(raw, out var version))
        {
            return new { Error = "version must be an integer" };
        }

        var basis = State().Basis.FirstOrDefault(b => b.Version == version);
        return basis is null
            ? new { Error = "basis version not found in this case" }
            : new { basis.Version, basis.ContentDigest, Text = basis.Content };
    }

    private object Search(IReadOnlyDictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
        {
            return new { Error = "query is required" };
        }

        return State().Basis
            .SelectMany(b => b.Content.Split('\n')
                .Where(line => line.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(line => new { b.Version, b.ContentDigest, Quote = line.Trim() }))
            .Take(MaxSearchHits)
            .ToList();
    }

    private object Status()
    {
        var state = State();
        return new
        {
            state.Revision,
            BasisVersions = state.Basis.Count,
            Receipts = state.ReceiptIds.Count,
            LastAction = state.Audit.LastOrDefault()?.Action,
        };
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
