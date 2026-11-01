using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The AlarmHub events that carry the alert engine's alarms. They are not the legacy <c>alarm</c>,
/// <c>urgent_alarm</c> and <c>clear_alarm</c>, so the bridge relays them to the Socket.IO
/// <c>/alarm</c> namespace alone: the web app shows engine alerts as in-app notifications and keeps
/// its alarm overlay for alarms raised through v2 notifications.
/// </summary>
public static class EngineAlarmEvents
{
    public const string Alarm = "engine_alarm";
    public const string UrgentAlarm = "engine_urgent_alarm";
    public const string ClearAlarm = "engine_clear_alarm";
}

/// <summary>An engine alarm as AlarmHub sends it: one of the <see cref="EngineAlarmEvents"/> and its Nightscout notify.</summary>
public sealed record EngineAlarm(string EventName, NotificationBase Notify);

/// <summary>
/// The alert engine on the Nightscout <c>/alarm</c> namespace: raises a dispatched excursion as an
/// alarm, sends the all-clear, and names the alarm a new subscriber should hear.
/// </summary>
/// <remarks>
/// Every <c>clear_alarm</c> consumer (AAPS, the Nocturne web store) dismisses every alarm it holds, and
/// the engine raises an excursion once rather than on every reading. So the all-clear goes out only
/// when nothing is still alarming: an open, unacknowledged, unsnoozed, unsuppressed excursion of a
/// warning or critical rule. A member's mute is theirs alone and does not quiet the tenant's alarm.
/// </remarks>
public interface IEngineAlarmRelay
{
    /// <summary>Raises a dispatched excursion to alarm subscribers. An info rule raises nothing.</summary>
    Task RaiseAsync(AlertPayload payload, CancellationToken ct);

    /// <summary>
    /// Sends the all-clear after a change to <paramref name="changedExcursionIds"/>, when one of them
    /// is a real excursion of a warning or critical rule and nothing is still alarming.
    /// </summary>
    Task ClearIfQuietAsync(IReadOnlyCollection<Guid> changedExcursionIds, CancellationToken ct);

    /// <summary>The alarm of the most severe, most recent excursion still alarming, or null.</summary>
    Task<EngineAlarm?> HighestAlarmingAsync(CancellationToken ct);
}

