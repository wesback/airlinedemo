using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Runs;

public sealed class DemoRunResetException(string message) : Exception(message);

public sealed record DemoRunFallback(string FixtureId, string ApplicationVersion);

public sealed record DemoRunResetResult(string NewRunId, CaseState NewRun, DemoRunFallback Fallback);

// Starts a clean run for the authorized owner. The selected run is only authorized, never written,
// so its audit, receipts and revision stay as they were. Reset accepts no evaluator input.
public sealed class DemoRunReset(ICaseStateStore store)
{
    public DemoRunResetResult Reset(string runId, string owner, string newRunId, string? fixtureId, string? applicationVersion)
    {
        // Throws CaseAccessDeniedException for unknown runs or runs owned by someone else, before any write.
        store.AuthorizeCase(runId, owner);

        if (string.Equals(runId, newRunId, StringComparison.Ordinal))
        {
            throw new DemoRunResetException("A reset must start a new run id.");
        }

        var fallback = ResolveFallback(fixtureId, applicationVersion);
        var scope = store.CreateCase(newRunId, owner);
        return new DemoRunResetResult(newRunId, store.Read(scope), fallback);
    }

    // Fallback results must be traceable to a fixture and an application version; missing metadata blocks selection.
    public static DemoRunFallback ResolveFallback(string? fixtureId, string? applicationVersion)
    {
        if (string.IsNullOrWhiteSpace(fixtureId))
        {
            throw new DemoRunResetException("Fallback blocked: fixture id is missing.");
        }

        if (string.IsNullOrWhiteSpace(applicationVersion))
        {
            throw new DemoRunResetException("Fallback blocked: application version metadata is missing.");
        }

        return new DemoRunFallback(fixtureId, applicationVersion);
    }
}
