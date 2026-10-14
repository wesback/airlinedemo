namespace AirlineDemo.Api.Investigation;

/// <summary>The limit an investigation exceeded (M-09).</summary>
public enum InvestigationBreach { ToolCalls, ModelCalls, Time, InputTokens, OutputTokens, Retries }

/// <summary>Raised when an investigation exceeds a limit. The investigation then returns no result.</summary>
public sealed class InvestigationLimitExceededException(InvestigationBreach breach) : Exception($"investigation limit exceeded: {breach}")
{
    public InvestigationBreach Breach { get; } = breach;
}

/// <summary>M-09 per-investigation limits. Defaults are the approved values; tests may use smaller ones.</summary>
public sealed record InvestigationLimitOptions
{
    public static InvestigationLimitOptions Default { get; } = new();

    public int MaxToolCalls { get; init; } = 20;
    public int MaxModelCalls { get; init; } = 8;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxInputTokensPerCall { get; init; } = 16_000;
    public int MaxOutputTokensPerCall { get; init; } = 2_000;
    public int MaxRetries { get; init; } = 2;
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Limits for one investigation. The investigator disposes them when the investigation ends, which releases the
/// deadline timer. A breach is sticky: once tripped, every later check throws the same breach.
/// </summary>
public sealed class InvestigationLimits : IInvestigationLimits, IDisposable
{
    private readonly InvestigationLimitOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _deadline = new();
    private readonly List<TimeSpan> _backoffs = [];
    private int _toolCalls;
    private int _modelCalls;

    public InvestigationLimits(InvestigationLimitOptions? options = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options ?? InvestigationLimitOptions.Default;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _deadline.Token.Register(() =>
        {
            if (!Released)
            {
                Breach ??= InvestigationBreach.Time;
            }
        });
        _deadline.CancelAfter(_options.Timeout);
    }

    public InvestigationBreach? Breach { get; private set; }

    public bool Released { get; private set; }

    public IReadOnlyList<TimeSpan> Backoffs => _backoffs;

    public int ToolCalls => _toolCalls;

    public int ModelCalls => _modelCalls;

    public CancellationToken ExecutionToken => _deadline.Token;

    public int? MaxOutputTokens => _options.MaxOutputTokensPerCall;

    public ValueTask BeforeToolCallAsync(CancellationToken cancellationToken)
    {
        Check();
        if (_toolCalls >= _options.MaxToolCalls)
        {
            Trip(InvestigationBreach.ToolCalls);
        }

        _toolCalls++;
        return default;
    }

    public ValueTask BeforeModelCallAsync(CancellationToken cancellationToken) => BeforeModelCallAsync(0, cancellationToken);

    public ValueTask BeforeModelCallAsync(int estimatedInputTokens, CancellationToken cancellationToken)
    {
        Check();
        if (_modelCalls >= _options.MaxModelCalls)
        {
            Trip(InvestigationBreach.ModelCalls);
        }

        if (estimatedInputTokens > _options.MaxInputTokensPerCall)
        {
            Trip(InvestigationBreach.InputTokens);
        }

        _modelCalls++;
        return default;
    }

    public ValueTask AfterModelCallAsync(long? inputTokens, long? outputTokens, CancellationToken cancellationToken)
    {
        if (inputTokens > _options.MaxInputTokensPerCall)
        {
            Trip(InvestigationBreach.InputTokens);
        }

        if (outputTokens > _options.MaxOutputTokensPerCall)
        {
            Trip(InvestigationBreach.OutputTokens);
        }

        return default;
    }

    // Exponential backoff: InitialBackoff, then 2x, up to MaxRetries retries. The next failure after that trips Retries.
    public async ValueTask<bool> TryBackoffAsync(int retriesUsed, CancellationToken cancellationToken)
    {
        Check();
        if (retriesUsed >= _options.MaxRetries)
        {
            Trip(InvestigationBreach.Retries);
        }

        var backoff = TimeSpan.FromTicks(_options.InitialBackoff.Ticks << retriesUsed);
        _backoffs.Add(backoff);
        await _delay(backoff, cancellationToken);
        return true;
    }

    // Releases the deadline timer. The breach and counters stay readable for the trace.
    public void Dispose()
    {
        if (Released)
        {
            return;
        }

        Released = true;
        _deadline.Dispose();
    }

    private void Check()
    {
        if (Breach is null && _deadline.IsCancellationRequested)
        {
            Breach = InvestigationBreach.Time;
        }

        if (Breach is { } breach)
        {
            throw new InvestigationLimitExceededException(breach);
        }
    }

    private void Trip(InvestigationBreach breach)
    {
        Breach ??= breach;
        throw new InvestigationLimitExceededException(Breach.Value);
    }
}
