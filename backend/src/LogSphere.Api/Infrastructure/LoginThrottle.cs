using System.Collections.Concurrent;

namespace LogSphere.Api.Infrastructure;

/// <summary>In-memory brute-force guard for dashboard login: after too many failures for a
/// given (ip, username) key within the window, the key is locked out for a cooldown period.
/// Deliberately simple and process-local (mirrors the ingestion rate limiter); front a
/// multi-instance deployment with a shared limiter at the proxy if needed.</summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset First, DateTimeOffset? LockedUntil)> _state = new();

    private static string Key(string? ip, string username) => $"{ip ?? "?"}|{username.ToLowerInvariant()}";

    /// <summary>Returns the remaining lockout if the key is currently locked, else null.</summary>
    public TimeSpan? RetryAfter(string? ip, string username)
    {
        if (!_state.TryGetValue(Key(ip, username), out var s) || s.LockedUntil is not { } until) return null;
        var now = DateTimeOffset.UtcNow;
        return until > now ? until - now : null;
    }

    public void RecordFailure(string? ip, string username)
    {
        var now = DateTimeOffset.UtcNow;
        _state.AddOrUpdate(Key(ip, username),
            _ => (1, now, null),
            (_, s) =>
            {
                if (s.LockedUntil is { } until && until > now) return s;      // already locked
                if (now - s.First > Window) return (1, now, null);            // window elapsed, reset
                var count = s.Count + 1;
                return count >= MaxFailures ? (count, s.First, now + Lockout) : (count, s.First, null);
            });
    }

    public void RecordSuccess(string? ip, string username) => _state.TryRemove(Key(ip, username), out _);
}
