using System.Collections.Concurrent;
using System.Globalization;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Tells the tenant's owner, by an in-app notification, that a device sent data dated more than the
/// engine's clock tolerance ahead of now (docs/alerts/engine-semantics.md §5.4). Alerts do not use
/// such data until its time comes near: a reading is skipped by the alert context
/// (<see cref="AlertReadings"/>), a pump-mode span or a device-status override is not sent to the
/// engine (<see cref="SensorContext.PumpModesRead"/>, <see cref="SensorContext.OverridesRead"/>), and a
/// rule that reads another such instant is held.
/// </summary>
/// <remarks>
/// <para>
/// One notification stands per tenant and fact (the context field the instant came from). It is
/// raised when an instant is beyond the tolerance, and archived only once the fact is observed at
/// or before now, so a clock a little more than the tolerance fast raises it once and keeps it.
/// The reading fact is observed by the reading selection; the others by the context each
/// evaluation pass reads. A fact that leaves the context (a span that ended, a rule that no longer
/// reads it) is not observed again, so its notification also stores the instant the fact was
/// ahead to, moved forward each time a pass reports the fact further ahead, and is archived once
/// now has passed the latest one. A reconcile that does not observe a fact (one for the reading
/// alone) archives it only then, so a device clock a little fast keeps one notification.
/// </para>
/// <para>
/// A notice never stands between a reading and its evaluation: callers report through
/// <see cref="ClockAnomalyNoticeIsolation"/>, which logs a failure and goes on.
/// </para>
/// <para>
/// Instants the host writes itself (<see cref="ClockAnomalyNotice.HostFacts"/>) are ahead only when the host's clock
/// went back, which the owner cannot act on: they are logged once per tenant and fact, and not
/// notified.
/// </para>
/// <para>
/// The notifications use their own source, so neither they nor the device-clock deviation notices
/// supersede the other. Creation is atomic across passes and replicas
/// (<see cref="IInAppNotificationService.CreateNotificationUnlessActiveAsync"/>).
/// <see cref="ClockAnomalyNoticeLog"/> keeps, per tenant, the facts last seen standing for up to
/// <see cref="ClockAnomalyNoticeLog.Freshness"/>, so a pass with nothing ahead and nothing standing
/// reads no notification.
/// </para>
/// </remarks>
public interface IClockAnomalyNotice
{
    /// <summary>The engine's anomalies from one evaluation pass, with the context the pass read.</summary>
    Task ReportAsync(SensorContext context, IReadOnlyCollection<ClockAnomaly> anomalies, CancellationToken ct);

    /// <summary>The reading selection of an alert pass at <paramref name="now"/>.</summary>
    Task ReportReadingAsync(AlertReadingSelection selection, DateTime now, CancellationToken ct);

    /// <summary>
    /// Whether the reading selection must be reported at <paramref name="now"/> though no new reading
    /// arrived: a notification may stand for the tenant, and is archived once time passes the reading.
    /// </summary>
    bool NeedsReadingReport(DateTime now);
}

/// <summary>
/// Per tenant, the clock-anomaly facts last seen standing, and the host facts already logged.
/// </summary>
public sealed class ClockAnomalyNoticeLog(TimeProvider timeProvider)
{
    /// <summary>How long a reading of the standing notifications is trusted, which bounds how late another replica's raise is seen.</summary>
    internal static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, (IReadOnlyDictionary<string, DateTime?> Facts, long At)> _standing = new();
    private readonly ConcurrentDictionary<(Guid, string), byte> _logged = new();

    /// <summary>The standing facts, each with the instant it was ahead to when known.</summary>
    internal bool TryGetStanding(Guid tenantId, out IReadOnlyDictionary<string, DateTime?> facts)
    {
        if (_standing.TryGetValue(tenantId, out var seen) && timeProvider.GetElapsedTime(seen.At) < Freshness)
        {
            facts = seen.Facts;
            return true;
        }
        facts = new Dictionary<string, DateTime?>();
        return false;
    }

    internal void Record(Guid tenantId, IReadOnlyDictionary<string, DateTime?> facts) =>
        _standing[tenantId] = (facts, timeProvider.GetTimestamp());

    internal bool FirstLog(Guid tenantId, string fact) => _logged.TryAdd((tenantId, fact), 0);
}

