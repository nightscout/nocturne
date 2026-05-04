using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Resolves the BG target range at a given time by loading the active
/// <see cref="Core.Models.V4.TargetRangeSchedule"/>. Targets are not adjusted by CCP.
/// </summary>
internal sealed class TargetRangeResolver : ITargetRangeResolver
{
    private readonly ITargetRangeScheduleRepository _repo;
    private readonly ITherapySettingsRepository _therapyRepo;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TargetRangeResolver> _logger;

    private const int CacheTtlSeconds = 5;
    private const double DefaultLow = 70.0;
    private const double DefaultHigh = 180.0;

    public TargetRangeResolver(
        ITargetRangeScheduleRepository repo,
        ITherapySettingsRepository therapyRepo,
        IActiveProfileResolver activeProfileResolver,
        ITenantAccessor tenantAccessor,
        IMemoryCache cache,
        ILogger<TargetRangeResolver> logger)
    {
        _repo = repo;
        _therapyRepo = therapyRepo;
        _activeProfileResolver = activeProfileResolver;
        _tenantAccessor = tenantAccessor;
        _cache = cache;
        _logger = logger;
    }

    public async Task<double> GetLowBGTargetAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var (low, _) = await ResolveRangeAsync(timeMills, specProfile, ct);
        return low;
    }

    public async Task<double> GetHighBGTargetAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var (_, high) = await ResolveRangeAsync(timeMills, specProfile, ct);
        return high;
    }

    private async Task<(double Low, double High)> ResolveRangeAsync(
        long timeMills, string? specProfile, CancellationToken ct)
    {
        var profileName = specProfile
            ?? await _activeProfileResolver.GetActiveProfileNameAsync(timeMills, ct)
            ?? "Default";

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;

        var schedule = await GetCachedScheduleAsync(profileName, timestamp, ct);
        if (schedule is null)
            return (DefaultLow, DefaultHigh);

        var adjustment = await _activeProfileResolver.GetCircadianAdjustmentAsync(timeMills, ct);
        var shiftedMills = timeMills + (adjustment?.TimeshiftMs ?? 0);

        var secondsFromMidnight = await ScheduleTimeHelper.GetSecondsFromMidnightAsync(
            shiftedMills, profileName, timestamp, _therapyRepo, ct);

        var range = ScheduleResolution.FindRangeAtTime(schedule.Entries, secondsFromMidnight);
        return range ?? (DefaultLow, DefaultHigh);
    }

    private async Task<Core.Models.V4.TargetRangeSchedule?> GetCachedScheduleAsync(
        string profileName, DateTime timestamp, CancellationToken ct)
    {
        var cacheKey = $"TargetRangeSchedule:{_tenantAccessor.TenantId}:{profileName}";

        if (_cache.TryGetValue(cacheKey, out Core.Models.V4.TargetRangeSchedule? cached))
            return cached;

        var schedule = await _repo.GetActiveAtAsync(profileName, timestamp, ct);
        _cache.Set(cacheKey, schedule, TimeSpan.FromSeconds(CacheTtlSeconds));
        return schedule;
    }
}
