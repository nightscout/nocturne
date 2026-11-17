using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Silences an alarm a Nightscout client acknowledged, by the group it went out with. An engine
/// alarm's group is its excursion id (<see cref="EngineAlarmRelay.Map"/>), and every unresolved
/// instance of that excursion is snoozed for the silence time; any other group names an alarm raised
/// through v2 notifications, which only the legacy in-memory snooze holds.
/// </summary>
/// <remarks>
/// Snooze only, never an acknowledgement: the caller may be a relay acting for a subscriber that has
/// no subject to acknowledge or mute for.
/// </remarks>
public interface IAlarmAckHandler
{
    Task<NotificationAckResponse> AckAsync(int level, string group, int silenceTimeMs, CancellationToken ct);
}

internal sealed class AlarmAckHandler(
    INocturneDbContextFactory contextFactory,
    IAlertSnoozeService snoozeService,
    INotificationV1Service legacyNotifications,
    TimeProvider timeProvider,
    ILogger<AlarmAckHandler> logger)
    : IAlarmAckHandler
{
    internal static readonly TimeSpan MaxSilence = TimeSpan.FromHours(24);

    public async Task<NotificationAckResponse> AckAsync(
        int level, string group, int silenceTimeMs, CancellationToken ct)
    {
        if (!Guid.TryParse(group, out var excursionId))
        {
            return await legacyNotifications.AckNotificationAsync(new NotificationAckRequest
            {
                Level = level,
                Group = group,
                Time = silenceTimeMs,
                SendClear = true,
            }, ct);
        }

        List<Guid> instanceIds;
        await using (var db = await contextFactory.CreateAsync(ct))
        {
            instanceIds = await db.AlertInstances
                .AsNoTracking()
                .Where(i => i.AlertExcursionId == excursionId && i.ResolvedAt == null)
                .Select(i => i.Id)
                .ToListAsync(ct);
        }

        var minutes = SilenceMinutes(silenceTimeMs);
        SnoozeResult? refusal = instanceIds.Count == 0 ? SnoozeResult.NotFound : null;
        foreach (var instanceId in instanceIds)
        {
            var outcome = await snoozeService.SnoozeAsync(instanceId, minutes, ct);
            if (outcome.Result != SnoozeResult.Snoozed)
                refusal ??= outcome.Result;
        }

        var timestamp = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (refusal is { } refused)
        {
            logger.LogWarning("Alarm ack for excursion {ExcursionId} was refused: {Refusal}", excursionId, refused);
            return new NotificationAckResponse
            {
                Success = false,
                Message = $"Excursion {excursionId} was not snoozed: {refused}",
                Timestamp = timestamp,
            };
        }

        return new NotificationAckResponse
        {
            Success = true,
            Message = $"Snoozed excursion {excursionId} for {minutes} minutes",
            Timestamp = timestamp,
        };
    }

    /// <summary>A silence time in milliseconds as whole snooze minutes, at least one and at most <see cref="MaxSilence"/>.</summary>
    internal static int SilenceMinutes(int silenceTimeMs) =>
        (int)Math.Clamp(Math.Ceiling(silenceTimeMs / 60_000d), 1, MaxSilence.TotalMinutes);
}
