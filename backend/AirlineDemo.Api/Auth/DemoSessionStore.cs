using System.Security.Cryptography;

namespace AirlineDemo.Api.Auth;

public sealed record DemoSession(string Id, DemoUser User, string CsrfToken, DateTimeOffset ExpiresAt);

public sealed class DemoSessionStore(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    private readonly Dictionary<string, DemoSession> _sessions = [];
    private readonly Lock _gate = new();

    public DemoSession Create(DemoUser user)
    {
        lock (_gate)
        {
            PurgeExpired();
            var session = new DemoSession(NewToken(), user, NewToken(), time.GetUtcNow() + Lifetime);
            _sessions[session.Id] = session;
            return session;
        }
    }

    public DemoSession? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session)) return null;
            var now = time.GetUtcNow();
            if (session.ExpiresAt <= now)
            {
                _sessions.Remove(id);
                return null;
            }

            var slid = session with { ExpiresAt = now + Lifetime };
            _sessions[id] = slid;
            return slid;
        }
    }

    public void Remove(string id)
    {
        lock (_gate) _sessions.Remove(id);
    }

    private void PurgeExpired()
    {
        var now = time.GetUtcNow();
        foreach (var id in _sessions.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToList()) _sessions.Remove(id);
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
