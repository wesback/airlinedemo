namespace AirlineDemo.Api.Auth;

public sealed class PasscodeThrottle(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, (int Failures, DateTimeOffset WindowStart)> _failures = [];
    private readonly Lock _gate = new();

    public bool IsLocked(string source)
    {
        lock (_gate)
        {
            if (!_failures.TryGetValue(source, out var state)) return false;
            if (time.GetUtcNow() - state.WindowStart >= Window)
            {
                _failures.Remove(source);
                return false;
            }

            return state.Failures >= MaxFailures;
        }
    }

    public void RecordFailure(string source)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            _failures[source] = _failures.TryGetValue(source, out var state) && now - state.WindowStart < Window
                ? (state.Failures + 1, state.WindowStart)
                : (1, now);
        }
    }

    public void Reset(string source)
    {
        lock (_gate) _failures.Remove(source);
    }
}
