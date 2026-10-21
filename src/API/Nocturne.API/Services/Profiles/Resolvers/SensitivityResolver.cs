using Microsoft.Extensions.Logging;
using Nocturne.Core.Clinical.Schedules;
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
    private readonly IPatientRecordRepository _patientRecordRepo;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ProfileVersionCache _versions;
    private readonly ILogger<SensitivityResolver> _logger;

    private const double DefaultSensitivity = 50.0;

    public SensitivityResolver(
        ISensitivityScheduleRepository repo,
        ITherapySettingsRepository therapyRepo,
        IPatientRecordRepository patientRecordRepo,
        IActiveProfileResolver activeProfileResolver,
        ProfileVersionCache versions,
        ILogger<SensitivityResolver> logger)
    {
        _repo = repo;
        _therapyRepo = therapyRepo;
        _patientRecordRepo = patientRecordRepo;
        _activeProfileResolver = activeProfileResolver;
        _versions = versions;
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
            shiftedMills, profileName, timestamp, _therapyRepo, _patientRecordRepo, ct);

        var value = ScheduleResolution.FindValueAtTime(schedule.Entries, secondsFromMidnight)
            ?? DefaultSensitivity;

        if (adjustment is not null)
            value = value * 100.0 / adjustment.Percentage;

        return value;
    }

    public async Task<double?> GetCurrentSensitivityPercentAsync(CancellationToken ct = default)
    {
        var nowMills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var adjustment = await _activeProfileResolver.GetCircadianAdjustmentAsync(nowMills, ct);
        if (adjustment is null || adjustment.Percentage <= 0)
            return null;
        return 10_000.0 / adjustment.Percentage;
    }

    private Task<Core.Models.V4.SensitivitySchedule?> GetCachedScheduleAsync(
        string profileName, DateTime timestamp, CancellationToken ct) =>
        _versions.GetActiveAtAsync(_repo, profileName, timestamp, ct);
}
