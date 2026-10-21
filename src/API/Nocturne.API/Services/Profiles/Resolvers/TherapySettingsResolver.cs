using Microsoft.Extensions.Logging;
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
    private readonly IPatientRecordRepository _patientRecordRepo;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ProfileVersionCache _versions;
    private readonly ILogger<TherapySettingsResolver> _logger;

    private bool? _hasData;

    private const double DefaultDia = 3.0;
    private const double DefaultCarbsHr = 20.0;

    public TherapySettingsResolver(
        ITherapySettingsRepository repo,
        IPatientInsulinRepository insulinRepo,
        IPatientRecordRepository patientRecordRepo,
        IActiveProfileResolver activeProfileResolver,
        ProfileVersionCache versions,
        ILogger<TherapySettingsResolver> logger)
    {
        _repo = repo;
        _insulinRepo = insulinRepo;
        _patientRecordRepo = patientRecordRepo;
        _activeProfileResolver = activeProfileResolver;
        _versions = versions;
        _logger = logger;
    }

    /// <remarks>
    /// Schedules an activation was uploaded with come from an AID that manages the profile, so their
    /// DIA stands as an externally managed profile's does.
    /// </remarks>
    public async Task<double> GetDIAAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        if (specProfile is null
            && await _activeProfileResolver.GetActiveSchedulesAsync(timeMills, ct) is { } snapshot
            && (snapshot.Insulin?.Dia ?? snapshot.Dia) is > 0 and var snapshotDia)
            return snapshotDia;

        var profileName = specProfile
            ?? await _activeProfileResolver.GetActiveProfileNameAsync(timeMills, ct)
            ?? await _activeProfileResolver.GetDefaultProfileNameAsync(ct);

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
        if (specProfile is null
            && await _activeProfileResolver.GetActiveSchedulesAsync(timeMills, ct) is { CarbsHr: > 0 } snapshot)
            return snapshot.CarbsHr!.Value;

        var profileName = specProfile
            ?? await _activeProfileResolver.GetActiveProfileNameAsync(timeMills, ct)
            ?? await _activeProfileResolver.GetDefaultProfileNameAsync(ct);

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;
        var settings = await GetCachedSettingsAsync(profileName, timestamp, ct);

        return settings?.CarbsHr > 0 ? settings.CarbsHr : DefaultCarbsHr;
    }

    public async Task<string?> GetTimezoneAsync(string? specProfile = null, CancellationToken ct = default)
    {
        // PatientRecord is the canonical source — a patient lives in one timezone regardless of
        // how many therapy profiles they have. The per-profile TherapySettings.Timezone field
        // remains a legacy fallback because connector-imported profiles (Nightscout, Glooko)
        // wrote into it before this move; backfill brings those rows forward on next migration,
        // but until then we read the legacy value rather than silently UTCing.
        var patient = await _patientRecordRepo.GetAsync(ct);
        if (!string.IsNullOrEmpty(patient?.Timezone))
            return patient.Timezone;

        var nowMills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (specProfile is null
            && await _activeProfileResolver.GetActiveSchedulesAsync(nowMills, ct) is { Timezone: { Length: > 0 } snapshotTimezone })
            return snapshotTimezone;

        var profileName = specProfile
            ?? await _activeProfileResolver.GetActiveProfileNameAsync(nowMills, ct)
            ?? await _activeProfileResolver.GetDefaultProfileNameAsync(ct);
        var settings = await GetCachedSettingsAsync(profileName, DateTime.UtcNow, ct);
        return settings?.Timezone;
    }

    public async Task<string?> GetUnitsAsync(string? specProfile = null, CancellationToken ct = default)
    {
        var profileName = specProfile ?? await _activeProfileResolver.GetDefaultProfileNameAsync(ct);
        var settings = await GetCachedSettingsAsync(profileName, DateTime.UtcNow, ct);
        return settings?.Units;
    }

    public async Task<bool> HasDataAsync(CancellationToken ct = default)
    {
        // Scoped resolver: the memo lives for one request and never crosses tenants.
        return _hasData ??= await _repo.CountAsync(null, null, ct) > 0;
    }

    private Task<Core.Models.V4.TherapySettings?> GetCachedSettingsAsync(
        string profileName, DateTime timestamp, CancellationToken ct) =>
        _versions.GetActiveAtAsync(_repo, profileName, timestamp, ct);
}
