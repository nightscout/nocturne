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

    public async Task<Func<long, double>> BuildResolverAsync(
        long fromMs, long toMs, CancellationToken ct = default)
    {
        // 1. One query for all profile spans covering the range.
        var spans = await _activeProfileResolver.GetActiveProfileSpansForRangeAsync(fromMs, toMs, ct);

        // 2. Pre-fetch schedule + timezone per distinct profile, and the default store that fills
        //    the time no span covers. Fetch at range start — same convention as BasalSegmentService.
        var rangeStart = DateTimeOffset.FromUnixTimeMilliseconds(fromMs).UtcDateTime;

        var defaultProfile = await _activeProfileResolver.GetDefaultProfileNameAsync(ct);
        var distinctProfiles = spans.Select(s => s.ProfileName).Append(defaultProfile).Distinct().ToList();

        var schedules = new Dictionary<string, Core.Models.V4.BasalSchedule?>(StringComparer.OrdinalIgnoreCase);
        var timezones = new Dictionary<string, TimeZoneInfo?>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in distinctProfiles)
        {
            schedules[name] = await GetCachedScheduleAsync(name, rangeStart, ct);

            var therapy = await _therapyRepo.GetActiveAtAsync(name, rangeStart, ct);
            // null when unset/unresolvable so the closure skips conversion (treats the stamp as UTC).
            // TimeZoneHelper resolves IANA/Windows IDs and mis-cased IANA IDs (e.g. "ETC/GMT-2").
            if (TimeZoneHelper.TryGetTimeZoneInfoFromId(therapy?.Timezone, out var tz))
            {
                timezones[name] = tz;
            }
            else
            {
                if (!string.IsNullOrEmpty(therapy?.Timezone))
                    _logger.LogWarning("Timezone '{Timezone}' for profile '{Profile}' could not be resolved", therapy.Timezone, name);
                timezones[name] = null;
            }
        }

        // Closure over pre-fetched data — no DB access inside.
        return timeMills =>
        {
            // Find the active span at timeMills.
            // Spans are in chronological order (guaranteed by GetActiveProfileSpansForRangeAsync);
            // LastOrDefault keeps the last match so abutting spans resolve to the later one.
            var active = spans.LastOrDefault(span =>
                span.StartMills <= timeMills &&
                (!span.EndMills.HasValue || span.EndMills.Value > timeMills));

            var profileName  = active?.ProfileName ?? defaultProfile;
            var adjustment   = active?.Adjustment;
            var shiftedMills = timeMills + (adjustment?.TimeshiftMs ?? 0);

            List<Core.Models.V4.ScheduleEntry>? entries;
            TimeZoneInfo? tz;
            if (active?.Schedules is { } snapshot)
            {
                entries = snapshot.Basal;
                tz = TimeZoneHelper.TryGetTimeZoneInfoFromId(snapshot.Timezone, out var snapshotTz)
                    ? snapshotTz
                    : timezones.GetValueOrDefault(profileName);
            }
            else
            {
                entries = schedules.GetValueOrDefault(profileName)?.Entries;
                tz = timezones.GetValueOrDefault(profileName);
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

    private Task<Core.Models.V4.BasalSchedule?> GetCachedScheduleAsync(
        string profileName, DateTime timestamp, CancellationToken ct) =>
        _versions.GetActiveAtAsync(_repo, profileName, timestamp, ct);
}
