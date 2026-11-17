using System.Globalization;
using Nocturne.Core.Clinical.Schedules;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Cuts a window at every profile span and every stored schedule version inside it, reading the
/// versions as <see cref="ActiveProfileSchedulesService"/> does.
/// </summary>
internal sealed class ProfileHistoryService : IProfileHistoryService
{
    private const int SecondsPerDay = 86_400;
    private const int ValueDigits = 4;

    private readonly IActiveProfileResolver _activeProfile;
    private readonly IProfileActivationResolver _activations;
    private readonly IBasalScheduleRepository _basal;
    private readonly ICarbRatioScheduleRepository _carbRatio;
    private readonly ISensitivityScheduleRepository _sensitivity;
    private readonly ITargetRangeScheduleRepository _targetRange;
    private readonly ITherapySettingsRepository _therapySettings;
    private readonly ITherapySettingsResolver _settings;
    private readonly ProfileVersionCache _versions;
    private readonly IApsSnapshotRepository _apsSnapshots;
    private readonly IStateSpanRepository _stateSpans;

    public ProfileHistoryService(
        IActiveProfileResolver activeProfile,
        IProfileActivationResolver activations,
        IBasalScheduleRepository basal,
        ICarbRatioScheduleRepository carbRatio,
        ISensitivityScheduleRepository sensitivity,
        ITargetRangeScheduleRepository targetRange,
        ITherapySettingsRepository therapySettings,
        ITherapySettingsResolver settings,
        ProfileVersionCache versions,
        IApsSnapshotRepository apsSnapshots,
        IStateSpanRepository stateSpans)
    {
        _activeProfile = activeProfile;
        _activations = activations;
        _basal = basal;
        _carbRatio = carbRatio;
        _sensitivity = sensitivity;
        _targetRange = targetRange;
        _therapySettings = therapySettings;
        _settings = settings;
        _versions = versions;
        _apsSnapshots = apsSnapshots;
        _stateSpans = stateSpans;
    }

    /// <summary>A time of day and the value, or the low and high, a schedule holds from it.</summary>
    private readonly record struct Timed(int Seconds, double Value, double? High);

    /// <summary>
    /// A stretch one profile span reads one version of every schedule over.
    /// </summary>
    /// <param name="Pending">The percentage and timeshift still to apply to the entries.</param>
    /// <param name="Reported">The percentage and timeshift the activation names, applied already or not; null when it changes nothing.</param>
    private sealed record Piece(
        long StartMills,
        long EndMills,
        string ProfileName,
        CircadianAdjustment? Pending,
        CircadianAdjustment? Reported,
        string? Timezone,
        List<Timed> Basal,
        List<Timed> Sensitivity,
        List<Timed> CarbRatio,
        List<Timed> Targets);

    public async Task<ProfileHistory> GetHistoryAsync(long fromMs, long toMs, CancellationToken ct = default)
    {
        if (toMs <= fromMs)
            return new ProfileHistory();

        var versions = new List<(ProfileVersion Version, string Key)>();
        foreach (var piece in await PiecesAsync(fromMs, toMs, ct))
        {
            var version = ToVersion(piece);
            var key = KeyOf(version);
            if (versions.Count > 0 && string.Equals(versions[^1].Key, key, StringComparison.Ordinal))
                versions[^1].Version.EndMills = version.EndMills;
            else
                versions.Add((version, key));
        }

        var startedAt = (await _activations.GetRangeAsync(fromMs, toMs, ct))
            .Select(s => s.Active.Activation)
            .DistinctBy(a => a.Id)
            .ToLookup(a => a.Mills);

        var history = new ProfileHistory { Versions = [.. versions.Select(v => v.Version)] };
        foreach (var (before, after) in history.Versions.Zip(history.Versions.Skip(1)))
        {
            history.Changes.Add(new ProfileChange
            {
                Mills = after.StartMills,
                PreviousProfileName = before.ProfileName,
                ProfileName = after.ProfileName,
                PreviousPercentage = before.Percentage,
                Percentage = after.Percentage,
                PreviousTimeshiftHours = before.TimeshiftHours,
                TimeshiftHours = after.TimeshiftHours,
                Activation = startedAt[after.StartMills].FirstOrDefault() is { } activation
                    ? ProfileActivationDto.From(activation)
                    : null,
                Attributes =
                [
                    .. new[]
                    {
                        Diff(ProfileAttribute.Basal, before.Basal, after.Basal),
                        Diff(ProfileAttribute.Sensitivity, before.Sensitivity, after.Sensitivity),
                        Diff(ProfileAttribute.CarbRatio, before.CarbRatio, after.CarbRatio),
                        Diff(ProfileAttribute.TargetRange, before.Targets, after.Targets),
                    }.OfType<ProfileAttributeChange>(),
                ],
            });
        }

        return history;
    }

