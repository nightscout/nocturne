using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Reports;

/// <summary>Whether each signal of the workspace has data in the last <paramref name="Hours"/> hours.</summary>
public sealed record CustomReportSignalAvailability(
    [property: Required] int Hours,
    [property: Required] IReadOnlyList<CustomReportSignalPresence> Signals);

/// <summary>One signal's availability.</summary>
/// <param name="State">
/// <c>data</c>: one of its sources has a record in the range the caller can read. <c>none</c>: none has.
/// <c>noAccess</c>: the caller holds the scopes of none of its cells, so nothing was read.
/// </param>
public sealed record CustomReportSignalPresence([property: Required] string Signal, [property: Required] string State);

/// <summary>
/// The records a signal draws, each with a query for whether any is in a range. A query reads one indexed row at
/// most, under the caller's own context, so row level security decides what it sees: a history-clamped reader finds
/// nothing older than its window.
/// </summary>
internal static class ReportDataSources
{
    public const string SensorGlucose = "sensorGlucose";
    public const string BgChecks = "bgChecks";
    public const string Boluses = "boluses";
    public const string Carbs = "carbs";
    public const string BasalInjections = "basalInjections";
    public const string TempBasals = "tempBasals";
    public const string ProfileActivations = "profileActivations";
    public const string ApsSnapshots = "apsSnapshots";
    public const string PumpSnapshots = "pumpSnapshots";
    public const string UploaderSnapshots = "uploaderSnapshots";
    public const string HeartRates = "heartRates";
    public const string StepCounts = "stepCounts";
    public const string SleepSessions = "sleepSessions";
    public const string PumpModeSpans = "pumpModeSpans";
    public const string OverrideSpans = "overrideSpans";
    public const string ActivitySpans = "activitySpans";
    public const string TemporaryTargetSpans = "temporaryTargetSpans";
    public const string PumpConnectivitySpans = "pumpConnectivitySpans";
    public const string DataExclusionSpans = "dataExclusionSpans";
    public const string MotionSpans = "motionSpans";
    public const string SensorEvents = "sensorEvents";
    public const string SiteEvents = "siteEvents";
    public const string PumpEvents = "pumpEvents";
    public const string SystemEvents = "systemEvents";
    public const string Trackers = "trackers";
    public const string SleepStages = "sleepStages";
    public const string SleepBiometrics = "sleepBiometrics";
    public const string LabHbA1c = "labHbA1c";
    public const string BodyWeights = "bodyWeights";
    public const string BodyFat = "bodyFat";
    public const string LeanMass = "leanMass";
    public const string AlertExcursions = "alertExcursions";
    public const string AlertInstances = "alertInstances";
    public const string DndWindows = "dndWindows";
    public const string SensorTrendRates = "sensorTrendRates";
    public const string SensorNoise = "sensorNoise";
    public const string MeterGlucose = "meterGlucose";
    public const string CompressionLows = "compressionLows";

    private static readonly string[] PumpModeCategories = [nameof(StateSpanCategory.PumpMode)];
    private static readonly string[] OverrideCategories = [nameof(StateSpanCategory.Override)];
    private static readonly string[] ActivityCategories = [.. StateSpanCategories.DrawnActivities.Select(c => c.ToString())];

    private static string[] EventTypes(ReportDeviceEventGroup group) =>
        [.. Enum.GetValues<DeviceEventType>().Where(t => Datasets.DeviceEventsDataset.GroupOf(t) == group).Select(t => t.ToString())];

    private static readonly string[] SensorEventTypes = EventTypes(ReportDeviceEventGroup.Sensor);
    private static readonly string[] SiteEventTypes = EventTypes(ReportDeviceEventGroup.Site);
    private static readonly string[] PumpEventTypes = EventTypes(ReportDeviceEventGroup.Pump);

