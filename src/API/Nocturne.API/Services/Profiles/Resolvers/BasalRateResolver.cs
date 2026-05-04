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