    public async Task<ProfileLanes> GetLanesAsync(
        long fromMs, long toMs, bool loop, bool temporaryTargets, CancellationToken ct = default)
    {
        if (toMs <= fromMs)
            return new ProfileLanes();

        var pieces = await PiecesAsync(fromMs, toMs, ct);
        var lanes = new ProfileLanes
        {
            Basal = Steps(pieces, p => p.Basal, pct => pct),
            Sensitivity = Steps(pieces, p => p.Sensitivity, Inverse),
            CarbRatio = Steps(pieces, p => p.CarbRatio, Inverse),
            Targets = Steps(pieces, p => p.Targets, _ => 100),
        };

        var from = DateTimeOffset.FromUnixTimeMilliseconds(fromMs).UtcDateTime;
        var to = DateTimeOffset.FromUnixTimeMilliseconds(toMs).UtcDateTime;

        if (loop)
            lanes.Loop = await LoopPointsAsync(lanes.Sensitivity, fromMs, toMs, from, to, ct);
        if (temporaryTargets)
            lanes.TemporaryTargets = await TemporaryTargetsAsync(fromMs, toMs, from, to, ct);

        return lanes;
    }

    private async Task<List<ProfileLoopPoint>> LoopPointsAsync(
        List<ProfileLaneStep> sensitivity, long fromMs, long toMs, DateTime from, DateTime to, CancellationToken ct)
    {
        var points = new List<ProfileLoopPoint>();
        var step = 0;
        foreach (var cycle in await _apsSnapshots.GetLoopCyclesAsync(from, to, ct))
        {
            if (cycle.Mills < fromMs || cycle.Mills >= toMs || cycle.SensitivityRatio is not > 0)
                continue;
            while (step < sensitivity.Count - 1 && sensitivity[step].EndMills <= cycle.Mills)
                step++;
            if (step >= sensitivity.Count || sensitivity[step].StartMills > cycle.Mills)
                continue;

            points.Add(new ProfileLoopPoint
            {
                Mills = cycle.Mills,
                SensitivityRatio = cycle.SensitivityRatio.Value,
                Sensitivity = ScaledRounding.Round(sensitivity[step].Value / cycle.SensitivityRatio.Value, ValueDigits),
            });
        }

        return points;
    }

    private async Task<List<ProfileLaneStep>> TemporaryTargetsAsync(
        long fromMs, long toMs, DateTime from, DateTime to, CancellationToken ct)
    {
        var targets = new List<ProfileLaneStep>();
        foreach (var span in (await _stateSpans.GetByCategory(StateSpanCategory.TemporaryTarget, from, to, ct))
            .Where(s => s.State != nameof(TemporaryTargetState.Cancelled))
            .OrderBy(s => s.StartMills))
        {
            var (top, bottom) = TemporaryTargetUnits.ToMgdl(
                (double?)span.Metadata.TryReadDecimal("targetTop"),
                (double?)span.Metadata.TryReadDecimal("targetBottom"),
                span.Metadata.TryReadString("units"));
            var start = Math.Max(span.StartMills, fromMs);
            var end = Math.Min(span.EndMills ?? toMs, toMs);
            if ((bottom ?? top, top ?? bottom) is (double low, double high) && end > start)
                targets.Add(new ProfileLaneStep { StartMills = start, EndMills = end, Value = low, High = high });
        }

        return targets;
    }

    /// <summary>The percentage to scale a sensitivity or carb ratio by: the inverse of the basal's.</summary>
    private static double Inverse(double percentage) => 10_000 / percentage;

    private async Task<List<Piece>> PiecesAsync(long fromMs, long toMs, CancellationToken ct)
    {
        var pieces = new List<Piece>();
        foreach (var span in await _activeProfile.GetProfileSpansForRangeAsync(fromMs, toMs, ct))
        {
            var end = span.EndMills ?? toMs;
            var reported = Meaningful(span.Adjustment ?? span.AppliedAdjustment);
            var pending = Meaningful(span.Adjustment);

            if (span.Schedules is { } snapshot)
            {
                pieces.Add(new Piece(
                    span.StartMills, end, span.ProfileName, pending, reported,
                    await _settings.GetTimezoneAtAsync(span.StartMills, ct: ct),
                    Values(snapshot.Basal), Values(snapshot.Sensitivity), Values(snapshot.CarbRatio), Ranges(snapshot.Targets)));
                continue;
            }

            // Sequential: see INocturneDbContextFactory.
            var basal = await StoredAsync(_basal, span.ProfileName, toMs, ct);
            var sensitivity = await StoredAsync(_sensitivity, span.ProfileName, toMs, ct);
            var carbRatio = await StoredAsync(_carbRatio, span.ProfileName, toMs, ct);
            var targets = await StoredAsync(_targetRange, span.ProfileName, toMs, ct);
            var settings = await StoredAsync(_therapySettings, span.ProfileName, toMs, ct);

            var cuts = Cuts(basal, span.StartMills, end)
                .Concat(Cuts(sensitivity, span.StartMills, end))
                .Concat(Cuts(carbRatio, span.StartMills, end))
                .Concat(Cuts(targets, span.StartMills, end))
                .Concat(Cuts(settings, span.StartMills, end))
                .Distinct()
                .Order()
                .Append(end);

            var start = span.StartMills;
            foreach (var cut in cuts)
            {
                pieces.Add(new Piece(
                    start, cut, span.ProfileName, pending, reported,
                    await _settings.GetTimezoneAtAsync(start, span.ProfileName, ct),
                    Values(InForce(basal, start)?.Entries),
                    Values(InForce(sensitivity, start)?.Entries),
                    Values(InForce(carbRatio, start)?.Entries),
                    Ranges(InForce(targets, start)?.Entries)));
                start = cut;
            }
        }

        return pieces;
    }

