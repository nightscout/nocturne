using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Notifications.External;

/// <summary>
/// The instance's record of the notices senders outside Nocturne sent (<c>external_notifications</c>):
/// one row per sender, tenant, member and key, kept after the notification is cleaned up.
/// </summary>
public interface IExternalNotificationLedger
{
    /// <summary>
    /// Records the key for each of <paramref name="recipients"/> who has no row for it yet, and returns
    /// every recipient's row. With <paramref name="dailyLimit"/>, a key the sender has not used on the
    /// tenant is refused while the sender used <paramref name="dailyLimit"/> other keys there in the
    /// last day. Senders on one tenant are serialised, so two sends of one key record it once.
    /// </summary>
    Task<ExternalNotificationClaim> ClaimAsync(
        ExternalNotificationSender sender,
        Guid tenantId,
        string key,
        IReadOnlyList<Guid> recipients,
        int? dailyLimit,
        DateTime now,
        CancellationToken ct);

    /// <summary>
    /// Records the notification a claimed row created, and returns whether the row still stands: false
    /// when the sender withdrew it while the notification was being made.
    /// </summary>
    Task<bool> RecordNotificationAsync(Guid entryId, Guid notificationId, CancellationToken ct);

    /// <summary>Removes a claimed row whose notification was not created, so the next send makes it.</summary>
    Task ReleaseAsync(Guid entryId, CancellationToken ct);

    /// <summary>Every row of the key on the tenant, withdrawn ones included.</summary>
    Task<IReadOnlyList<ExternalNotificationEntry>> ListAsync(
        ExternalNotificationSender sender, Guid tenantId, string key, CancellationToken ct);

    /// <summary>Marks a row withdrawn at <paramref name="now"/>; false when it already was.</summary>
    Task<bool> MarkWithdrawnAsync(Guid entryId, DateTime now, CancellationToken ct);
}

/// <summary>One member's row of a key.</summary>
/// <param name="IsNew">Whether this claim recorded it.</param>
public sealed record ExternalNotificationEntry(
    Guid Id,
    Guid SubjectId,
    Guid? NotificationId,
    DateTime? WithdrawnAt,
    bool IsNew);

/// <summary>The rows of a claim, or when the sender may claim a new key again.</summary>
public sealed record ExternalNotificationClaim(
    IReadOnlyList<ExternalNotificationEntry> Entries,
    TimeSpan? RetryAfter)
{
    public bool IsRateLimited => RetryAfter is not null;
}

public sealed class ExternalNotificationLedger(INocturneDbContextFactory contexts) : IExternalNotificationLedger
{
    /// <summary>Serialises the claims of one sender on one tenant.</summary>
    private const int ClaimLockClass = 0x4E58_4E54;

    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    public async Task<ExternalNotificationClaim> ClaimAsync(
        ExternalNotificationSender sender,
        Guid tenantId,
        string key,
        IReadOnlyList<Guid> recipients,
        int? dailyLimit,
        DateTime now,
        CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.ExecuteInTransactionAsync(
            async token =>
            {
                await db.LockForTransactionAsync(ClaimLockClass, $"{sender.KindName}:{sender.Id}:{tenantId}", token);

                var existing = await Rows(db, sender, tenantId)
                    .Where(n => n.Key == key)
                    .Select(n => new ExternalNotificationEntry(n.Id, n.SubjectId, n.NotificationId, n.WithdrawnAt, false))
                    .ToListAsync(token);

                var missing = recipients.Distinct().Where(r => existing.All(e => e.SubjectId != r)).ToList();

                if (dailyLimit is { } limit && existing.Count == 0 && missing.Count > 0)
                {
                    var since = now - Day;
                    var firstSends = await Rows(db, sender, tenantId)
                        .Where(n => n.SysCreatedAt > since)
                        .GroupBy(n => n.Key)
                        .Select(g => g.Min(n => n.SysCreatedAt))
                        .OrderBy(at => at)
                        .ToListAsync(token);

                    if (firstSends.Count >= limit)
                    {
                        var freesAt = firstSends[firstSends.Count - limit] + Day;
                        return new ExternalNotificationClaim([], freesAt - now);
                    }
                }

                var claimed = new List<ExternalNotificationEntry>();
                foreach (var subjectId in missing)
                {
                    var id = Guid.CreateVersion7();
                    var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO external_notifications
                            (id, sender_kind, sender_id, sender_name, tenant_id, subject_id, key, grant_id, sys_created_at)
                        VALUES
                            ({id}, {sender.KindName}, {sender.Id}, {sender.Name}, {tenantId}, {subjectId}, {key}, {sender.GrantId}, {now})
                        ON CONFLICT (sender_kind, sender_id, tenant_id, subject_id, key) DO NOTHING
                        """,
                        token);
                    if (inserted == 1)
                        claimed.Add(new ExternalNotificationEntry(id, subjectId, null, null, true));
                }

                return new ExternalNotificationClaim([.. existing, .. claimed], null);
            },
            verifySucceeded: async (claim, token) =>
            {
                var ids = claim.Entries.Where(e => e.IsNew).Select(e => e.Id).ToList();
                return ids.Count == 0
                    || await db.ExternalNotifications.CountAsync(n => ids.Contains(n.Id), token) == ids.Count;
            },
            ct: ct);
    }

    public async Task<bool> RecordNotificationAsync(Guid entryId, Guid notificationId, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var entry = await db.ExternalNotifications.SingleAsync(n => n.Id == entryId, ct);
        entry.NotificationId = notificationId;
        await db.SaveChangesAsync(ct);
        return entry.WithdrawnAt is null;
    }

    public async Task ReleaseAsync(Guid entryId, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        await db.ExternalNotifications.Where(n => n.Id == entryId).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<ExternalNotificationEntry>> ListAsync(
        ExternalNotificationSender sender, Guid tenantId, string key, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await Rows(db, sender, tenantId)
            .Where(n => n.Key == key)
            .OrderBy(n => n.SysCreatedAt)
            .Select(n => new ExternalNotificationEntry(n.Id, n.SubjectId, n.NotificationId, n.WithdrawnAt, false))
            .ToListAsync(ct);
    }

    public async Task<bool> MarkWithdrawnAsync(Guid entryId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var entry = await db.ExternalNotifications.SingleOrDefaultAsync(n => n.Id == entryId, ct);
        if (entry is null || entry.WithdrawnAt is not null)
            return false;
        entry.WithdrawnAt = now;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static IQueryable<ExternalNotificationEntity> Rows(
        Infrastructure.Data.NocturneDbContext db, ExternalNotificationSender sender, Guid tenantId) =>
        db.ExternalNotifications.AsNoTracking()
            .Where(n => n.SenderKind == sender.KindName && n.SenderId == sender.Id && n.TenantId == tenantId);
}