    /// <summary>Whether the source has a record in [from, to): a span by overlap, anything else by its time.</summary>
    public static readonly IReadOnlyDictionary<string, Func<NocturneDbContext, DateTime, DateTime, CancellationToken, Task<bool>>> All =
        new Dictionary<string, Func<NocturneDbContext, DateTime, DateTime, CancellationToken, Task<bool>>>
        {
            [SensorGlucose] = (db, from, to, ct) => db.SensorGlucose.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to, ct),
            [BgChecks] = (db, from, to, ct) => db.BGChecks.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to, ct),
            [Boluses] = (db, from, to, ct) => db.Boluses.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to, ct),
            [Carbs] = (db, from, to, ct) => db.CarbIntakes.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to, ct),
            [BasalInjections] = (db, from, to, ct) => db.BasalInjections.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to, ct),
            [TempBasals] = (db, from, to, ct) =>
                db.TempBasals.AnyAsync(e => e.Timestamp < to && (e.EndTimestamp == null || e.EndTimestamp >= from), ct),
            [ProfileActivations] = (db, from, to, ct) =>
                db.ProfileActivations.AnyAsync(e => e.Timestamp < to && (e.EndTimestamp == null || e.EndTimestamp >= from), ct),
            [ApsSnapshots] = (db, from, to, ct) => db.ApsSnapshots.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to, ct),
            [PumpSnapshots] = (db, from, to, ct) => db.PumpSnapshots.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to, ct),
            [UploaderSnapshots] = (db, from, to, ct) => db.UploaderSnapshots.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to, ct),
            [HeartRates] = (db, from, to, ct) => db.HeartRates.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to, ct),
            [StepCounts] = (db, from, to, ct) => db.StepCounts.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to, ct),
            [SleepSessions] = (db, from, to, ct) => db.SleepSessions.AnyAsync(e => e.StartTime < to && e.EndTime >= from, ct),
            [PumpModeSpans] = (db, from, to, ct) => SpansAsync(db, PumpModeCategories, from, to, ct),
            [OverrideSpans] = (db, from, to, ct) => SpansAsync(db, OverrideCategories, from, to, ct),
            [ActivitySpans] = (db, from, to, ct) => SpansAsync(db, ActivityCategories, from, to, ct),
            [TemporaryTargetSpans] = (db, from, to, ct) => SpansAsync(db, [nameof(StateSpanCategory.TemporaryTarget)], from, to, ct),
            [PumpConnectivitySpans] = (db, from, to, ct) => SpansAsync(db, [nameof(StateSpanCategory.PumpConnectivity)], from, to, ct),
            [DataExclusionSpans] = (db, from, to, ct) => SpansAsync(db, [nameof(StateSpanCategory.DataExclusion)], from, to, ct),
            [MotionSpans] = (db, from, to, ct) => SpansAsync(db, [nameof(StateSpanCategory.Motion)], from, to, ct),
            [SensorEvents] = (db, from, to, ct) => DeviceEventsAsync(db, SensorEventTypes, from, to, ct),
            [SiteEvents] = (db, from, to, ct) => DeviceEventsAsync(db, SiteEventTypes, from, to, ct),
            [PumpEvents] = (db, from, to, ct) => DeviceEventsAsync(db, PumpEventTypes, from, to, ct),
            [SystemEvents] = (db, from, to, ct) =>
            {
                var (start, end) = (Mills(from), Mills(to));
                return db.SystemEvents.AnyAsync(e => e.Mills >= start && e.Mills < end, ct);
            },
            [Trackers] = (db, from, to, ct) =>
                db.TrackerInstances.AnyAsync(e => e.StartedAt < to && (e.CompletedAt == null || e.CompletedAt >= from), ct),
            [SleepStages] = (db, from, to, ct) => db.SleepStages.AnyAsync(e => e.StartTime < to && e.EndTime >= from, ct),
            [SleepBiometrics] = (db, from, to, ct) => db.SleepBiometricSamples.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to, ct),
            [LabHbA1c] = (db, from, to, ct) =>
            {
                var day = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
                return db.LabHbA1cResults.AnyAsync(e => e.MeasuredAt >= day && e.MeasuredAt < to, ct);
            },
            [BodyWeights] = (db, from, to, ct) =>
            {
                var (start, end) = (Mills(from), Mills(to));
                return db.BodyWeights.AnyAsync(e => e.Mills >= start && e.Mills < end, ct);
            },
            [BodyFat] = (db, from, to, ct) =>
            {
                var (start, end) = (Mills(from), Mills(to));
                return db.BodyWeights.AnyAsync(e => e.Mills >= start && e.Mills < end && e.BodyFatPercent != null, ct);
            },
            [LeanMass] = (db, from, to, ct) =>
            {
                var (start, end) = (Mills(from), Mills(to));
                return db.BodyWeights.AnyAsync(e => e.Mills >= start && e.Mills < end && e.LeanMassKg != null, ct);
            },
            [AlertExcursions] = (db, from, to, ct) =>
                db.AlertExcursions.AnyAsync(e => e.StartedAt < to && (e.EndedAt == null || e.EndedAt >= from), ct),
            [AlertInstances] = (db, from, to, ct) =>
                db.AlertInstances.AnyAsync(e => !e.IsTest && e.TriggeredAt >= from && e.TriggeredAt < to, ct),
            [DndWindows] = (db, from, to, ct) =>
                db.DndWindows.AnyAsync(e => e.StartedAt < to && (e.EndsAt == null || e.EndsAt >= from) && (e.ClearedAt == null || e.ClearedAt >= from), ct),
            [SensorTrendRates] = (db, from, to, ct) =>
                db.SensorGlucose.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to && e.TrendRate != null, ct),
            [SensorNoise] = (db, from, to, ct) =>
                db.SensorGlucose.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to && e.Noise != null, ct),
            [MeterGlucose] = (db, from, to, ct) => db.MeterGlucose.AnyAsync(e => e.Timestamp >= from && e.Timestamp < to, ct),
            [CompressionLows] = (db, from, to, ct) =>
            {
                var (start, end) = (Mills(from), Mills(to));
                return db.CompressionLowSuggestions.AnyAsync(e => e.StartMills < end && e.EndMills >= start, ct);
            },
        };

    private static long Mills(DateTime at) => new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static Task<bool> DeviceEventsAsync(NocturneDbContext db, string[] types, DateTime from, DateTime to, CancellationToken ct) =>
        db.DeviceEvents.AnyAsync(e => types.Contains(e.EventType) && e.Timestamp >= from && e.Timestamp < to, ct);

    private static Task<bool> SpansAsync(NocturneDbContext db, string[] categories, DateTime from, DateTime to, CancellationToken ct) =>
        db.StateSpans.AnyAsync(e => categories.Contains(e.Category) && e.StartTimestamp < to && (e.EndTimestamp == null || e.EndTimestamp >= from), ct);
}

