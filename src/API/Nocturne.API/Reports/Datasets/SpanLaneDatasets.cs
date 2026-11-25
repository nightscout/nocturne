using Microsoft.EntityFrameworkCore;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// Each stage of each sleep session, a lane per stage: a hypnogram. Read through the request's own repository, so
/// RLS decides what a reader sees: <c>sleep_stages</c> is hidden from shares and shown to a clamped member exactly
/// while its session is (<see cref="ShareDataCategories.HiddenParentRecency"/>).
/// </summary>
internal sealed class SleepStagesDataset : LaneDataset
{
    /// <summary>The most sessions one read takes the stages of.</summary>
    private const int MaxSessions = 400;

    public override string Name => SleepStagesParameters.Name;

    public override string Label => "Sleep stages";

    public override string Description => "Each sleep stage, from its start to its end: awake, REM, light and deep.";

    public override Type ParametersType => typeof(SleepStagesParameters);

    public override string RequiredScope => Scope.SleepRead;

    protected override string LaneWords => "Awake, REM, Light, Deep, or Asleep where the source does not tell stages apart: the lane the stage is drawn in.";

    protected override string LabelWords => "The stage.";

    protected override string Noun => "stage";

    protected override string ReadName => "sleep_stages";

    public override int PointBudget => 4000;

    public override TimeSpan DefaultRange => TimeSpan.FromDays(3);

    public override TimeSpan MaxRange => TimeSpan.FromDays(31);

    private static readonly string[] Order = ["Awake", "REM", "Light", "Deep", "Asleep", "In bed", "Restless"];

    protected override int LaneRank(string lane) => Array.IndexOf(Order, lane) is var i and >= 0 ? i : Order.Length;

    protected override async Task<LaneRead> ReadAsync(ReportDatasetRequest request, long from, long to, int limit, CancellationToken cancellationToken)
    {
        var sessions = (await request.Services.GetRequiredService<ISleepSessionRepository>().GetSessionsAsync(
            Utc(from), Utc(to), limit: MaxSessions + 1, descending: false, includeStages: true, cancellationToken: cancellationToken)).ToList();
        return new([.. sessions.Take(MaxSessions).SelectMany(s => s.Stages ?? [])
            .Where(st => LaneOf(st.Stage) is not null && Mills(st.StartTime) < to && Mills(st.EndTime) > from)
            .OrderBy(st => st.StartTime)
            .Take(limit)
            .Select(st => new Span(LaneOf(st.Stage)!, LaneOf(st.Stage)!, Mills(st.StartTime), Mills(st.EndTime), ColorOf(st.Stage)))],
            Cut: sessions.Count > MaxSessions);
    }

    internal static string? LaneOf(SleepStageType stage) => stage switch
    {
        SleepStageType.Awake or SleepStageType.AwakeInBed or SleepStageType.OutOfBed => "Awake",
        SleepStageType.Rem => "REM",
        SleepStageType.Light => "Light",
        SleepStageType.Deep => "Deep",
        SleepStageType.Asleep => "Asleep",
        SleepStageType.InBed => "In bed",
        SleepStageType.Restless => "Restless",
        _ => null,
    };

    /// <summary>The colors the printed sleep report gives the stages.</summary>
    internal static ChartColor ColorOf(SleepStageType stage) => stage switch
    {
        SleepStageType.Awake or SleepStageType.AwakeInBed or SleepStageType.OutOfBed or SleepStageType.Restless => ChartColor.Chart5,
        SleepStageType.Light => ChartColor.Profile,
        SleepStageType.Rem => ChartColor.Override,
        SleepStageType.Deep => ChartColor.Chart3,
        _ => ChartColor.ActivitySleep,
    };
}

/// <summary>
/// Each excursion of an alert (the time its condition held), a lane per alert, named by when it was acknowledged.
/// Read in the request's own context: <c>alert_excursions</c> is shared under <c>alerts.read</c> and clamped by
/// overlap (<see cref="ShareDataCategories.GovernedSpanEndColumns"/>); report runs are an account's only, as alert
/// history is.
/// </summary>
internal sealed class AlertExcursionsDataset : LaneDataset
{
    public override string Name => AlertExcursionsParameters.Name;

