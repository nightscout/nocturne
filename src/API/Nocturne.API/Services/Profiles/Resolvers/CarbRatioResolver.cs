using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Resolves the insulin-to-carb ratio at a given time by loading the active
/// <see cref="Core.Models.V4.CarbRatioSchedule"/> and applying inverse CCP percentage scaling.
/// </summary>
internal sealed class CarbRatioResolver : ICarbRatioResolver
{
    private readonly ICarbRatioScheduleRepository _repo;
    private readonly ITherapySettingsRepository _therapyRepo;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CarbRatioResolver> _logger;

    private const int CacheTtlSeconds = 5;
    private const double DefaultCarbRatio = 12.0;

    public CarbRatioResolver(
        ICarbRatioScheduleRepository repo,
        ITherapySettingsRepository therapyRepo,
        IActiveProfileResolver activeProfileResolver,
        ITenantAccessor tenantAccessor,
        IMemoryCache cache,
        ILogger<CarbRatioResolver> logger)
    {
        _repo = repo;
        _therapyRepo = therapyRepo;
        _activeProfileResolver = activeProfileResolver;
        _tenantAccessor = tenantAccessor;
        _cache = cache;
        _logger = logger;
    }

    public async Task<double> GetCarbRatioAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var profileName = specProfile
            ?? await _activeProfileResolver.GetActiveProfileNameAsync(timeMills, ct)
            ?? "Default";

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;

        var schedule = await GetCachedScheduleAsync(profileName, timestamp, ct);
        if (schedule is null)
            return DefaultCarbRatio;

        var adjustment = await _activeProfileResolver.GetCircadianAdjustmentAsync(timeMills, ct);
        var shiftedMills = timeMills + (adjustment?.TimeshiftMs ?? 0);

        var secondsFromMidnight = await ScheduleTimeHelper.GetSecondsFromMidnightAsync(
            shiftedMills, profileName, timestamp, _therapyRepo, ct);

        var value = ScheduleResolution.FindValueAtTime(schedule.Entries, secondsFromMidnight)
            ?? DefaultCarbRatio;

        if (adjustment is not null)
            value = value * 100.0 / adjustment.Percentage;

        return value;
    }

    private async Task<Core.Models.V4.CarbRatioSchedule?> GetCachedScheduleAsync(
        string profileName, DateTime timestamp, CancellationToken ct)
    {
        var cacheKey = $"CarbRatioSchedule:{_tenantAccessor.TenantId}:{profileName}";

        if (_cache.TryGetValue(cacheKey, out Core.Models.V4.CarbRatioSchedule? cached))
            return cached;

        var schedule = await _repo.GetActiveAtAsync(profileName, timestamp, ct);
        _cache.Set(cacheKey, schedule, TimeSpan.FromSeconds(CacheTtlSeconds));
        return schedule;
    }
}
