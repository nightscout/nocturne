using Microsoft.Extensions.Logging;
using Nocturne.Core.Clinical.Schedules;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Resolves the scheduled basal rate at a given time by loading the active <see cref="Core.Models.V4.BasalSchedule"/>
/// and applying CCP percentage scaling.
/// </summary>
internal sealed class BasalRateResolver : IBasalRateResolver
{
    private readonly IBasalScheduleRepository _repo;
    private readonly ITherapySettingsRepository _therapyRepo;
    private readonly IPatientRecordRepository _patientRecordRepo;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ProfileVersionCache _versions;
    private readonly ILogger<BasalRateResolver> _logger;

    private const double DefaultBasalRate = 1.0;

    public BasalRateResolver(
        IBasalScheduleRepository repo,
        ITherapySettingsRepository therapyRepo,
        IPatientRecordRepository patientRecordRepo,
        IActiveProfileResolver activeProfileResolver,
        ProfileVersionCache versions,
        ILogger<BasalRateResolver> logger)
    {
        _repo = repo;
        _therapyRepo = therapyRepo;
        _patientRecordRepo = patientRecordRepo;
        _activeProfileResolver = activeProfileResolver;
        _versions = versions;
        _logger = logger;
    }

    public async Task<double> GetBasalRateAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var schedule = await ActiveSchedule.ResolveAsync(
            timeMills, specProfile, _activeProfileResolver, _repo, _versions,
            s => s.Entries, s => s.Basal, _therapyRepo, _patientRecordRepo, ct);
        if (schedule is null)
            return DefaultBasalRate;

        var value = ScheduleResolution.FindValueAtTime(schedule.Entries, schedule.SecondsFromMidnight)
            ?? DefaultBasalRate;

        if (schedule.Adjustment is { } adjustment)
            value = value * adjustment.Percentage / 100.0;

        return value;
    }

    /// <remarks>
    /// Each time reads the schedule and timezone versions in force at it, so a profile upload inside
    /// the range applies from its own start. A time outside the range reads the span nearest it.
    /// </remarks>
    public async Task<Func<long, double>> BuildResolverAsync(
        long fromMs, long toMs, CancellationToken ct = default)
    {
        var spans = await _activeProfileResolver.GetProfileSpansForRangeAsync(fromMs, toMs, ct);

        var stores = new Dictionary<string, StoreVersions>(StringComparer.Ordinal);
        var timezones = new Dictionary<string, TimeZoneInfo?>(StringComparer.Ordinal);
        foreach (var name in spans.Select(s => s.ProfileName).Distinct(StringComparer.Ordinal))
        {
            var settings = await _versions.GetVersionsAsync(_therapyRepo, name, ct);
            stores[name] = new StoreVersions(await _versions.GetVersionsAsync(_repo, name, ct), settings);
            foreach (var id in settings.Select(v => v.Timezone).OfType<string>().Where(id => id.Length > 0))
            {
                if (timezones.ContainsKey(id))
                    continue;
                // TimeZoneHelper resolves IANA/Windows IDs and mis-cased IANA IDs (e.g. "ETC/GMT-2").
                timezones[id] = TimeZoneHelper.TryGetTimeZoneInfoFromId(id, out var tz) ? tz : null;
                if (timezones[id] is null)
                    _logger.LogWarning("Timezone '{Timezone}' for profile '{Profile}' could not be resolved", id, name);
            }
        }

        // Closure over pre-fetched data — no DB access inside.
        return timeMills =>
        {
            if (SpanAt(spans, timeMills) is not { } span)
                return DefaultBasalRate;

            var at = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;
            var store = stores[span.ProfileName];
            var adjustment = span.Adjustment;
            var shiftedMills = timeMills + (adjustment?.TimeshiftMs ?? 0);

            // null when unset/unresolvable so the stamp is read as UTC.
            var tz = ProfileVersionCache.ActiveAt(store.Settings, at)?.Timezone is { Length: > 0 } id
                ? timezones.GetValueOrDefault(id)
                : null;
            List<Core.Models.V4.ScheduleEntry>? entries;
            if (span.Schedules is { } snapshot)
            {
                entries = snapshot.Basal;
                if (TimeZoneHelper.TryGetTimeZoneInfoFromId(snapshot.Timezone, out var snapshotTz))
                    tz = snapshotTz;
            }
            else
            {
                entries = ProfileVersionCache.ActiveAt(store.Basal, at)?.Entries;
            }

            if (entries is null)
                return DefaultBasalRate;

            var dto = DateTimeOffset.FromUnixTimeMilliseconds(shiftedMills);
            if (tz is not null)
                dto = TimeZoneInfo.ConvertTime(dto, tz);

            var secondsFromMidnight = (int)dto.TimeOfDay.TotalSeconds;
            var value = ScheduleResolution.FindValueAtTime(entries, secondsFromMidnight)
                ?? DefaultBasalRate;

            return adjustment is not null
                ? value * adjustment.Percentage / 100.0
                : value;
        };
    }

    /// <summary>The span holding <paramref name="timeMills"/>, or the nearest one when none does.</summary>
    private static ProfileSpan? SpanAt(IReadOnlyList<ProfileSpan> spans, long timeMills)
    {
        if (spans.Count == 0)
            return null;

        int low = 0, high = spans.Count - 1, found = 0;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (spans[mid].StartMills <= timeMills)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return spans[found];
    }

    private sealed record StoreVersions(
        Core.Models.V4.BasalSchedule[] Basal, Core.Models.V4.TherapySettings[] Settings);
}