    public override string Label => "Alert periods";

    public override string Description => "Each time an alert's condition held, from its start to its end.";

    public override Type ParametersType => typeof(AlertExcursionsParameters);

    public override string RequiredScope => Scope.AlertsRead;

    protected override string LaneWords => "The alert's name: the lane its periods are drawn in.";

    protected override string LabelWords => "The alert, and how long after its start it was acknowledged, or that it was not.";

    protected override string Noun => "period";

    protected override string ReadName => "alert_excursions";

    public override TimeSpan DefaultRange => TimeSpan.FromDays(7);

    public override TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    protected override async Task<LaneRead> ReadAsync(ReportDatasetRequest request, long from, long to, int limit, CancellationToken cancellationToken)
    {
        var (start, end) = (Utc(from), Utc(to));
        await using var db = await request.Services.GetRequiredService<INocturneDbContextFactory>().CreateAsync(cancellationToken);
        var rows = await db.AlertExcursions.AsNoTracking()
            .OfCondition()
            .Where(e => e.StartedAt < end && (e.EndedAt == null || e.EndedAt > start))
            .OrderBy(e => e.StartedAt).Take(limit)
            .Select(e => new { e.StartedAt, e.EndedAt, e.AcknowledgedAt, Rule = e.AlertRule!.Name, Severity = (AlertRuleSeverity?)e.AlertRule.Severity })
            .ToListAsync(cancellationToken);
        return new([.. rows.Select(e =>
        {
            var name = string.IsNullOrWhiteSpace(e.Rule) ? "Alert" : e.Rule.Trim();
            var label = e.AcknowledgedAt is { } ack
                ? $"{name}, acknowledged after {ReportFacts.Number(Math.Max(0, (ack - e.StartedAt).TotalMinutes), "0")} min"
                : $"{name}, not acknowledged";
            return new Span(name, label, Mills(e.StartedAt), e.EndedAt is { } ended ? Mills(ended) : to, SeverityColor(e.Severity));
        })]);
    }

    internal static ChartColor SeverityColor(AlertRuleSeverity? stored) =>
        (stored is { } severity ? AlertRuleMapper.Readable(severity) : (AlertRuleSeverity?)null) switch
        {
            AlertRuleSeverity.Critical => ChartColor.SystemEventAlarm,
            AlertRuleSeverity.Warning => ChartColor.SystemEventWarning,
            _ => ChartColor.SystemEventInfo,
        };
}

/// <summary>
/// Each time an alert was raised (its instances), as a tick in a lane per alert, with how often it was snoozed. Test
/// alerts are left out. Read in the request's own context: <c>alert_instances</c> is shared under <c>alerts.read</c>
/// and clamped by overlap; report runs are an account's only, as alert history is.
/// </summary>
internal sealed class AlertInstancesDataset : LaneDataset
{
    public override string Name => AlertInstancesParameters.Name;

    public override string Label => "Alerts raised";

    public override string Description => "Each time an alert was raised, and how many times it was snoozed.";

    public override Type ParametersType => typeof(AlertInstancesParameters);

    public override string RequiredScope => Scope.AlertsRead;

    protected override string LaneWords => "The alert's name: the lane it is drawn in.";

    protected override string LabelWords => "The alert, and how many times it was snoozed.";

    protected override string Noun => "alert";

    protected override string ReadName => "alert_instances";

    protected override bool Instants => true;

    public override TimeSpan DefaultRange => TimeSpan.FromDays(7);