    private static CircadianAdjustment? Meaningful(CircadianAdjustment? adjustment) =>
        adjustment is null or { Percentage: 100, TimeshiftMs: 0 } || adjustment.Percentage <= 0 ? null : adjustment;

    /// <summary>The stored versions a window ending at <paramref name="toMs"/> may read, newest first.</summary>
    private async Task<TRecord[]> StoredAsync<TRecord>(
        IProfileScopedRepository<TRecord> repo, string profileName, long toMs, CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped =>
        [.. (await _versions.GetVersionsAsync(repo, profileName, ct)).Where(v => v.Timestamp.ToMills() < toMs)];

    private static IEnumerable<long> Cuts<TRecord>(TRecord[] newestFirst, long fromMs, long toMs)
        where TRecord : class, IV4Record =>
        ProfileVersionCache.Slice(newestFirst, fromMs, toMs, BeforeFirstVersion.Oldest).Skip(1).Select(s => s.StartMills);

    private static TRecord? InForce<TRecord>(TRecord[] newestFirst, long atMs)
        where TRecord : class, IV4Record =>
        newestFirst.FirstOrDefault(v => v.Timestamp.ToMills() <= atMs) ?? newestFirst.LastOrDefault();

    private static List<Timed> Values(List<ScheduleEntry>? entries) =>
        Ordered(entries?.Select(e => new Timed(ScheduleEntryTime.SecondsOf(e.TimeAsSeconds, e.Time), e.Value, null)));

    private static List<Timed> Ranges(List<TargetRangeEntry>? entries) =>
        Ordered(entries?.Select(e => new Timed(ScheduleEntryTime.SecondsOf(e.TimeAsSeconds, e.Time), e.Low, e.High)));

    /// <summary>One entry per time of day, the last listed winning as <see cref="ScheduleEntryTime.TryEntryAt"/> reads them, in order.</summary>
    private static List<Timed> Ordered(IEnumerable<Timed>? entries) =>
        entries is null ? [] : [.. entries.GroupBy(e => e.Seconds).Select(g => g.Last()).OrderBy(e => e.Seconds)];

    private static ProfileVersion ToVersion(Piece piece)
    {
        var percentage = piece.Pending?.Percentage ?? 100;
        var shiftSeconds = (int)((piece.Pending?.TimeshiftMs ?? 0) / 1000);
        return new ProfileVersion
        {
            StartMills = piece.StartMills,
            EndMills = piece.EndMills,
            ProfileName = piece.ProfileName,
            Percentage = piece.Reported?.Percentage,
            TimeshiftHours = piece.Reported?.TimeshiftMs / (double)TimeSpan.MillisecondsPerHour,
            Timezone = piece.Timezone,
            Basal = AsInForce(piece.Basal, percentage / 100, shiftSeconds),
            Sensitivity = AsInForce(piece.Sensitivity, 100 / percentage, shiftSeconds),
            CarbRatio = AsInForce(piece.CarbRatio, 100 / percentage, shiftSeconds),
            Targets = AsInForce(piece.Targets, 1, shiftSeconds),
        };
    }

    /// <summary>
    /// <paramref name="entries"/> as a day reads them once scaled and shifted: starting at midnight, each
    /// entry at the local time it takes effect.
    /// </summary>
    /// <param name="shiftSeconds">A lookup at a local time reads the entry in force that many seconds later.</param>
    private static List<ProfileHistoryEntry> AsInForce(List<Timed> entries, double factor, int shiftSeconds)
    {
        if (entries.Count == 0)
            return [];

        // A time before the first entry reads the first, which is the value midnight holds.
        var fromMidnight = entries[0].Seconds == 0 ? entries : [entries[0] with { Seconds = 0 }, .. entries];
        var shifted = fromMidnight
            .Select(e => e with { Seconds = ((e.Seconds - shiftSeconds) % SecondsPerDay + SecondsPerDay) % SecondsPerDay })
            .OrderBy(e => e.Seconds)
            .ToList();
        if (shifted[0].Seconds != 0)
            shifted.Insert(0, shifted[^1] with { Seconds = 0 });

        return
        [
            .. shifted.Select(e => new ProfileHistoryEntry
            {
                TimeAsSeconds = e.Seconds,
                Value = Scaled(e.Value, factor),
                High = e.High,
            }),
        ];
    }

    private static double Scaled(double value, double factor) =>
        factor == 1 ? value : ScaledRounding.Round(value * factor, ValueDigits);

    private static string KeyOf(ProfileVersion version)
    {
        static string Entries(List<ProfileHistoryEntry> entries) => string.Join(
            ';',
            entries.Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.TimeAsSeconds}={e.Value:R}-{e.High:R}")));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{version.ProfileName}|{version.Percentage:R}|{version.TimeshiftHours:R}|{version.Timezone}|{Entries(version.Basal)}|{Entries(version.Sensitivity)}|{Entries(version.CarbRatio)}|{Entries(version.Targets)}");
    }

