namespace AirlineDemo.Api.SourceSystems;
public sealed class SourceReadBudget(ISourceReadAdmissionStore store)
{
    public static readonly TimeSpan RollingWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(1);
    public const int MaxRequests = 20;
    public const int MaxInFlightPerCase = 2;

    public Task<Guid?> TryAcquireAsync(string sessionId, string caseId, DateTimeOffset requestedAt, CancellationToken cancellationToken = default) => store.TryAcquireAsync(sessionId, caseId, requestedAt, cancellationToken);

    public Task<bool> ReleaseAsync(Guid leaseId, DateTimeOffset releasedAt) => store.ReleaseAsync(leaseId, releasedAt, CancellationToken.None);
}