    public override TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    protected override async Task<LaneRead> ReadAsync(ReportDatasetRequest request, long from, long to, int limit, CancellationToken cancellationToken)
    {
        var (start, end) = (Utc(from), Utc(to));
        await using var db = await request.Services.GetRequiredService<INocturneDbContextFactory>().CreateAsync(cancellationToken);
        var rows = await db.AlertInstances.AsNoTracking()
            .OfCondition()
            .Where(i => i.TriggeredAt >= start && i.TriggeredAt < end)
            .OrderBy(i => i.TriggeredAt).Take(limit)
            .Select(i => new
            {
                i.TriggeredAt, i.SnoozeCount, Rule = i.AlertExcursion!.AlertRule!.Name,
                Severity = (AlertRuleSeverity?)i.AlertExcursion.AlertRule.Severity,
            })
            .ToListAsync(cancellationToken);
        return new([.. rows.Select(i =>
        {
            var name = string.IsNullOrWhiteSpace(i.Rule) ? "Alert" : i.Rule.Trim();
            var label = i.SnoozeCount switch
            {
                0 => name,
                1 => $"{name}, snoozed 1 time",
                var n => $"{name}, snoozed {n} times",
            };
            var at = Mills(i.TriggeredAt);
            return new Span(name, label, at, at, AlertExcursionsDataset.SeverityColor(i.Severity));
        })]);
    }
}

/// <summary>
/// Each do-not-disturb period, from its start to when it ended or was cleared, a lane for the alerts it held back.
/// Read in the request's own context: <c>dnd_windows</c> is hidden from shares and clamped for a member by overlap,
/// ending at the earlier of its expiry and its clearing (<see cref="ShareDataCategories.HiddenSpanEarlyEndColumns"/>); its
/// endpoints serve accounts holding <c>alerts.read</c>, as report runs are.
/// </summary>
internal sealed class DndWindowsDataset : LaneDataset
{
    public override string Name => DndWindowsParameters.Name;

    public override string Label => "Do not disturb";

    public override string Description => "Each do-not-disturb period, from its start to its end, and the alerts it held back.";

    public override Type ParametersType => typeof(DndWindowsParameters);

    public override string RequiredScope => Scope.AlertsRead;

    protected override string LaneWords => "Low alerts, High alerts or All alerts: the alerts the period held back.";

    protected override string LabelWords => "The alerts the period held back, and whether it was stopped early.";

    protected override string Noun => "period";

    protected override string ReadName => "dnd_windows";

    public override TimeSpan DefaultRange => TimeSpan.FromDays(7);

    public override TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    protected override async Task<LaneRead> ReadAsync(ReportDatasetRequest request, long from, long to, int limit, CancellationToken cancellationToken)
    {
        var rows = await request.Services.GetRequiredService<IDndWindowRepository>()
            .GetOverlappingAsync(Utc(from), Utc(to), limit, cancellationToken);
        return new([.. rows.Select(w =>
        {
            var cleared = w.ClearedAt is { } c && (w.EndsAt is null || c < w.EndsAt);
            var ended = cleared ? w.ClearedAt : w.EndsAt;
            var lane = ScopeWords(w.Scope);
            return new Span(lane, cleared ? $"{lane}, stopped early" : lane, Mills(w.StartedAt), ended is { } e ? Mills(e) : to, ChartColor.PumpModeSleep);
        })]);
    }

    internal static string ScopeWords(DndScope scope) => scope switch
    {
        DndScope.Lows => "Low alerts",
        DndScope.Highs => "High alerts",
        _ => "All alerts",
    };
}

/// <summary>
/// Each compression low Nocturne found at night: a drop and recovery that looks like pressure on the sensor, not a
/// real low. Read in the request's own context: <c>compression_low_suggestions</c> is hidden from shares and clamped
/// for a member by its end (<see cref="ShareDataCategories.UnixMillisRecencyTables"/>); its endpoints are glucose
/// reads.
/// </summary>
internal sealed class CompressionLowsDataset : LaneDataset
{
    public override string Name => CompressionLowsParameters.Name;

    public override string Label => "Compression lows";

    public override string Description => "Each period at night that looks like a false low from pressure on the sensor.";

    public override Type ParametersType => typeof(CompressionLowsParameters);

    public override string RequiredScope => Scope.GlucoseRead;

