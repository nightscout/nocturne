using Nocturne.Core.Models;

namespace Nocturne.API.Services.Notifications;

/// <summary>
/// The admin notifications of the legacy v1 notification API, per tenant.
/// One per instance, because <see cref="NotificationV1Service"/> lives for one request and the
/// notifications must outlast it. A non-persistent admin notification lasts
/// <see cref="AdminNotificationLifetime"/>; expired entries go on access. A tenant holds at most
/// <see cref="MaxAdminNotificationsPerTenant"/> admin notifications and gives up the oldest at the
/// cap. A title and message are cut to <see cref="MaxTextLength"/>, so a caller cannot grow the
/// store without bound.
/// </summary>
public sealed class LegacyNotificationStore(TimeProvider timeProvider)
{
    internal const int MaxAdminNotificationsPerTenant = 500;
    internal const int MaxTextLength = 1024;
    internal static readonly TimeSpan AdminNotificationLifetime = TimeSpan.FromHours(9);

    private readonly Dictionary<(Guid TenantId, string Message), AdminNotification> _adminNotifications = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Records the notification, or counts one more occurrence of the notification that has the same
    /// message. Returns the occurrence count.
    /// </summary>
    public int AddAdminNotification(Guid tenantId, AdminNotification notification)
    {
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var title = Cut(notification.Title, MaxTextLength);
        var message = Cut(notification.Message, MaxTextLength);
        lock (_gate)
        {
            RemoveExpiredAdminNotifications(tenantId, nowMs);
            var key = (tenantId, message);
            if (_adminNotifications.TryGetValue(key, out var existing))
            {
                existing.Count += 1;
                existing.LastRecorded = nowMs;
                return existing.Count;
            }

            if (_adminNotifications.Count(e => e.Key.TenantId == tenantId) >= MaxAdminNotificationsPerTenant)
                _adminNotifications.Remove(_adminNotifications.Where(e => e.Key.TenantId == tenantId).MinBy(e => e.Value.LastRecorded).Key);

            _adminNotifications[key] = new AdminNotification
            {
                Title = title,
                Message = message,
                Count = 1,
                LastRecorded = notification.LastRecorded == 0 ? nowMs : notification.LastRecorded,
                Persistent = notification.Persistent,
            };
            return 1;
        }
    }

    public List<AdminNotification> ListAdminNotifications(Guid tenantId)
    {
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_gate)
        {
            RemoveExpiredAdminNotifications(tenantId, nowMs);
            return _adminNotifications
                .Where(e => e.Key.TenantId == tenantId)
                .Select(e => new AdminNotification
                {
                    Title = e.Value.Title,
                    Message = e.Value.Message,
                    Count = e.Value.Count,
                    LastRecorded = e.Value.LastRecorded,
                    Persistent = e.Value.Persistent,
                })
                .ToList();
        }
    }

    public void ClearAdminNotifications(Guid tenantId)
    {
        lock (_gate)
        {
            foreach (var key in _adminNotifications.Keys.Where(k => k.TenantId == tenantId).ToList())
                _adminNotifications.Remove(key);
        }
    }

    private static string Cut(string value, int length) =>
        value.Length > length ? value[..length] : value;

    private void RemoveExpiredAdminNotifications(Guid tenantId, long nowMs)
    {
        var expired = _adminNotifications
            .Where(e => e.Key.TenantId == tenantId && !e.Value.Persistent && nowMs - e.Value.LastRecorded >= AdminNotificationLifetime.TotalMilliseconds)
            .Select(e => e.Key)
            .ToList();
        foreach (var key in expired)
            _adminNotifications.Remove(key);
    }
}
