using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Resolves scalar therapy settings (DIA, carb absorption rate, timezone, units) from the active
/// <see cref="Core.Models.V4.TherapySettings"/> record, with DIA priority chain considering
/// <see cref="Core.Models.V4.PatientInsulin"/> overrides.
/// </summary>
internal sealed class TherapySettingsResolver : ITherapySettingsResolver
{
    private readonly ITherapySettingsRepository _repo;
    private readonly IPatientInsulinRepository _insulinRepo;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TherapySettingsResolver> _logger;

    private const int CacheTtlSeconds = 5;
    private const double DefaultDia = 3.0;
    private const double DefaultCarbsHr = 20.0;

    public TherapySettingsResolver(
        ITherapySettingsRepository repo,
        IPatientInsulinRepository insulinRepo,
        IActiveProfileResolver activeProfileResolver,
        ITenantAccessor tenantAccessor,
        IMemoryCache cache,
        ILogger<TherapySettingsResolver> logger)
    {
        _repo = repo;
        _insulinRepo = insulinRepo;
        _activeProfileResolver = activeProfileResolver;
        _tenantAccessor = tenantAccessor;
        _cache = cache;
        _logger = logger;
    }

    public async Task<double> GetDIAAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var profileName = specProfile
            ?? await _activeProfileResolver.GetActiveProfileNameAsync(timeMills, ct)
            ?? "Default";

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;
        var settings = await GetCachedSettingsAsync(profileName, timestamp, ct);

        if (settings is null)
            return DefaultDia;

        // Priority 1: ExternallyManaged profiles use TherapySettings.Dia directly
        if (settings.IsExternallyManaged)
            return settings.Dia > 0 ? settings.Dia : DefaultDia;

        // Priority 2: PatientInsulin primary bolus DIA
        var primaryBolus = await _insulinRepo.GetPrimaryBolusInsulinAsync(ct);
        if (primaryBolus is not null)
            return primaryBolus.Dia > 0 ? primaryBolus.Dia : DefaultDia;

        // Priority 3: TherapySettings.Dia
        return settings.Dia > 0 ? settings.Dia : DefaultDia;
    }

    public async Task<double> GetCarbAbsorptionRateAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var profileName = specProfile
            ?? await _activeProfileResolver.GetActiveProfileNameAsync(timeMills, ct)
            ?? "Default";

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;
        var settings = await GetCachedSettingsAsync(profileName, timestamp, ct);

        return settings?.CarbsHr > 0 ? settings.CarbsHr : DefaultCarbsHr;
    }

    public async Task<string?> GetTimezoneAsync(string? specProfile = null, CancellationToken ct = default)
    {
        var profileName = specProfile ?? "Default";
        var settings = await GetCachedSettingsAsync(profileName, DateTime.UtcNow, ct);
        return settings?.Timezone;
    }

    public async Task<string?> GetUnitsAsync(string? specProfile = null, CancellationToken ct = default)
    {
        var profileName = specProfile ?? "Default";
        var settings = await GetCachedSettingsAsync(profileName, DateTime.UtcNow, ct);
        return settings?.Units;
    }

    public async Task<bool> HasDataAsync(CancellationToken ct = default)
    {
        var count = await _repo.CountAsync(null, null, ct);
        return count > 0;
    }

    private async Task<Core.Models.V4.TherapySettings?> GetCachedSettingsAsync(
        string profileName, DateTime timestamp, CancellationToken ct)
    {
        var cacheKey = $"TherapySettings:{_tenantAccessor.TenantId}:{profileName}";

        if (_cache.TryGetValue(cacheKey, out Core.Models.V4.TherapySettings? cached))
            return cached;

        var settings = await _repo.GetActiveAtAsync(profileName, timestamp, ct);
        _cache.Set(cacheKey, settings, TimeSpan.FromSeconds(CacheTtlSeconds));
        return settings;
    }
}