    /// <summary>The rows of a schedule that differs between two versions, or null when it reads the same at every time of day.</summary>
    private static ProfileAttributeChange? Diff(
        ProfileAttribute attribute, List<ProfileHistoryEntry> before, List<ProfileHistoryEntry> after)
    {
        static ProfileHistoryEntry? At(List<ProfileHistoryEntry> entries, int seconds) =>
            entries.LastOrDefault(e => e.TimeAsSeconds <= seconds);

        var rows = before.Concat(after)
            .Select(e => e.TimeAsSeconds)
            .Distinct()
            .Order()
            .Select(seconds =>
            {
                var was = At(before, seconds);
                var now = At(after, seconds);
                return new ProfileChangeRow
                {
                    TimeAsSeconds = seconds,
                    Before = was?.Value,
                    After = now?.Value,
                    BeforeHigh = was?.High,
                    AfterHigh = now?.High,
                    Changed = was?.Value != now?.Value || was?.High != now?.High,
                };
            })
            .ToList();

        return rows.Any(r => r.Changed) ? new ProfileAttributeChange { Attribute = attribute, Rows = rows } : null;
    }

    /// <param name="pieces">The window's pieces, in order.</param>
    /// <param name="entriesOf">Reads the schedule the lane draws from a piece.</param>
    /// <param name="scale">Turns the activation's percentage into the one the lane's values are scaled by.</param>
    private static List<ProfileLaneStep> Steps(
        List<Piece> pieces, Func<Piece, List<Timed>> entriesOf, Func<double, double> scale)
    {
        var steps = new List<ProfileLaneStep>();
        foreach (var piece in pieces)
        {
            var entries = entriesOf(piece);
            if (entries.Count == 0)
                continue;

            var percentage = scale(piece.Pending?.Percentage ?? 100);
            var lows = Segments(piece, entries.Select(e => (e.Seconds, e.Value)), percentage);
            var highs = entries[0].High is null
                ? null
                : Segments(piece, entries.Select(e => (e.Seconds, e.High ?? e.Value)), percentage);

            for (var i = 0; i < lows.Count; i++)
            {
                var value = percentage == 100 ? lows[i].UnitsPerHour : ScaledRounding.Round(lows[i].UnitsPerHour, ValueDigits);
                var high = highs?[i].UnitsPerHour;
                if (steps.Count > 0 && steps[^1] is { } last
                    && last.EndMills == lows[i].StartMills && last.Value == value && last.High == high)
                {
                    last.EndMills = lows[i].EndMills;
                    continue;
                }

                steps.Add(new ProfileLaneStep
                {
                    StartMills = lows[i].StartMills,
                    EndMills = lows[i].EndMills,
                    Value = value,
                    High = high,
                });
            }
        }

        return steps;
    }

    /// <summary>
    /// A schedule over a piece in real time, cut as <see cref="BasalCalculator"/> cuts a basal schedule:
    /// the same entry times give the same cuts whatever the values.
    /// </summary>
    private static List<BasalSegment> Segments(Piece piece, IEnumerable<(int Seconds, double Value)> entries, double percentage) =>
    [
        .. BasalCalculator.BuildSegments(
            new BasalWindow(piece.StartMills, piece.EndMills),
            [
                new ScheduleAssignment(
                    piece.StartMills,
                    piece.EndMills,
                    piece.ProfileName,
                    [.. entries.Select(e => new ScheduleEntry { TimeAsSeconds = e.Seconds, Value = e.Value })],
                    percentage,
                    piece.Pending?.TimeshiftMs ?? 0,
                    piece.Timezone),
            ]),
    ];
}
