using Nocturne.API.Services.Alerts;

namespace Nocturne.API.Services.Notifications;

/// <summary>
/// Nightscout's alarm table for alarms that name no engine excursion (v2 notifications, the
/// <c>default</c> group): until when each tenant's alarm of a level and group is snoozed. One per
/// instance, because <see cref="NotificationV1Service"/> lives for one request and a snooze must
/// outlast it. A snooze lasts at most <see cref="AlarmAckHandler.MaxSilence"/>, a tenant holds at
/// most <see cref="MaxAlarmsPerTenant"/>, and a group is cut to <see cref="MaxGroupLength"/>
/// characters, so a caller that only holds the right to snooze cannot grow the table without bound.
/// </summary>
public sealed class LegacyAlarmSnoozeStore(TimeProvider timeProvider)
{
    internal const int MaxAlarmsPerTenant = 100;
    internal const int MaxGroupLength = 128;

    private readonly Dictionary<(Guid TenantId, int Level, string Group), DateTimeOffset> _snoozedUntil = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Snoozes the alarm for <paramref name="silenceTimeMs"/>, unless an earlier snooze of it has not
    /// ended. Returns false in that case, and the earlier snooze stands. A tenant at its cap gives up
    /// the snooze that ends first.
    /// </summary>
    public bool TrySnooze(Guid tenantId, int level, string group, int silenceTimeMs)
    {
        var now = timeProvider.GetUtcNow();
        var key = (tenantId, level, group.Length > MaxGroupLength ? group[..MaxGroupLength] : group);
        var silence = TimeSpan.FromMilliseconds(Math.Clamp(silenceTimeMs, 0, AlarmAckHandler.MaxSilence.TotalMilliseconds));

        lock (_gate)
        {
            if (_snoozedUntil.TryGetValue(key, out var until) && now < until)
                return false;

            if (!_snoozedUntil.ContainsKey(key))
                MakeRoom(tenantId, now);

            _snoozedUntil[key] = now + silence;
            return true;
        }
    }

    private void MakeRoom(Guid tenantId, DateTimeOffset now)
    {
        var held = _snoozedUntil.Where(e => e.Key.TenantId == tenantId).ToList();
        foreach (var (key, until) in held)
        {
            if (until <= now)
                _snoozedUntil.Remove(key);
        }

        held.RemoveAll(e => e.Value <= now);
        if (held.Count < MaxAlarmsPerTenant)
            return;

        _snoozedUntil.Remove(held.MinBy(e => e.Value).Key);
    }
}