/// <summary>Whether each workspace signal has data in a range, for the request's caller.</summary>
public interface ICustomReportSignalAvailability
{
    /// <param name="granted">The caller's read scopes: a signal none of whose cells they satisfy is not read.</param>
    Task<CustomReportSignalAvailability> ForRangeAsync(int hours, IReadOnlySet<string> granted, CancellationToken cancellationToken);
}

/// <summary>
/// Answers availability per source, not per signal: each source that a signal the caller may read draws is asked
/// once, in one context, whatever number of signals share it.
/// </summary>
internal sealed class ReportSignalAvailability(
    IReportDatasetRegistry registry,
    INocturneDbContextFactory contexts,
    TimeProvider time) : ICustomReportSignalAvailability
{
    public const string Data = "data";
    public const string None = "none";
    public const string NoAccess = "noAccess";

    public async Task<CustomReportSignalAvailability> ForRangeAsync(int hours, IReadOnlySet<string> granted, CancellationToken cancellationToken)
    {
        hours = Math.Clamp(hours, 1, ReportSpec.MaxRangeHours);
        var permitted = ReportWorkspace.Build(registry, granted).Signals.Where(s => s.Permitted).Select(s => s.Id).ToHashSet();
        var to = time.GetUtcNow().UtcDateTime;
        var from = to.AddHours(-hours);

        var needed = ReportWorkspace.SignalTable.Where(s => permitted.Contains(s.Id)).SelectMany(s => s.Sources).Distinct().ToList();
        var found = new Dictionary<string, bool>();
        if (needed.Count > 0)
        {
            await using var db = await contexts.CreateAsync(cancellationToken);
            foreach (var source in needed)
                found[source] = await ReportDataSources.All[source](db, from, to, cancellationToken);
        }

        return new CustomReportSignalAvailability(hours, [.. ReportWorkspace.SignalTable.Select(s => new CustomReportSignalPresence(s.Id,
            !permitted.Contains(s.Id) ? NoAccess : s.Sources.Any(found.GetValueOrDefault) ? Data : None))]);
    }
}
