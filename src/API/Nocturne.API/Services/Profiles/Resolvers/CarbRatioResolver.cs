using Microsoft.Extensions.Logging;
using Nocturne.Core.Clinical.Schedules;
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
    private readonly IPatientRecordRepository _patientRecordRepo;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ProfileVersionCache _versions;
    private readonly ILogger<CarbRatioResolver> _logger;

    private const double DefaultCarbRatio = 12.0;

    public CarbRatioResolver(
        ICarbRatioScheduleRepository repo,
        ITherapySettingsRepository therapyRepo,
        IPatientRecordRepository patientRecordRepo,
        IActiveProfileResolver activeProfileResolver,
        ProfileVersionCache versions,
        ILogger<CarbRatioResolver> logger)
    {
        _repo = repo;
        _therapyRepo = therapyRepo;
        _patientRecordRepo = patientRecordRepo;
        _activeProfileResolver = activeProfileResolver;
        _versions = versions;
        _logger = logger;
    }

    public async Task<double> GetCarbRatioAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var schedule = await ActiveSchedule.ResolveAsync(
            timeMills, specProfile, _activeProfileResolver, _repo, _versions,
            s => s.Entries, s => s.CarbRatio, _therapyRepo, _patientRecordRepo, ct);
        if (schedule is null)
            return DefaultCarbRatio;

        var value = ScheduleResolution.FindValueAtTime(schedule.Entries, schedule.SecondsFromMidnight)
            ?? DefaultCarbRatio;

        if (schedule.Adjustment is { } adjustment)
            value = value * 100.0 / adjustment.Percentage;

        return value;
    }
}
