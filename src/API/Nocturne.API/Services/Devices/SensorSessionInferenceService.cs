using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Clinical.Devices;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Devices;

/// <inheritdoc />
/// <remarks>
/// <para>
/// Readings and events are read with every deduplicated copy: the attributed copy of a reading, or a
/// recorded event linked to an inferred one, may be hidden behind another source's primary, and
/// missing either would invent a gap or keep an inferred start a recorded one has superseded.
/// </para>
/// <para>
/// Each inferred start is keyed on its device and the first reading after its gap, so a rerun finds
/// the start it wrote. Withdrawal deletes only the inferred copy, so a recorded event deduplication
/// linked to it is the one that remains, and the recorded events around a withdrawn start are put
/// to the trackers again: see <see cref="WithdrawAsync"/>.
/// </para>
/// <para>
/// A session event the user deleted, from any source, dismisses the gap it stood for, so a later
/// pass must not infer it back: see <see cref="IsDismissed"/>.
/// </para>
/// </remarks>
public sealed class SensorSessionInferenceService(
    IPatientDeviceRepository patientDevices,
    ISensorGlucoseRepository sensorGlucose,
    IDeviceEventRepository deviceEvents,
    IDeviceEventReactor reactor,
    TimeProvider timeProvider,
    ILogger<SensorSessionInferenceService> logger) : ISensorSessionInferenceService
{
    /// <summary>
    /// The recent past the background pass reconciles live. Wide enough that readings a connector
    /// catches up on after an outage still reach a pass, and that a start whose gap later fills in is
    /// still withdrawn.
    /// </summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromHours(48);

    /// <summary>
    /// How far back <see cref="InferHistoryAsync"/> looks: far enough to give sensor age a history.
    /// Only readings attributed to the device count, so where registration back-stamps fewer
    /// (<see cref="DeviceReattributionService"/> caps it by record count), the history is shorter.
    /// </summary>
    public static readonly TimeSpan HistoryDepth = TimeSpan.FromDays(180);

    /// <summary>The span of history reconciled at a time, so a long one is never read in one go.</summary>
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(30);

    private static readonly IReadOnlyCollection<DeviceEventType> SessionEventTypes = SensorSessionInference.SessionEventTypes;

    /// <summary>
    /// The longest gap the detector accepts for any catalogued CGM, so a start in the window sees
    /// both ends of its gap whatever the device.
    /// </summary>
    private static readonly TimeSpan Reach =
        TimeSpan.FromMinutes(DeviceCatalog.GetAll().Max(e => e.Cgm?.WarmupMinutes ?? 0))
        + SensorSessionInference.MaxChangeTime;

    /// <inheritdoc />
    public async Task InferAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var cgms = (await patientDevices.GetByDateRangeAsync(from, to, ct))
            .Select(d => (Device: d, Cgm: CgmOf(d)))
            .Where(d => d.Cgm is not null)
            .Select(d => (d.Device, d.Cgm!))
            .ToList();

        await ReconcileAsync(cgms, from, to, WriteOrigin.Live, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// History older than <see cref="LiveWindow"/> is written as a backfill, so a sensor change from
    /// months ago neither moves a tracker nor reaches clients as news; the live window is reconciled
    /// as the background pass reconciles it.
    /// </remarks>
    public async Task InferHistoryAsync(PatientDevice device, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var liveFrom = now - LiveWindow;
        var (from, to) = RegisteredRange(device, now - HistoryDepth, now);

        // An edit can narrow the device's dates or make it no CGM at all, leaving starts inferred
        // where it no longer explains the readings.
        var cgm = CgmOf(device);
        await WithdrawOutsideAsync(device, cgm, cgm is null ? null : (from, to), now, ct);
        if (cgm is null)
            return;

        var historyTo = Earlier(to, liveFrom);
        for (var windowFrom = from; windowFrom < historyTo; windowFrom += HistoryWindow)
            await ReconcileAsync([(device, cgm)], windowFrom, Earlier(windowFrom + HistoryWindow, historyTo), WriteOrigin.Backfill, ct);

        if (to > liveFrom)
            await ReconcileAsync([(device, cgm)], Later(from, liveFrom), to, WriteOrigin.Live, ct);
    }

    /// <inheritdoc />
    public Task ForgetDeviceAsync(PatientDevice device, CancellationToken ct = default)
        => WithdrawOutsideAsync(device, CgmOf(device), kept: null, timeProvider.GetUtcNow().UtcDateTime, ct);

    /// <summary>
    /// [<paramref name="from"/>, <paramref name="to"/>) narrowed to <paramref name="device"/>'s
    /// registered dates. They are local, so a day either side covers any offset, as attribution allows;
    /// a range they do not overlap comes back empty.
    /// </summary>
    private static (DateTime From, DateTime To) RegisteredRange(PatientDevice device, DateTime from, DateTime to)
    {
        if (device.StartDate is { } start)
            from = Later(from, start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-1));
        if (device.EndDate is { } end)
            to = Earlier(to, end.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc).AddDays(1));
        return (from, to);
    }

    /// <summary>
    /// Withdraws <paramref name="device"/>'s inferred starts of the last <see cref="HistoryDepth"/>
    /// that fall outside <paramref name="kept"/>, or all of them when nothing is kept.
    /// </summary>
    private async Task WithdrawOutsideAsync(
        PatientDevice device, CgmProperties? cgm, (DateTime From, DateTime To)? kept, DateTime now, CancellationToken ct)
    {
        var events = await deviceEvents.GetAllCopiesAsync(now - HistoryDepth - Reach, now, SessionEventTypes, ct);
        var outside = events
            .Where(e => e.DataSource == DataSources.SensorSessionInference && e.PatientDeviceId == device.Id
                && (kept is not { } range || AnchorOf(e, cgm) < range.From || AnchorOf(e, cgm) >= range.To))
            .ToList();

        await WithdrawAsync(device, outside, events, ct);
    }

    /// <summary>
    /// Withdraws <paramref name="starts"/>, and re-offers the recorded changes around those a tracker
    /// was following (<see cref="ReplayRecordedAsync"/>). One no run followed, a backfill from history
    /// or a start replaced since, undid nothing, and a replay for it would move a tracker on old data.
    /// Withdrawal is always live: a start written by an earlier live pass may still be followed.
    /// </summary>
    private async Task WithdrawAsync(
        PatientDevice device, IReadOnlyList<DeviceEvent> starts, IReadOnlyList<DeviceEvent> events, CancellationToken ct)
    {
        if (starts.Count == 0)
            return;

        var followed = await reactor.GetFollowedAsync(starts.Select(e => e.Id).ToList(), ct);
        foreach (var start in starts)
        {
            await deviceEvents.WithdrawBySyncIdentifierAsync(
                DataSources.SensorSessionInference, start.SyncIdentifier!, WriteOrigin.Live, ct);
        }

        if (starts.Where(e => followed.Contains(e.Id)).ToList() is { Count: > 0 } undone)
            await ReplayRecordedAsync(device, events, undone, ct);
    }

    /// <summary>
    /// When an inferred start's gap was read: one warm-up before the first reading after it, which
    /// its sync key records. A user who corrected the start's time moved its timestamp, not its gap,
    /// so which pass owns it is judged by this rather than by the timestamp.
    /// </summary>
    private static DateTime AnchorOf(DeviceEvent start, CgmProperties? cgm)
    {
        var key = start.SyncIdentifier;
        if (key?.LastIndexOf(':') is not { } colon || colon < 0 || !long.TryParse(key[(colon + 1)..], out var firstReadingMills))
            return start.Timestamp;

        return DateTimeOffset.FromUnixTimeMilliseconds(firstReadingMills).UtcDateTime
            - TimeSpan.FromMinutes(cgm?.WarmupMinutes ?? 0);
    }

    private static CgmProperties? CgmOf(PatientDevice device)
        => device.DeviceCategory == DeviceCategory.CGM && device.CatalogId is { } catalogId
            ? DeviceCatalog.GetById(catalogId)?.Cgm
            : null;

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Earlier(DateTime a, DateTime b) => a < b ? a : b;

    private async Task ReconcileAsync(
        IReadOnlyList<(PatientDevice Device, CgmProperties Cgm)> cgms, DateTime from, DateTime to, WriteOrigin origin,
        CancellationToken ct)
    {
        if (cgms.Count == 0)
            return;

        var eventsFrom = from - Reach - SensorSessionInference.RecordedEventSlack;
        var eventsTo = to + Reach + SensorSessionInference.RecordedEventSlack;
        var events = await deviceEvents.GetAllCopiesAsync(eventsFrom, eventsTo, SessionEventTypes, ct);
        var deleted = await deviceEvents.GetUserDeletedAsync(eventsFrom, eventsTo, SessionEventTypes, ct);

        foreach (var (device, cgm) in cgms)
        {
            // A device explains only the readings of its registered dates, whatever stayed stamped to it.
            var (deviceFrom, deviceTo) = RegisteredRange(device, from, to);
            if (deviceFrom < deviceTo)
                await ReconcileDeviceAsync(device, cgm, events, deleted, deviceFrom, deviceTo, origin, ct);
        }
    }

    private async Task ReconcileDeviceAsync(
        PatientDevice device, CgmProperties cgm, IReadOnlyList<DeviceEvent> events,
        IReadOnlyList<DeviceEvent> deleted, DateTime from, DateTime to, WriteOrigin origin, CancellationToken ct)
    {
        var readings = await sensorGlucose.GetAttributedReadingTimesAsync(device.Id, from - Reach, to + Reach, ct);

        var inferred = events
            .Where(e => e.DataSource == DataSources.SensorSessionInference && e.PatientDeviceId == device.Id)
            .ToList();
        var recorded = events
            .Where(e => e.DataSource != DataSources.SensorSessionInference && CouldBeFor(device, e))
            .Select(e => e.Timestamp);

        var detected = SensorSessionInference
            .Detect(readings.Select(r => r.Timestamp), cgm, recorded, timeProvider.GetUtcNow().UtcDateTime)
            .Where(s => s.StartedAt >= from && s.StartedAt < to)
            .ToDictionary(s => SyncIdentifierFor(device.Id, s));

        var stale = inferred
            .Where(e => AnchorOf(e, cgm) >= from && AnchorOf(e, cgm) < to && !detected.ContainsKey(e.SyncIdentifier ?? string.Empty))
            .ToList();
        await WithdrawAsync(device, stale, events, ct);

        var written = inferred.Select(e => e.SyncIdentifier).ToHashSet();
        var unwritten = detected.Where(d => !written.Contains(d.Key)).ToList();
        if (unwritten.Count == 0)
            return;

        var dismissals = deleted.Where(e => CouldBeFor(device, e)).ToList();
        var offsets = readings.ToLookup(r => r.Timestamp, r => r.UtcOffset);

        foreach (var (syncIdentifier, start) in unwritten)
        {
            if (dismissals.Any(d => IsDismissed(start, d, cgm)))
                continue;

            try
            {
                await deviceEvents.CreateAsync(new DeviceEvent
                {
                    Timestamp = start.StartedAt,
                    UtcOffset = offsets[start.FirstReadingAfter].First(),
                    EventType = DeviceEventType.SensorStart,
                    PatientDeviceId = device.Id,
                    DeviceId = device.DeviceId,
                    DataSource = DataSources.SensorSessionInference,
                    SyncIdentifier = syncIdentifier,
                }, origin, ct);
            }
            catch (RecreationBlockedException)
            {
                logger.LogDebug(
                    "Inferred sensor start {SyncIdentifier} was deleted by the user while being written", syncIdentifier);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                // Another pass over the same device, the background one or a second edit, wrote it first.
                logger.LogDebug("Inferred sensor start {SyncIdentifier} was written by another pass", syncIdentifier);
            }
        }
    }

    /// <summary>
    /// Puts the recorded session events that could have accounted for the undone starts' gaps to
    /// the trackers again (<see cref="IDeviceEventReactor.OnReplayedAsync"/>). Withdrawing a start
    /// undoes the tracker run it began, and a recorded change dated before it was passed over as
    /// history when it arrived, so it would otherwise never start the run it stands for. Only one
    /// record of each change is replayed (<see cref="SensorSessionInference.DistinctSessions"/>): a
    /// later copy would replace the run the first one started.
    /// </summary>
    private async Task ReplayRecordedAsync(
        PatientDevice device, IReadOnlyList<DeviceEvent> events, IReadOnlyList<DeviceEvent> withdrawn, CancellationToken ct)
    {
        var replayFrom = withdrawn.Min(e => e.Timestamp) - Reach - SensorSessionInference.RecordedEventSlack;
        var replayTo = withdrawn.Max(e => e.Timestamp) + Reach + SensorSessionInference.RecordedEventSlack;

        var replay = SensorSessionInference.DistinctSessions(events
            .Where(e => e.DataSource != DataSources.SensorSessionInference && CouldBeFor(device, e)
                && e.Timestamp >= replayFrom && e.Timestamp <= replayTo));

        if (replay.Count == 0)
            return;

        // A replay that fails is not retried: the next pass finds no withdrawn start to replay for.
        try
        {
            await reactor.OnReplayedAsync(replay, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Replaying {Count} recorded session event(s) for device {PatientDeviceId} failed",
                replay.Count, device.Id);
        }
    }

    /// <summary>
    /// An unattributed event may well be this sensor's, as may one whose device registration was
    /// deleted; one attributed to another device is not.
    /// </summary>
    private static bool CouldBeFor(PatientDevice device, DeviceEvent e)
        => e.PatientDeviceId == device.Id || e.PatientDeviceId is null;

    /// <summary>
    /// Whether the user's deletion of <paramref name="dismissal"/> dismissed the gap behind
    /// <paramref name="start"/>. A deleted recorded event dismisses the gaps it would have accounted
    /// for. A deleted inferred start dismisses any gap overlapping the longest gap it could have been
    /// read from, since backfill landing in a dismissed gap leaves a shorter gap anywhere inside it.
    /// </summary>
    private static bool IsDismissed(InferredSensorStart start, DeviceEvent dismissal, CgmProperties cgm)
    {
        if (dismissal.DataSource != DataSources.SensorSessionInference)
        {
            return dismissal.Timestamp >= start.LastReadingBefore - SensorSessionInference.RecordedEventSlack
                && dismissal.Timestamp <= start.FirstReadingAfter + SensorSessionInference.RecordedEventSlack;
        }

        var earliestGapStart = dismissal.Timestamp - SensorSessionInference.MaxChangeTime;
        var gapEnd = dismissal.Timestamp + TimeSpan.FromMinutes(cgm.WarmupMinutes);
        return start.LastReadingBefore <= gapEnd && start.FirstReadingAfter >= earliestGapStart;
    }

    private static string SyncIdentifierFor(Guid patientDeviceId, InferredSensorStart start)
        => $"{patientDeviceId:N}:{start.FirstReadingAfter.ToMills()}";
}
