using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Resolves the scheduled basal rate at a given time by loading the active <see cref="Core.Models.V4.BasalSchedule"/>
/// and applying CCP percentage scaling.
/// </summary>
internal sealed class BasalRateResolver : IBasalRateResolver
{
    private readonly IBasalScheduleRepository _repo;
    private readonly ITherapySettingsRepository _therapyRepo;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BasalRateResolver> _logger;

    private const int CacheTtlSeconds = 5;
    private const double DefaultBasalRate = 1.0;

    public BasalRateResolver(
        IBasalScheduleRepository repo,
        ITherapySettingsRepository therapyRepo,
        IActiveProfileResolver activeProfileResolver,
        ITenantAccessor tenantAccessor,
        IMemoryCache cache,
        ILogger<BasalRateResolver> logger)
    {
        _repo = repo;
        _therapyRepo = therapyRepo;
        _activeProfileResolver = activeProfileResolver;
        _tenantAccessor = tenantAccessor;
        _cache = cache;
        _logger = logger;
    }

    public async Task<double> GetBasalRateAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var profileName = specProfile
            ?? await _activeProfileResolver.GetActiveProfileNameAsync(timeMills, ct)
            ?? "Default";

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;

        var schedule = await GetCachedScheduleAsync(profileName, timestamp, ct);
        if (schedule is null)
            return DefaultBasalRate;

        var adjustment = await _activeProfileResolver.GetCircadianAdjustmentAsync(timeMills, ct);
        var shiftedMills = timeMills + (adjustment?.TimeshiftMs ?? 0);

        var secondsFromMidnight = await ScheduleTimeHelper.GetSecondsFromMidnightAsync(
            shiftedMills, profileName, timestamp, _therapyRepo, ct);

        var value = ScheduleResolution.FindValueAtTime(schedule.Entries, secondsFromMidnight)
            ?? DefaultBasalRate;

        if (adjustment is not null)
            value = value * adjustment.Percentage / 100.0;

        return value;
    }

    public async Task<Func<long, double>> BuildResolverAsync(
        long fromMs, long toMs, CancellationToken ct = default)
    {
        // 1. One query for all profile spans covering the range.
        var spans = await _activeProfileResolver.GetActiveProfileSpansForRangeAsync(fromMs, toMs, ct);

        // 2. Pre-fetch schedule + timezone per distinct profile (typically just "Default").
        //    Fetch at range start — same convention as BasalSegmentService.
        var rangeStart = DateTimeOffset.FromUnixTimeMilliseconds(fromMs).UtcDateTime;

        var distinctProfiles = spans.Select(s => s.ProfileName).Distinct().ToList();
        if (distinctProfiles.Count == 0)
            distinctProfiles.Add("Default");

        var schedules = new Dictionary<string, Core.Models.V4.BasalSchedule?>(StringComparer.OrdinalIgnoreCase);
        var timezones = new Dictionary<string, TimeZoneInfo?>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in distinctProfiles)
        {
            schedules[name] = await GetCachedScheduleAsync(name, rangeStart, ct);

            var therapy = await _therapyRepo.GetActiveAtAsync(name, rangeStart, ct);
            TimeZoneInfo? tz = null;
            if (!string.IsNullOrEmpty(therapy?.Timezone))
            {
                try { tz = TimeZoneInfo.FindSystemTimeZoneById(therapy.Timezone); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            timezones[name] = tz;
        }

        // Closure over pre-fetched data — no DB access inside.
        return timeMills =>
        {
            // Find the active span at timeMills.
            // Spans are in chronological order (guaranteed by GetActiveProfileSpansForRangeAsync);
            // last match wins when spans abut exactly.
            ProfileSpan? active = null;
            foreach (var span in spans)
            {
                if (span.StartMills <= timeMills &&
                    (!span.EndMills.HasValue || span.EndMills.Value > timeMills))
                {
                    active = span;
                }
            }

            var profileName  = active?.ProfileName ?? "Default";
            var adjustment   = active?.Adjustment;
            var shiftedMills = timeMills + (adjustment?.TimeshiftMs ?? 0);

            if (!schedules.TryGetValue(profileName, out var schedule) || schedule is null)
                return DefaultBasalRate;

            var tz  = timezones.GetValueOrDefault(profileName);
            var dto = DateTimeOffset.FromUnixTimeMilliseconds(shiftedMills);
            if (tz is not null)
                dto = TimeZoneInfo.ConvertTime(dto, tz);

            var secondsFromMidnight = (int)dto.TimeOfDay.TotalSeconds;
            var value = ScheduleResolution.FindValueAtTime(schedule.Entries, secondsFromMidnight)
                ?? DefaultBasalRate;

            return adjustment is not null
                ? value * adjustment.Percentage / 100.0
                : value;
        };
    }

    private async Task<Core.Models.V4.BasalSchedule?> GetCachedScheduleAsync(
        string profileName, DateTime timestamp, CancellationToken ct)
    {
        var cacheKey = $"BasalSchedule:{_tenantAccessor.TenantId}:{profileName}";

        if (_cache.TryGetValue(cacheKey, out Core.Models.V4.BasalSchedule? cached))
            return cached;

        var schedule = await _repo.GetActiveAtAsync(profileName, timestamp, ct);
        _cache.Set(cacheKey, schedule, TimeSpan.FromSeconds(CacheTtlSeconds));
        return schedule;
    }
}
