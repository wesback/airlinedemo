using AirlineDemo.Api.Persistence;
namespace AirlineDemo.Api.Runs;

public sealed class CasePackBlockedVariantResetException(string code, int statusCode, string message)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>Starts a new Case C blocked-variant run through the case-state store's atomic commit.</summary>
public sealed class CasePackBlockedVariantReset(ICaseStateStore store)
{
    private TimeProvider? _timeProvider;
    public CasePackBlockedVariantReset(ICaseStateStore store, TimeProvider timeProvider) : this(store)
        => _timeProvider = timeProvider;

    public CasePackResetCommitResult Reset(
        CaseScope scope, string actor, string commandId, long expectedRevision, string variant)
    {
        if (scope is null
            || !string.Equals(scope.CaseId, "cp-case-c", StringComparison.Ordinal))
            throw new CasePackBlockedVariantResetException("unsupported_target", 422, "Only the Case C blocked variant can be reset.");
        if (expectedRevision < 0)
            throw new CasePackBlockedVariantResetException("validation_error", 400, "Reset command fields are invalid.");
        try
        {
            return store.CommitBlockedVariantReset(scope, expectedRevision, actor, commandId, variant,
                _timeProvider?.GetUtcNow());
        }
        catch (ArgumentException exception) when (exception.ParamName == "variant")
        {
            throw new CasePackBlockedVariantResetException("unsupported_target", 422, "Only the Case C blocked variant can be reset.");
        }
        catch (ArgumentException)
        {
            throw new CasePackBlockedVariantResetException("validation_error", 400, "Reset command fields are invalid.");
        }
        catch (RevisionConflictException)
        {
            throw new CasePackBlockedVariantResetException("stale_revision", 409, "Case revision is stale; no change was made.");
        }
        catch (CasePackResetCommandConflictException)
        {
            throw new CasePackBlockedVariantResetException("command_id_conflict", 409, "Reset command id was already used for a different command.");
        }
    }
}
