using Microsoft.Extensions.Logging;
using Nocturne.Core.Clinical.Schedules;
using Nocturne.Core.Constants;
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
    private readonly IPatientRecordRepository _patientRecordRepo;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly ProfileVersionCache _versions;
    private readonly ILogger<TargetRangeResolver> _logger;

    private const double DefaultLow = GlucoseConstants.TargetBottomMgdl;
    private const double DefaultHigh = GlucoseConstants.TargetTopMgdl;

    public TargetRangeResolver(
        ITargetRangeScheduleRepository repo,
        ITherapySettingsRepository therapyRepo,
        IPatientRecordRepository patientRecordRepo,
        IActiveProfileResolver activeProfileResolver,
        ProfileVersionCache versions,
        ILogger<TargetRangeResolver> logger)
    {
        _repo = repo;
        _therapyRepo = therapyRepo;
        _patientRecordRepo = patientRecordRepo;
        _activeProfileResolver = activeProfileResolver;
        _versions = versions;
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

    public Task<(double Low, double High)> GetBGTargetRangeAsync(long timeMills, string? specProfile = null, CancellationToken ct = default)
        => ResolveRangeAsync(timeMills, specProfile, ct);

    public async Task<(double Low, double High)?> FindBGTargetRangeAsync(
        long timeMills, string? specProfile = null, CancellationToken ct = default)
    {
        var schedule = await ActiveSchedule.ResolveAsync(
            timeMills, specProfile, _activeProfileResolver, _repo, _versions,
            s => s.Entries, s => s.Targets, _therapyRepo, _patientRecordRepo, ct);
        return schedule is null
            ? null
            : ScheduleResolution.FindRangeAtTime(schedule.Entries, schedule.SecondsFromMidnight);
    }

    private async Task<(double Low, double High)> ResolveRangeAsync(
        long timeMills, string? specProfile, CancellationToken ct)
        => await FindBGTargetRangeAsync(timeMills, specProfile, ct) ?? (DefaultLow, DefaultHigh);
}
