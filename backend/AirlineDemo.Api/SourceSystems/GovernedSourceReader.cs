using AirlineDemo.Api.Investigation;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.SourceSystems;

public sealed record SourceReadRecordRef(string SourceId, string RecordId, int RecordVersion);

public sealed record GovernedSourceReadResult(
    string SourceId,
    string Mode,
    object? Value,
    int Count,
    string Outcome,
    string? ErrorCode,
    IReadOnlyList<SourceReadRecordRef> RecordRefs);

public sealed class GovernedSourceReader(
    IServiceProvider services,
    SourceReadBudget budget,
    TimeProvider time)
{
    private const int MaxListReadRecords = 101;

    public async Task<GovernedSourceReadResult> ReadAsync(
        string sessionId,
        string caseId,
        string sourceId,
        string? recordId,
        string mode,
        DateTimeOffset requestedAt,
        CancellationToken requestAborted)
    {
        var remaining = requestedAt + SourceReadBudget.ReadDeadline - time.GetUtcNow();
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, deadline.Token);

        Guid? leaseId;
        try
        {
            leaseId = await budget.TryAcquireAsync(sessionId, caseId, requestedAt, linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !requestAborted.IsCancellationRequested)
        {
            return Failure(sourceId, mode, "timed_out", "source_read_timeout");
        }

        if (leaseId is null) return Failure(sourceId, mode, "limited", "source_read_limit");

        try
        {
            try
            {
                var read = recordId is null
                    ? await ListSource(sourceId, caseId, mode, linked.Token)
                    : await GetSource(sourceId, caseId, recordId, mode, linked.Token);
                if (deadline.IsCancellationRequested) return Failure(sourceId, mode, "timed_out", "source_read_timeout");
                if (read.ErrorCode is not null) return Failure(sourceId, mode, OutcomeForError(read.ErrorCode), read.ErrorCode);
                if (recordId is null && read.Count > 100)
                    return Failure(sourceId, mode, "rejected", "source_record_limit");

                var refs = RecordRefs(read.Value);
                var outcome = read.Count == 0 ? "empty"
                    : mode == "stale" ? "stale"
                    : mode == "conflicting" ? "conflicting"
                    : "success";
                return new GovernedSourceReadResult(sourceId, mode, read.Value, read.Count, outcome, null, refs);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !requestAborted.IsCancellationRequested)
            {
                return Failure(sourceId, mode, "timed_out", "source_read_timeout");
            }
        }
        finally
        {
            await budget.ReleaseAsync(leaseId.Value, time.GetUtcNow());
        }
    }

    private async Task<ReadOutcome> ListSource(string sourceId, string caseId, string mode, CancellationToken cancellationToken) =>
        sourceId switch
        {
            "technical" => await List(services.GetRequiredService<ISourceSystem<TechnicalPayload>>(), caseId, sourceId, mode, cancellationToken),
            "maintenance" => await List(services.GetRequiredService<ISourceSystem<MaintenancePayload>>(), caseId, sourceId, mode, cancellationToken),
            "finance" => await List(services.GetRequiredService<ISourceSystem<FinancePayload>>(), caseId, sourceId, mode, cancellationToken),
            "records" => await List(services.GetRequiredService<ISourceSystem<RecordsPayload>>(), caseId, sourceId, mode, cancellationToken),
            "contracts" => await List(services.GetRequiredService<ISourceSystem<ContractsPayload>>(), caseId, sourceId, mode, cancellationToken),
            _ => new ReadOutcome(null, 0, "unknown_source"),
        };

    private async Task<ReadOutcome> GetSource(string sourceId, string caseId, string recordId, string mode,
        CancellationToken cancellationToken) =>
        sourceId switch
        {
            "technical" => await Get(services.GetRequiredService<ISourceSystem<TechnicalPayload>>(), caseId, recordId, sourceId, mode, cancellationToken),
            "maintenance" => await Get(services.GetRequiredService<ISourceSystem<MaintenancePayload>>(), caseId, recordId, sourceId, mode, cancellationToken),
            "finance" => await Get(services.GetRequiredService<ISourceSystem<FinancePayload>>(), caseId, recordId, sourceId, mode, cancellationToken),
            "records" => await Get(services.GetRequiredService<ISourceSystem<RecordsPayload>>(), caseId, recordId, sourceId, mode, cancellationToken),
            "contracts" => await Get(services.GetRequiredService<ISourceSystem<ContractsPayload>>(), caseId, recordId, sourceId, mode, cancellationToken),
            _ => new ReadOutcome(null, 0, "unknown_source"),
        };

    private static async Task<ReadOutcome> List<T>(ISourceSystem<T> source, string caseId, string sourceId, string mode,
        CancellationToken cancellationToken) where T : class
    {
        var maxRecords = mode == "conflicting" ? (MaxListReadRecords + 1) / 2 : MaxListReadRecords;
        var result = await SourceModeFindingPolicy.Apply(source, sourceId, mode).ListAsync(caseId, cancellationToken, maxRecords);
        return result switch
        {
            SourceReadSucceeded<IReadOnlyList<SourceRecord<T>>> success =>
                success.Value.Any(record => !string.Equals(record.CaseId, caseId, StringComparison.Ordinal))
                    ? new ReadOutcome(null, 0, "source_case_mismatch")
                    : new ReadOutcome(success.Value, success.Value.Count, null),
            SourceUnavailable<IReadOnlyList<SourceRecord<T>>> => new ReadOutcome(null, 0, "source_unavailable"),
            _ => new ReadOutcome(null, 0, "unknown_source"),
        };
    }

    private static async Task<ReadOutcome> Get<T>(ISourceSystem<T> source, string caseId, string recordId, string sourceId,
        string mode, CancellationToken cancellationToken) where T : class
    {
        var result = await SourceModeFindingPolicy.Apply(source, sourceId, mode).GetAsync(caseId, recordId, cancellationToken);
        return result switch
        {
            SourceReadSucceeded<SourceRecord<T>> success =>
                string.Equals(success.Value.CaseId, caseId, StringComparison.Ordinal)
                    ? new ReadOutcome(success.Value, 1, null)
                    : new ReadOutcome(null, 0, "source_case_mismatch"),
            SourceRecordNotFound<SourceRecord<T>> => new ReadOutcome(null, 0, "record_not_found"),
            SourceUnavailable<SourceRecord<T>> => new ReadOutcome(null, 0, "source_unavailable"),
            _ => new ReadOutcome(null, 0, "unknown_source"),
        };
    }

    private static IReadOnlyList<SourceReadRecordRef> RecordRefs(object? value) => value switch
    {
        IReadOnlyList<SourceRecord<TechnicalPayload>> records => records.Select(ToRef).ToArray(),
        IReadOnlyList<SourceRecord<MaintenancePayload>> records => records.Select(ToRef).ToArray(),
        IReadOnlyList<SourceRecord<FinancePayload>> records => records.Select(ToRef).ToArray(),
        IReadOnlyList<SourceRecord<RecordsPayload>> records => records.Select(ToRef).ToArray(),
        IReadOnlyList<SourceRecord<ContractsPayload>> records => records.Select(ToRef).ToArray(),
        SourceRecord<TechnicalPayload> record => [ToRef(record)],
        SourceRecord<MaintenancePayload> record => [ToRef(record)],
        SourceRecord<FinancePayload> record => [ToRef(record)],
        SourceRecord<RecordsPayload> record => [ToRef(record)],
        SourceRecord<ContractsPayload> record => [ToRef(record)],
        _ => [],
    };

    private static SourceReadRecordRef ToRef<T>(SourceRecord<T> record) where T : class =>
        new(record.SourceId, record.RecordId, record.RecordVersion);

    private static string OutcomeForError(string errorCode) => errorCode switch
    {
        "source_unavailable" => "unavailable",
        "record_not_found" => "not_found",
        "source_read_limit" => "limited",
        "source_read_timeout" => "timed_out",
        "source_record_limit" => "rejected",
        _ => "failed",
    };

    private static GovernedSourceReadResult Failure(string sourceId, string mode, string outcome, string errorCode) =>
        new(sourceId, mode, null, 0, outcome, errorCode, []);

    private sealed record ReadOutcome(object? Value, int Count, string? ErrorCode);
}
