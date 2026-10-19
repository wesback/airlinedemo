namespace AirlineDemo.Api.SourceSystems;

// Provenance envelope for every datum read through a source port (PRD DD-004).
// Field types and the date representation are specified in docs/source-systems-contract.md.
public sealed record SourceRecord<TPayload> where TPayload : class
{
    public string SourceId { get; init; } = string.Empty;

    public string RecordId { get; init; } = string.Empty;

    public int RecordVersion { get; init; }

    // Calendar date in UTC, serialized as YYYY-MM-DD.
    public DateOnly AsOf { get; init; }

    public string CaseId { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public bool Stale { get; init; }

    public TPayload Payload { get; init; } = null!;

    // Fixed to true for every synthetic source; false is rejected by Validate.
    public bool Synthetic { get; init; } = true;

    // Returns every contract violation. An empty list means the record is valid for requestedCaseId.
    public IReadOnlyList<string> Validate(string requestedCaseId)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(SourceId))
        {
            problems.Add("sourceId is required.");
        }

        if (string.IsNullOrWhiteSpace(RecordId))
        {
            problems.Add("recordId is required.");
        }

        if (RecordVersion < 1)
        {
            problems.Add("recordVersion must be a positive integer.");
        }

        if (AsOf == default)
        {
            problems.Add("asOf is required.");
        }

        if (string.IsNullOrWhiteSpace(CaseId))
        {
            problems.Add("caseId is required.");
        }
        else if (!string.Equals(CaseId, requestedCaseId, StringComparison.Ordinal))
        {
            problems.Add("caseId does not match the requested case.");
        }

        if (string.IsNullOrWhiteSpace(Kind))
        {
            problems.Add("kind is required.");
        }

        if (Payload is null)
        {
            problems.Add("payload is required.");
        }

        if (!Synthetic)
        {
            problems.Add("synthetic must be true.");
        }

        return problems;
    }
}
