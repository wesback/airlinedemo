namespace AirlineDemo.Api.Admission;

/// <summary>A refused admission, recorded so a rejected request leaves an explicit trace of the limit that stopped it.</summary>
public sealed record AdmissionEvent(DateTimeOffset At, string Kind, string Subject, string Error);

/// <summary>
/// DD-019 admission budgets: a per-user rolling package submission rate and an application-wide investigation
/// concurrency cap. Rejections are recorded and never create admission state.
/// </summary>
public sealed class WorkloadAdmissionLimits(TimeProvider time)
{
    public const int PackageAttemptsPerWindow = 10;
    public static readonly TimeSpan PackageWindow = TimeSpan.FromSeconds(60);
    public const int MaxConcurrentInvestigations = 2;
    public const string PackageKind = "package-rate-limit";
    public const string InvestigationKind = "investigation-concurrency-limit";
    public const string PackageLimitError = "package submission limit exceeded";
    public const string InvestigationLimitError = "investigation concurrency limit exceeded";
    // Only the most recent refusals are kept, so repeated refusals cannot grow memory without bound.
    public const int MaxRecordedEvents = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _admittedPackages = new(StringComparer.Ordinal);
    private readonly Queue<AdmissionEvent> _events = new();
    private int _activeInvestigations;

    public IReadOnlyList<AdmissionEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>Admits one package submission for a user; only admitted attempts count toward the rolling window.</summary>
    public bool TryAdmitPackage(string userId)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (!_admittedPackages.TryGetValue(userId, out var admitted))
            {
                admitted = new Queue<DateTimeOffset>();
                _admittedPackages[userId] = admitted;
            }

            while (admitted.Count > 0 && admitted.Peek() <= now - PackageWindow)
            {
                admitted.Dequeue();
            }

            if (admitted.Count >= PackageAttemptsPerWindow)
            {
                Record(new AdmissionEvent(now, PackageKind, userId, PackageLimitError));
                return false;
            }

            admitted.Enqueue(now);
            return true;
        }
    }

    /// <summary>Takes an application-wide investigation slot, or returns null and records the refusal.</summary>
    public InvestigationSlot? TryAdmitInvestigation()
    {
        lock (_gate)
        {
            if (_activeInvestigations >= MaxConcurrentInvestigations)
            {
                Record(new AdmissionEvent(time.GetUtcNow(), InvestigationKind, "application", InvestigationLimitError));
                return null;
            }

            _activeInvestigations++;
            return new InvestigationSlot(this);
        }
    }

    private void Record(AdmissionEvent refusal)
    {
        if (_events.Count == MaxRecordedEvents) _events.Dequeue();
        _events.Enqueue(refusal);
    }

    private void Release()
    {
        lock (_gate)
        {
            _activeInvestigations--;
        }
    }

    /// <summary>Holds one investigation slot until disposed. Disposing twice releases the slot once.</summary>
    public sealed class InvestigationSlot : IDisposable
    {
        private readonly WorkloadAdmissionLimits _owner;
        private int _released;

        internal InvestigationSlot(WorkloadAdmissionLimits owner) => _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Release();
            }
        }
    }
}
