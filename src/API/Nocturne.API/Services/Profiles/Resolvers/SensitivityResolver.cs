using Microsoft.Extensions.Logging;
using Nocturne.Core.Clinical.Schedules;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

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
    private readonly TimeProvider _time;
    private readonly ILogger<SensitivityResolver> _logger;

    private const double DefaultSensitivity = 50.0;

    public SensitivityResolver(
        ISensitivityScheduleRepository repo,
        ITherapySettingsRepository therapyRepo,
        IPatientRecordRepository patientRecordRepo,
        IActiveProfileResolver activeProfileResolver,
        ProfileVersionCache versions,
        IProfileActivationResolver activations,
        TimeProvider timeProvider,
        ILogger<SensitivityResolver> logger)
    {
        _repo = repo;
        _therapyRepo = therapyRepo;
        _patientRecordRepo = patientRecordRepo;
        _activeProfileResolver = activeProfileResolver;
        _versions = versions;
        _activations = activations;
        _time = timeProvider;
        _logger = logger;
    }

    public async Task<double> GetSensitivityAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var schedule = await ResolveScheduleAsync(timeMills, specProfile, ct);
        if (schedule is null)
            return DefaultSensitivity;

        return Scaled(
            ScheduleResolution.FindValueAtTime(schedule.Entries, schedule.SecondsFromMidnight) ?? DefaultSensitivity,
            schedule);
    }

    public async Task<double?> FindSensitivityAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var schedule = await ResolveScheduleAsync(timeMills, specProfile, ct);
        return schedule is not null
            && ScheduleResolution.FindValueAtTime(schedule.Entries, schedule.SecondsFromMidnight) is { } value
                ? Scaled(value, schedule)
                : null;
    }

    private Task<ActiveSchedule.At<ScheduleEntry>?> ResolveScheduleAsync(
        long timeMills, string? specProfile, CancellationToken ct) =>
        ActiveSchedule.ResolveAsync(
            timeMills, specProfile, _activeProfileResolver, _repo, _versions,
            s => s.Entries, s => s.Sensitivity, _therapyRepo, _patientRecordRepo, ct);

    private static double Scaled(double value, ActiveSchedule.At<ScheduleEntry> schedule) =>
        schedule.Adjustment is { } adjustment ? value * 100.0 / adjustment.Percentage : value;

    /// <remarks>
    /// Read off the activation itself, which carries its percentage whether or not its schedules
    /// already have it applied.
    /// </remarks>
    public async Task<double?> GetCurrentSensitivityPercentAsync(CancellationToken ct = default)
    {
        var nowMills = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var active = await _activations.GetAtAsync(nowMills, ct);
        if (active is null || active.Activation.Percentage <= 0)
            return null;
        return 10_000.0 / active.Activation.Percentage;
    }
}