    protected override string LaneWords => "Not reviewed, Accepted or Dismissed: what you did with it.";

    protected override string LabelWords => "Its lowest glucose.";

    protected override string Noun => "compression low";

    protected override string ReadName => "compression_lows";

    public override TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public override TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    protected override async Task<LaneRead> ReadAsync(ReportDatasetRequest request, long from, long to, int limit, CancellationToken cancellationToken)
    {
        await using var db = await request.Services.GetRequiredService<INocturneDbContextFactory>().CreateAsync(cancellationToken);
        var rows = await db.CompressionLowSuggestions.AsNoTracking()
            .Where(s => s.StartMills < to && s.EndMills > from)
            .OrderBy(s => s.StartMills).Take(limit)
            .Select(s => new { s.StartMills, s.EndMills, s.Status, s.LowestGlucose })
            .ToListAsync(cancellationToken);
        return new([.. rows.Select(s =>
        {
            var lane = s.Status switch
            {
                nameof(CompressionLowStatus.Accepted) => "Accepted",
                nameof(CompressionLowStatus.Dismissed) => "Dismissed",
                _ => "Not reviewed",
            };
            var label = s.LowestGlucose is { } mgdl
                ? $"Lowest {GlucoseSummary.Format(mgdl, request.UseMmol)} {ReportFacts.GlucoseUnit(request.UseMmol)}"
                : "Compression low";
            return new Span(lane, label, s.StartMills, s.EndMills, lane == "Dismissed" ? ChartColor.MutedForeground : ChartColor.GlucoseLow);
        })]);
    }
}

/// <summary>
/// Each low and high episode the glucose statistics count (<see cref="GlucoseEpisodes"/>), over the consensus limits
/// every report counts them by, from the run's shared readings (<see cref="ReportRunCache.GlucoseAsync"/>).
/// </summary>
internal sealed class GlucoseEpisodesDataset : LaneDataset
{
    public override string Name => GlucoseEpisodesParameters.Name;

    public override string Label => "Lows and highs";

    public override string Description => "Each low and high episode, 15 minutes or more past a limit, from its start to its end.";

    public override Type ParametersType => typeof(GlucoseEpisodesParameters);

    public override string RequiredScope => Scope.GlucoseRead;

    protected override string LaneWords => "Lows or Highs: the lane the episode is drawn in.";

    protected override string LabelWords => "Low, Very low, High or Very high, and its lowest or highest glucose.";

    protected override string Noun => "episode";

    protected override string? ReadName => null;

    public override TimeSpan DefaultRange => TimeSpan.FromDays(7);

    public override TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    protected override int LaneRank(string lane) => lane == "Lows" ? 0 : 1;

    protected override async Task<LaneRead> ReadAsync(ReportDatasetRequest request, long from, long to, int limit, CancellationToken cancellationToken)
    {
        var readings = await request.Cache.GlucoseAsync(request, cancellationToken);
        return new([.. Spans(GlucoseEpisodes.Find(readings, new GlycemicThresholds()), request.UseMmol).Take(limit)]);
    }

    internal static IEnumerable<Span> Spans(IEnumerable<GlucoseEpisode> episodes, bool useMmol) =>
        episodes.Select(e =>
        {
            var (kind, color) = (e.BelowRange, e.Severe) switch
            {
                (true, true) => ("Very low", ChartColor.GlucoseVeryLow),
                (true, false) => ("Low", ChartColor.GlucoseLow),
                (false, true) => ("Very high", ChartColor.GlucoseVeryHigh),
                (false, false) => ("High", ChartColor.GlucoseHigh),
            };
            var extreme = $"{(e.BelowRange ? "lowest" : "highest")} {GlucoseSummary.Format(e.Extreme.Mgdl, useMmol)} {ReportFacts.GlucoseUnit(useMmol)}";
            return new Span(e.BelowRange ? "Lows" : "Highs", $"{kind}, {extreme}", Mills(e.StartAt), Mills(e.EndAt), color);
        });
}
