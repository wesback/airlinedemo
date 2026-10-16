namespace AirlineDemo.Api.SourceSystems;

// Read-only, case-scoped port through which application code reads one source system.
// Synthetic stubs and any future real adapter implement this same contract.
// The port has no write, update or delete members by design (PRD FR-001, AC-001).
public interface ISourceSystem<TPayload> where TPayload : class
{
    Task<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>> ListAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<SourceReadResult<SourceRecord<TPayload>>> GetAsync(
        string caseId,
        string recordId,
        CancellationToken cancellationToken = default);
}

// Outcome of a list or get read. Each outcome is a distinct type so that callers
// cannot mistake an unavailable source for an empty successful list.
public abstract record SourceReadResult<TSuccess>;

public sealed record SourceReadSucceeded<TSuccess>(TSuccess Value) : SourceReadResult<TSuccess>;

// Error code: source_unavailable.
public sealed record SourceUnavailable<TSuccess>(string SourceId) : SourceReadResult<TSuccess>
{
    public const string ErrorCode = "source_unavailable";
}

// The source identifier is not registered.
public sealed record UnknownSource<TSuccess>(string SourceId) : SourceReadResult<TSuccess>;

// The source exists and is available, but no record has the requested identifier for this case.
public sealed record SourceRecordNotFound<TSuccess>(string RecordId) : SourceReadResult<TSuccess>;
