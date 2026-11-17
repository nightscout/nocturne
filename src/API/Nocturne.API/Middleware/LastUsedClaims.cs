using System.Collections.Concurrent;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Middleware;

/// <summary>
/// Which request in this process stamps a row as last used. A dashboard load fans out into parallel
/// requests that all read the same stale stamp, so <see cref="LastUsedStamp.IsDue"/> alone sends each
/// of them to the row; the first to claim it is the only one that does.
/// </summary>
/// <remarks>
/// A claim older than <see cref="LastUsedStamp.Interval"/> is the same as none, so a sweep once per
/// interval drops them and the map holds at most the rows claimed in the last two intervals. A failed
/// stamp is not retried before the claim lapses. Across processes the repository's conditional UPDATE
/// does what this does within one.
/// </remarks>
public sealed class LastUsedClaims(TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, DateTime> _claimedAt = new();
    private long _nextSweepTicks;

    /// <summary>
    /// Whether the caller is the one to stamp row <paramref name="id"/>, which its request read as last
    /// stamped at <paramref name="lastUsedAt"/>.
    /// </summary>
    public bool TryClaim(Guid id, DateTime? lastUsedAt)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (!LastUsedStamp.IsDue(lastUsedAt, now))
        {
            return false;
        }

        Sweep(now);

        while (true)
        {
            if (_claimedAt.TryAdd(id, now))
            {
                return true;
            }

            if (!_claimedAt.TryGetValue(id, out var claimedAt))
            {
                continue;
            }

            if (!LastUsedStamp.IsDue(claimedAt, now))
            {
                return false;
            }

            if (_claimedAt.TryUpdate(id, now, claimedAt))
            {
                return true;
            }
        }
    }

    private void Sweep(DateTime now)
    {
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.Ticks < due || Interlocked.CompareExchange(ref _nextSweepTicks, (now + LastUsedStamp.Interval).Ticks, due) != due)
        {
            return;
        }

        var claims = (ICollection<KeyValuePair<Guid, DateTime>>)_claimedAt;
        foreach (var claim in _claimedAt)
        {
            if (LastUsedStamp.IsDue(claim.Value, now))
            {
                claims.Remove(claim);
            }
        }
    }
}
