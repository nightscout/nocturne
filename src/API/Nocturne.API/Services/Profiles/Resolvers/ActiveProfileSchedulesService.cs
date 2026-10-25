using System.Globalization;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Reads each span's schedules as <see cref="ActiveSchedule"/> does at a point: the activation's
/// snapshot when it carries one, else the stored version in force, cut at every version boundary.
/// </summary>
/// <remarks>
/// Unlike <see cref="ProfileVersionCache.ActiveAt{TRecord}"/>, a time before the first stored version
/// reads the oldest, and a version stored at or after the window's end is never read: a report on a
/// past window must not show an edit made since. Versions are told apart by their entries' times and
/// values, so a profile re-uploaded unchanged is not a change.
/// </remarks>
internal sealed class ActiveProfileSchedulesService : IActiveProfileSchedulesService
{
    private readonly IActiveProfileResolver _activeProfile;
    private readonly IBasalScheduleRepository _basal;
    private readonly ICarbRatioScheduleRepository _carbRatio;
    private readonly ISensitivityScheduleRepository _sensitivity;
    private readonly ITargetRangeScheduleRepository _targetRange;
    private readonly ProfileVersionCache _versions;

    public ActiveProfileSchedulesService(
        IActiveProfileResolver activeProfile,
        IBasalScheduleRepository basal,
        ICarbRatioScheduleRepository carbRatio,
        ISensitivityScheduleRepository sensitivity,
        ITargetRangeScheduleRepository targetRange,
        ProfileVersionCache versions)
    {
        _activeProfile = activeProfile;
        _basal = basal;
        _carbRatio = carbRatio;
        _sensitivity = sensitivity;
        _targetRange = targetRange;
        _versions = versions;
    }

    public async Task<IReadOnlyList<ActiveProfileSchedules>> GetForRangeAsync(
        long fromMs, long toMs, CancellationToken ct = default)
    {
        if (toMs <= fromMs)
            return [];

        var spans = await _activeProfile.GetProfileSpansForRangeAsync(fromMs, toMs, ct);
        var result = new List<ActiveProfileSchedules>();
        foreach (var group in spans.GroupBy(s => s.ProfileName, StringComparer.Ordinal))
        {
            var profileSpans = group.ToList();
            var activeMs = profileSpans.Sum(s => Length(s, toMs));
            var scaling = profileSpans
                .GroupBy(ScalingOf)
                .MaxBy(g => g.Sum(s => Length(s, toMs)))!.Key;

            var basal = await InForceAsync(profileSpans, toMs, _basal, r => r.Entries, s => s.Basal, ValueKey, ct);
            var carbRatio = await InForceAsync(profileSpans, toMs, _carbRatio, r => r.Entries, s => s.CarbRatio, ValueKey, ct);
            var sensitivity = await InForceAsync(profileSpans, toMs, _sensitivity, r => r.Entries, s => s.Sensitivity, ValueKey, ct);
            var targets = await InForceAsync(profileSpans, toMs, _targetRange, r => r.Entries, s => s.Targets, RangeKey, ct);

            result.Add(new ActiveProfileSchedules
            {
                ProfileName = group.Key,
                ActiveMs = activeMs,
                Share = (double)activeMs / (toMs - fromMs),
                Percentage = scaling.Adjustment?.Percentage,
                TimeshiftHours = scaling.Adjustment?.TimeshiftMs / (double)TimeSpan.MillisecondsPerHour,
                AdjustmentApplied = scaling.Applied,
                Basal = basal.Entries,
                BasalChanges = basal.Changes,
                CarbRatio = carbRatio.Entries,
                CarbRatioChanges = carbRatio.Changes,
                Sensitivity = sensitivity.Entries,
                SensitivityChanges = sensitivity.Changes,
                Targets = targets.Entries,
                TargetRangeChanges = targets.Changes,
            });
        }

        return
        [
            .. result
                .OrderByDescending(p => p.ActiveMs)
                .ThenBy(p => p.ProfileName, StringComparer.Ordinal),
        ];
    }

