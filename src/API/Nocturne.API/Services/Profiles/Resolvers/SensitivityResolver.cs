using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Resolves the insulin sensitivity factor at a given time by loading the active
/// <see cref="Core.Models.V4.SensitivitySchedule"/> and applying inverse CCP percentage scaling.
/// </summary>
internal sealed class SensitivityResolver : ISensitivityResolver
{
    private readonly ISensitivityScheduleRepository _repo;
    private readonly ITherapySettingsRepository _therapyRepo;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SensitivityResolver> _logger;

    private const int CacheTtlSeconds = 5;
    private const double DefaultSensitivity = 50.0;

    public SensitivityResolver(
        ISensitivityScheduleRepository repo,
        ITherapySettingsRepository therapyRepo,
        IActiveProfileResolver activeProfileResolver,
        ITenantAccessor tenantAccessor,
        IMemoryCache cache,
        ILogger<SensitivityResolver> logger)
    {
        _repo = repo;
        _therapyRepo = therapyRepo;
        _activeProfileResolver = activeProfileResolver;
        _tenantAccessor = tenantAccessor;
        _cache = cache;
        _logger = logger;
    }

    public async Task<double> GetSensitivityAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var profileName = specProfile
            ?? await _activeProfileResolver.GetActiveProfileNameAsync(timeMills, ct)
            ?? "Default";

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;

        var schedule = await GetCachedScheduleAsync(profileName, timestamp, ct);
        if (schedule is null)
            return DefaultSensitivity;

        var adjustment = await _activeProfileResolver.GetCircadianAdjustmentAsync(timeMills, ct);
        var shiftedMills = timeMills + (adjustment?.TimeshiftMs ?? 0);

        var secondsFromMidnight = await ScheduleTimeHelper.GetSecondsFromMidnightAsync(
            shiftedMills, profileName, timestamp, _therapyRepo, ct);

        var value = ScheduleResolution.FindValueAtTime(schedule.Entries, secondsFromMidnight)
            ?? DefaultSensitivity;

        if (adjustment is not null)
            value = value * 100.0 / adjustment.Percentage;

        return value;
    }

    private async Task<Core.Models.V4.SensitivitySchedule?> GetCachedScheduleAsync(
        string profileName, DateTime timestamp, CancellationToken ct)
    {
        var cacheKey = $"SensitivitySchedule:{_tenantAccessor.TenantId}:{profileName}";

        if (_cache.TryGetValue(cacheKey, out Core.Models.V4.SensitivitySchedule? cached))
            return cached;

        var schedule = await _repo.GetActiveAtAsync(profileName, timestamp, ct);
        _cache.Set(cacheKey, schedule, TimeSpan.FromSeconds(CacheTtlSeconds));
        return schedule;
    }
}