internal sealed class ClockAnomalyNotice(
    IInAppNotificationService notifications,
    ITenantOwnerResolver ownerResolver,
    ITenantAccessor tenantAccessor,
    ClockAnomalyNoticeLog log,
    TimeProvider timeProvider,
    ILogger<ClockAnomalyNotice> logger)
    : IClockAnomalyNotice
{
    internal const string NotificationType = "alerts.deviceClockAhead";
    internal const string NotificationSource = "alert-clock";
    internal const string ReadingFact = "last_reading_at";
    internal const string AheadUntilKey = "aheadUntil";
    private const string PumpModesFact = "active_pump_modes";
    private const string OverrideFact = "active_override";
    private const string TitleKey = "device_clock_ahead";

    /// <summary>Instants the host writes from its own clock (docs/alerts/engine-semantics.md §5.4).</summary>
    internal static readonly IReadOnlySet<string> HostFacts =
        new HashSet<string> { "enabled_at", "sustained", "hysteresis_started_at", "active_alerts" };

    /// <summary>
    /// The reading facts are observed by the reading selection, which sees every stored reading;
    /// the context holds only the selected one.
    /// </summary>
    private static readonly IReadOnlySet<string> ReadingFacts =
        new HashSet<string> { ReadingFact, "latest_timestamp" };

    public Task ReportAsync(SensorContext context, IReadOnlyCollection<ClockAnomaly> anomalies, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var raise = anomalies.ToDictionary(a => a.Fact, a => a.Ahead);
        var observed = Observed(context).Where(o => o.Value is not null && !ReadingFacts.Contains(o.Key))
            .ToDictionary(o => o.Key, o => o.Value!.Value);
        foreach (var (fact, read) in SpanReads(context))
        {
            var newest = read.NewestStart;
            if (newest is { } ahead && ahead > AlertReadings.Bound(read.At) && ahead > now
                && (!raise.TryGetValue(fact, out var reported) || reported < ahead - now))
                raise[fact] = ahead - now;
            observed[fact] = newest ?? now;
        }
        return ReconcileAsync(raise, observed, now, ct);
    }

    /// <summary>The span reads a live pass made, each under the fact the engine reads it as.</summary>
    private static IEnumerable<(string Fact, SpansRead Read)> SpanReads(SensorContext context)
    {
        if (context.PumpModesRead is { } pumpModes)
            yield return (PumpModesFact, pumpModes);
        if (context.OverridesRead is { } overrides)
            yield return (OverrideFact, overrides);
    }

    public Task ReportReadingAsync(AlertReadingSelection selection, DateTime now, CancellationToken ct)
    {
        var newest = selection.Newest?.Timestamp;
        var raise = new Dictionary<string, TimeSpan>();
        if (newest is { } ahead && ahead > AlertReadings.Bound(now))
            raise[ReadingFact] = ahead - now;
        var observed = new Dictionary<string, DateTime>();
        if (newest is { } at)
            observed[ReadingFact] = at;
        return ReconcileAsync(raise, observed, now, ct);
    }

    public bool NeedsReadingReport(DateTime now) =>
        !log.TryGetStanding(tenantAccessor.TenantId, out var standing)
        || standing.ContainsKey(ReadingFact)
        || standing.Any(f => Passed(f.Key, f.Value, now));

    /// <summary>
    /// A fact other than the reading's whose stored instant is no longer ahead: whatever the
    /// context now holds for it, that instant cannot be.
    /// </summary>
    private static bool Passed(string fact, DateTime? aheadUntil, DateTime now) =>
        !ReadingFacts.Contains(fact) && aheadUntil is { } until && until <= now;

    /// <summary>
    /// The fact a stored notification stands for: one raised before pump modes became a set is keyed
    /// by the retired anchor name, and stands for the same span as the set's.
    /// </summary>
    private static string FactOf(string sourceId) => sourceId == "active_pump_state" ? PumpModesFact : sourceId;

    private static DateTime? AheadUntil(InAppNotificationDto notification) =>
        notification.Metadata?.TryGetValue(AheadUntilKey, out var value) == true
        && DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var until)
            ? until
            : null;

    private async Task ReconcileAsync(
        Dictionary<string, TimeSpan> raise,
        Dictionary<string, DateTime> observed,
        DateTime now,
        CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        foreach (var fact in raise.Keys.Where(HostFacts.Contains).ToList())
        {
            if (log.FirstLog(tenantId, fact))
                logger.LogWarning(
                    "An alert evaluation read {Fact} {AheadMinutes:0.#} minutes ahead of the clock for tenant {TenantId}; the host clock went back",
                    fact, raise[fact].TotalMinutes, tenantId);
            raise.Remove(fact);
        }

        bool Clears(string fact, DateTime? aheadUntil) =>
            !raise.ContainsKey(fact)
            && (observed.TryGetValue(fact, out var at) ? at <= now : Passed(fact, aheadUntil, now));

        bool Covers(string fact, DateTime? stored) =>
            ReadingFacts.Contains(fact) || stored >= now + raise[fact];

        if (log.TryGetStanding(tenantId, out var known)
            && raise.All(r => known.TryGetValue(r.Key, out var stored) && Covers(r.Key, stored))
            && !known.Any(f => Clears(f.Key, f.Value)))
            return;

        var owner = await ownerResolver.GetOwnerSubjectIdAsync(tenantId, ct);
        if (owner is null)
        {
            log.Record(tenantId, new Dictionary<string, DateTime?>());
            return;
        }

        var active = (await notifications.GetActiveNotificationsAsync(owner, ct))
            .Where(n => n.Type == NotificationType && n.SourceId is not null)
            .ToList();
        var standing = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        var sourceOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in active)
        {
            var fact = FactOf(n.SourceId!);
            standing[fact] = AheadUntil(n);
            sourceOf[fact] = n.SourceId!;
        }

        foreach (var (fact, ahead) in raise)
        {
            var aheadUntil = now + ahead;
            var metadata = new Dictionary<string, object>
            {
                ["fact"] = fact,
                [AheadUntilKey] = aheadUntil.ToString("O", CultureInfo.InvariantCulture),
            };
            if (standing.TryGetValue(fact, out var stored))
            {
                if (!Covers(fact, stored))
                {
                    await notifications.ReplaceMetadataBySourceAsync(owner, NotificationType, sourceOf.GetValueOrDefault(fact, fact), metadata, ct);
                    standing[fact] = aheadUntil;
                }
                continue;
            }
            if (await notifications.CreateNotificationUnlessActiveAsync(
                    owner, NotificationType, TitleKey, fact,
                    category: NotificationCategory.Alert,
                    source: NotificationSource,
                    subtitle: TitleKey + "_subtitle",
                    metadata: metadata,
                    cancellationToken: ct))
                logger.LogWarning(
                    "Data for tenant {TenantId} is dated {AheadMinutes:0.#} minutes ahead of the clock ({Fact}); alerts do not use it until its time is near",
                    tenantId, ahead.TotalMinutes, fact);
            standing[fact] = aheadUntil;
        }

        foreach (var (fact, _) in standing.Where(f => Clears(f.Key, f.Value)).ToList())
        {
            await notifications.ArchiveBySourceAsync(owner, NotificationType, sourceOf.GetValueOrDefault(fact, fact), NotificationArchiveReason.ConditionMet, ct);
            standing.Remove(fact);
        }

        log.Record(tenantId, standing);
    }

    /// <summary>The newest instant of each device fact the context carries.</summary>
    private static Dictionary<string, DateTime?> Observed(SensorContext context) => new()
    {
        ["last_aps_cycle_at"] = context.LastApsCycleAt,
        ["last_aps_enacted_at"] = context.LastApsEnactedAt,
        ["last_site_change_at"] = context.LastSiteChangeAt,
        ["last_sensor_start_at"] = context.LastSensorStartAt,
        ["last_carb_at"] = context.LastCarbAt,
        ["last_bolus_at"] = context.LastBolusAt,
        ["aps_determination"] = context.ApsDetermination?.At,
        ["active_pump_suspension"] = context.ActivePumpSuspension?.StartedAt,
        [OverrideFact] = context.ActiveOverride?.StartedAt,
        ["active_do_not_disturb"] = context.ActiveDoNotDisturb?.StartedAt,
        ["active_state_spans"] = context.ActiveStateSpans.Values.Select(s => (DateTime?)s.StartedAt).Max(),
        ["active_trackers"] = context.ActiveTrackers.Values.Where(t => !t.IsScheduled)
            .Select(t => (DateTime?)t.ReferenceAt).Max(),
    };
}

/// <summary>
/// Reports to <see cref="IClockAnomalyNotice"/> so that its failure (the store down, a lock
/// timeout) is logged and never stops the alert evaluation around it.
/// </summary>
internal static class ClockAnomalyNoticeIsolation
{
    public static async Task ReportReadingIsolatedAsync(
        this IClockAnomalyNotice notice, AlertReadingSelection selection, DateTime now, ILogger logger, CancellationToken ct)
    {
        try
        {
            await notice.ReportReadingAsync(selection, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The clock-anomaly notice could not be updated for a reading selection");
        }
    }

    public static async Task ReportIsolatedAsync(
        this IClockAnomalyNotice notice, SensorContext context, IReadOnlyCollection<ClockAnomaly> anomalies,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            await notice.ReportAsync(context, anomalies, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The clock-anomaly notice could not be updated for an evaluation pass");
        }
    }
}