    private static long Length(ProfileSpan span, long toMs) => (span.EndMills ?? toMs) - span.StartMills;

    /// <param name="Adjustment">Null when the span's schedules are read as they are.</param>
    /// <param name="Applied">Whether <paramref name="Adjustment"/> is already in the span's schedules.</param>
    private sealed record Scaling(CircadianAdjustment? Adjustment, bool Applied);

    private static Scaling ScalingOf(ProfileSpan span)
    {
        var (adjustment, applied) = span.Adjustment is { } pending
            ? (pending, false)
            : (span.AppliedAdjustment, span.AppliedAdjustment is not null);
        return adjustment is null or { Percentage: 100, TimeshiftMs: 0 }
            ? new Scaling(null, false)
            : new Scaling(adjustment, applied);
    }

    private sealed record Slice<TEntry>(long StartMills, long EndMills, List<TEntry> Entries, string Key);

    /// <param name="profileSpans">One profile's spans, in order.</param>
    private async Task<(List<TEntry> Entries, ScheduleChangeInfo Changes)> InForceAsync<TRecord, TEntry>(
        List<ProfileSpan> profileSpans,
        long toMs,
        IProfileScopedRepository<TRecord> repo,
        Func<TRecord, List<TEntry>> storedEntries,
        Func<ProfileActivationSnapshot, List<TEntry>?> snapshotEntries,
        Func<TEntry, (int Seconds, string Values)> entryKey,
        CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped
    {
        var versions = (await _versions.GetVersionsAsync(repo, profileSpans[0].ProfileName, ct))
            .Where(v => v.Timestamp.ToMills() < toMs)
            .ToArray();
        string Key(List<TEntry> entries) =>
            string.Join(';', entries.Select(entryKey).OrderBy(k => k.Seconds).Select(k => $"{k.Seconds}={k.Values}"));

        var slices = new List<Slice<TEntry>>();
        foreach (var span in profileSpans)
        {
            var end = span.EndMills ?? toMs;
            if (span.Schedules is { } snapshot)
            {
                if (snapshotEntries(snapshot) is { } fromSnapshot)
                    slices.Add(new Slice<TEntry>(span.StartMills, end, fromSnapshot, Key(fromSnapshot)));
                continue;
            }

            foreach (var slice in ProfileVersionCache.Slice(versions, span.StartMills, end, BeforeFirstVersion.Oldest))
            {
                var stored = storedEntries(slice.Version);
                slices.Add(new Slice<TEntry>(slice.StartMills, slice.EndMills, stored, Key(stored)));
            }
        }

        var longest = slices
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .MaxBy(g => g.Sum(s => s.EndMills - s.StartMills));

        var changeTimes = slices
            .Zip(slices.Skip(1))
            .Where(pair => !string.Equals(pair.First.Key, pair.Second.Key, StringComparison.Ordinal))
            .Select(pair => pair.Second.StartMills)
            .ToList();

        return (
            longest?.First().Entries ?? [],
            new ScheduleChangeInfo
            {
                ChangedDuringPeriod = changeTimes.Count > 0,
                ChangeCount = changeTimes.Count,
                LastChangedAt = changeTimes.Count > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(changeTimes[^1]).UtcDateTime
                    : null,
            });
    }

    private static (int, string) ValueKey(ScheduleEntry entry) =>
        (ScheduleEntryTime.SecondsOf(entry.TimeAsSeconds, entry.Time), entry.Value.ToString("R", CultureInfo.InvariantCulture));

    private static (int, string) RangeKey(TargetRangeEntry entry) =>
        (ScheduleEntryTime.SecondsOf(entry.TimeAsSeconds, entry.Time),
            string.Create(CultureInfo.InvariantCulture, $"{entry.Low:R}-{entry.High:R}"));
}
