using Microsoft.Extensions.Logging;
using Nocturne.Core.Clinical.Schedules;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

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
            && SnapshotDia(snapshot) is { } snapshotDia)
            return snapshotDia;

        var profileName = specProfile ?? await _activeProfileResolver.GetProfileNameAsync(timeMills, ct);

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

    /// <summary>
    /// The DIA an activation was uploaded with: its insulin's, else its own, or null when neither is
    /// set. A zero or negative value is not set.
    /// </summary>
    internal static double? SnapshotDia(ProfileActivationSnapshot snapshot) =>
        snapshot.Insulin?.Dia is > 0 and var insulinDia ? insulinDia
        : snapshot.Dia is > 0 and var dia ? dia
        : null;

    public async Task<double> GetCarbAbsorptionRateAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        if (specProfile is null
            && await _activeProfileResolver.GetActiveSchedulesAsync(timeMills, ct) is { CarbsHr: > 0 } snapshot)
            return snapshot.CarbsHr!.Value;

        var profileName = specProfile ?? await _activeProfileResolver.GetProfileNameAsync(timeMills, ct);

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime;
        var settings = await GetCachedSettingsAsync(profileName, timestamp, ct);

        return settings?.CarbsHr > 0 ? settings.CarbsHr : DefaultCarbsHr;
    }

    public Task<string?> GetTimezoneAsync(string? specProfile = null, CancellationToken ct = default) =>
        GetTimezoneAtAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), specProfile, ct);

    /// <remarks>The order is <see cref="ScheduleTimeHelper.TimezoneOf"/>'s.</remarks>
    public async Task<string?> GetTimezoneAtAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var patient = await _patientRecordRepo.GetAsync(ct);
        if (!string.IsNullOrEmpty(patient?.Timezone))
            return patient.Timezone;

        var snapshot = specProfile is null ? await _activeProfileResolver.GetActiveSchedulesAsync(timeMills, ct) : null;
        var profileName = specProfile ?? await _activeProfileResolver.GetProfileNameAsync(timeMills, ct);
        var settings = await GetCachedSettingsAsync(
            profileName, DateTimeOffset.FromUnixTimeMilliseconds(timeMills).UtcDateTime, ct);
        return ScheduleTimeHelper.TimezoneOf(null, snapshot?.Timezone, settings?.Timezone);
    }

    public async Task<string?> GetUnitsAsync(string? specProfile = null, CancellationToken ct = default)
    {
        var profileName = specProfile
            ?? await _activeProfileResolver.GetDefaultProfileNameAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
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
