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
    private readonly IProfileActivationResolver _activations;
    private readonly ILogger<SensitivityResolver> _logger;

    private const double DefaultSensitivity = 50.0;

    public SensitivityResolver(
        ISensitivityScheduleRepository repo,
        ITherapySettingsRepository therapyRepo,
        IPatientRecordRepository patientRecordRepo,
        IActiveProfileResolver activeProfileResolver,
        ProfileVersionCache versions,
        IProfileActivationResolver activations,
        ILogger<SensitivityResolver> logger)
    {
        _repo = repo;
        _therapyRepo = therapyRepo;
        _patientRecordRepo = patientRecordRepo;
        _activeProfileResolver = activeProfileResolver;
        _versions = versions;
        _activations = activations;
        _logger = logger;
    }

    public async Task<double> GetSensitivityAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var schedule = await ActiveSchedule.ResolveAsync(
            timeMills, specProfile, _activeProfileResolver, _repo, _versions,
            s => s.Entries, s => s.Sensitivity, _therapyRepo, _patientRecordRepo, ct);
        if (schedule is null)
            return DefaultSensitivity;

        var value = ScheduleResolution.FindValueAtTime(schedule.Entries, schedule.SecondsFromMidnight)
            ?? DefaultSensitivity;

        if (schedule.Adjustment is { } adjustment)
            value = value * 100.0 / adjustment.Percentage;

        return value;
    }

    /// <remarks>
    /// Read off the activation itself, which carries its percentage whether or not its schedules
    /// already have it applied.
    /// </remarks>
    public async Task<double?> GetCurrentSensitivityPercentAsync(CancellationToken ct = default)
    {
        var nowMills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var active = await _activations.GetAtAsync(nowMills, ct);
        if (active is null || active.Activation.Percentage <= 0)
            return null;
        return 10_000.0 / active.Activation.Percentage;
    }
}