internal sealed class EngineAlarmRelay(
    INocturneDbContextFactory contextFactory,
    ITenantAccessor tenantAccessor,
    ITenantGlucoseUnits glucoseUnits,
    ICanonicalGlucoseService canonicalGlucose,
    ISignalRBroadcastService broadcastService,
    TimeProvider timeProvider,
    ILogger<EngineAlarmRelay> logger)
    : IEngineAlarmRelay
{
    public async Task RaiseAsync(AlertPayload payload, CancellationToken ct)
    {
        if (!RaisesAlarm(payload.Severity)) return;

        try
        {
            var useMmol = payload.GlucoseValue is not null && await glucoseUnits.UsesMmolAsync(ct);
            var alarm = Map(payload, useMmol, timeProvider.GetUtcNow())!;
            await broadcastService.BroadcastEngineAlarmAsync(alarm.EventName, alarm.Notify);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to raise the alarm for excursion {ExcursionId}", payload.ExcursionId);
        }
    }

    public async Task ClearIfQuietAsync(IReadOnlyCollection<Guid> changedExcursionIds, CancellationToken ct)
    {
        if (changedExcursionIds.Count == 0) return;

        try
        {
            await using var db = await contextFactory.CreateAsync(ct);

            var raisedAnAlarm = await db.AlertInstances
                .AsNoTracking()
                .AnyAsync(i => changedExcursionIds.Contains(i.AlertExcursionId)
                               && !i.IsTest
                               && i.AlertExcursion!.AlertRule!.Severity != AlertRuleSeverity.Info, ct);
            if (!raisedAnAlarm) return;

            if (await Alarming(db, timeProvider.GetUtcNow().UtcDateTime).AnyAsync(ct)) return;

            await broadcastService.BroadcastEngineAlarmAsync(EngineAlarmEvents.ClearAlarm, new NotificationBase
            {
                Clear = true,
                Title = "All Clear",
                Timestamp = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to decide the all-clear after excursions {ExcursionIds} changed",
                string.Join(",", changedExcursionIds));
        }
    }

    public async Task<EngineAlarm?> HighestAlarmingAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        AlarmingExcursion? top;
        await using (var db = await contextFactory.CreateAsync(ct))
        {
            top = await Alarming(db, now.UtcDateTime)
                .OrderByDescending(i => i.AlertExcursion!.AlertRule!.Severity == AlertRuleSeverity.Critical)
                .ThenByDescending(i => i.TriggeredAt)
                .Select(i => new AlarmingExcursion(
                    i.Id,
                    i.AlertExcursionId,
                    i.AlertExcursion!.AlertRule!.Name,
                    i.AlertExcursion.AlertRule.ConditionType,
                    i.AlertExcursion.AlertRule.Severity))
                .FirstOrDefaultAsync(ct);
        }
        if (top is null) return null;

        var latest = await canonicalGlucose.GetLatestAsync(ct);
        var fresh = latest is not null && now.UtcDateTime - latest.Timestamp <= SmartSnoozeTrendGate.MaxLatestAge
            ? latest
            : null;

        var payload = new AlertPayload
        {
            AlertType = top.ConditionType,
            RuleName = top.RuleName,
            GlucoseValue = fresh is null ? null : (decimal)fresh.Mgdl,
            Trend = null,
            TrendRate = (decimal?)fresh?.TrendRate,
            ReadingTimestamp = fresh?.Timestamp ?? now.UtcDateTime,
            ExcursionId = top.ExcursionId,
            InstanceId = top.InstanceId,
            TenantId = tenantAccessor.TenantId,
            SubjectName = string.Empty,
            ActiveExcursionCount = 0,
            Severity = top.Severity,
        };
        return Map(payload, fresh is not null && await glucoseUnits.UsesMmolAsync(ct), now);
    }

    private sealed record AlarmingExcursion(
        Guid InstanceId, Guid ExcursionId, string RuleName, AlertConditionType ConditionType, AlertRuleSeverity Severity);

    private static bool RaisesAlarm(AlertRuleSeverity severity) =>
        severity is AlertRuleSeverity.Critical or AlertRuleSeverity.Warning;

    /// <summary>
    /// The unresolved instances that keep their excursion sounding. An excursion in hysteresis counts:
    /// it is still open and returns to active without a new dispatch, so an all-clear sent then would
    /// leave it silent if the condition returns. A lapsed snooze counts too, before the engine resumes it.
    /// </summary>
    private static IQueryable<AlertInstanceEntity> Alarming(NocturneDbContext db, DateTime now) =>
        db.AlertInstances
            .AsNoTracking()
            .Where(i => i.ResolvedAt == null
                        && !i.IsTest
                        && i.SuppressionReason == null
                        && (i.SnoozedUntil == null || i.SnoozedUntil <= now)
                        && i.AlertExcursion!.EndedAt == null
                        && i.AlertExcursion.AcknowledgedAt == null
                        && i.AlertExcursion.AlertRule!.IsEnabled
                        && i.AlertExcursion.AlertRule.Severity != AlertRuleSeverity.Info);

    /// <summary>
    /// The Nightscout notify for a dispatched excursion, or null for an info rule, which raises no
    /// alarm. The group is the excursion id, which is how an <c>/alarm</c> ack names what to snooze
    /// (<see cref="AlarmAckHandler"/>).
    /// </summary>
    internal static EngineAlarm? Map(AlertPayload payload, bool useMmol, DateTimeOffset now)
    {
        if (!RaisesAlarm(payload.Severity)) return null;

        var critical = payload.Severity == AlertRuleSeverity.Critical;
        var condition = AlertConditionTypeNames.ToWireString(payload.AlertType);

        // NIGHTSCOUT-COMPAT: Nightscout clients show an alarm's title and message as sent, so the
        // backend renders them, in the tenant's units.
        var message = payload.GlucoseValue is { } mgdl
            ? useMmol
                ? $"BG Now: {GlucoseUnits.Scale((double)mgdl, useMmol: true).ToString("0.0", CultureInfo.InvariantCulture)} mmol/L"
                : $"BG Now: {GlucoseUnits.Scale((double)mgdl, useMmol: false).ToString("0", CultureInfo.InvariantCulture)} mg/dL"
            : payload.RuleName;

        return new EngineAlarm(
            critical ? EngineAlarmEvents.UrgentAlarm : EngineAlarmEvents.Alarm,
            new NotificationBase
            {
                Level = critical ? 2 : 1,
                Title = payload.RuleName,
                Message = message,
                Group = payload.ExcursionId.ToString(),
                Plugin = condition,
                EventName = condition,
                Timestamp = now.ToUnixTimeMilliseconds(),
            });
    }
}
