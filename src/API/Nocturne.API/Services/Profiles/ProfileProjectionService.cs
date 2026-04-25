using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles;

/// <summary>
/// Reconstructs legacy <see cref="Profile"/> records from V4 schedule data.
/// Queries therapy settings and correlated schedule repositories, then maps
/// them into the monolithic profile shape expected by V1/V3 API consumers.
/// </summary>
public class ProfileProjectionService : IProfileProjectionService
{
    private readonly ITherapySettingsRepository _therapyRepo;
    private readonly IBasalScheduleRepository _basalRepo;
    private readonly ICarbRatioScheduleRepository _carbRatioRepo;
    private readonly ISensitivityScheduleRepository _sensitivityRepo;
    private readonly ITargetRangeScheduleRepository _targetRangeRepo;

    public ProfileProjectionService(
        ITherapySettingsRepository therapyRepo,
        IBasalScheduleRepository basalRepo,
        ICarbRatioScheduleRepository carbRatioRepo,
        ISensitivityScheduleRepository sensitivityRepo,
        ITargetRangeScheduleRepository targetRangeRepo)
    {
        _therapyRepo = therapyRepo;
        _basalRepo = basalRepo;
        _carbRatioRepo = carbRatioRepo;
        _sensitivityRepo = sensitivityRepo;
        _targetRangeRepo = targetRangeRepo;
    }

    /// <inheritdoc />
    public async Task<Profile?> GetCurrentProfileAsync(CancellationToken ct = default)
    {
        var settings = await _therapyRepo.GetAsync(
            from: null, to: null, device: null, source: null,
            limit: 1, offset: 0, descending: true, ct: ct);

        var latest = settings.FirstOrDefault();
        if (latest is null)
            return null;

        return await AssembleProfileAsync(latest, ct);
    }

    /// <inheritdoc />
    public async Task<Profile?> GetProfileByIdAsync(string id, CancellationToken ct = default)
    {
        // Try legacy ID first
        var settings = await _therapyRepo.GetByLegacyIdAsync(id, ct);

        // Fall back to GUID lookup
        if (settings is null && Guid.TryParse(id, out var guid))
            settings = await _therapyRepo.GetByIdAsync(guid, ct);

        if (settings is null)
            return null;

        return await AssembleProfileAsync(settings, ct);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Profile>> GetProfilesAsync(
        int count = 10, int skip = 0, CancellationToken ct = default)
    {
        var settingsList = await _therapyRepo.GetAsync(
            from: null, to: null, device: null, source: null,
            limit: count, offset: skip, descending: true, ct: ct);

        var profiles = new List<Profile>();
        foreach (var settings in settingsList)
        {
            var profile = await AssembleProfileAsync(settings, ct);
            profiles.Add(profile);
        }

        return profiles;
    }

    /// <inheritdoc />
    public async Task<long> CountProfilesAsync(string? find = null, CancellationToken ct = default)
    {
        return await _therapyRepo.CountAsync(from: null, to: null, ct: ct);
    }

    /// <summary>
    /// Assembles a <see cref="Profile"/> from a <see cref="TherapySettings"/> record and its
    /// correlated schedule siblings, loading them by <see cref="TherapySettings.CorrelationId"/>
    /// when available, or by profile name as a fallback.
    /// </summary>
    private async Task<Profile> AssembleProfileAsync(TherapySettings settings, CancellationToken ct)
    {
        BasalSchedule? basal;
        CarbRatioSchedule? carbRatio;
        SensitivitySchedule? sensitivity;
        TargetRangeSchedule? targetRange;

        if (settings.CorrelationId.HasValue)
        {
            var correlationId = settings.CorrelationId.Value;

            var basalTask = _basalRepo.GetByCorrelationIdAsync(correlationId, ct);
            var carbRatioTask = _carbRatioRepo.GetByCorrelationIdAsync(correlationId, ct);
            var sensitivityTask = _sensitivityRepo.GetByCorrelationIdAsync(correlationId, ct);
            var targetRangeTask = _targetRangeRepo.GetByCorrelationIdAsync(correlationId, ct);

            await Task.WhenAll(basalTask, carbRatioTask, sensitivityTask, targetRangeTask);

            basal = basalTask.Result
                .FirstOrDefault(s => s.ProfileName == settings.ProfileName);
            carbRatio = carbRatioTask.Result
                .FirstOrDefault(s => s.ProfileName == settings.ProfileName);
            sensitivity = sensitivityTask.Result
                .FirstOrDefault(s => s.ProfileName == settings.ProfileName);
            targetRange = targetRangeTask.Result
                .FirstOrDefault(s => s.ProfileName == settings.ProfileName);
        }
        else
        {
            var basalTask = _basalRepo.GetByProfileNameAsync(settings.ProfileName, ct);
            var carbRatioTask = _carbRatioRepo.GetByProfileNameAsync(settings.ProfileName, ct);
            var sensitivityTask = _sensitivityRepo.GetByProfileNameAsync(settings.ProfileName, ct);
            var targetRangeTask = _targetRangeRepo.GetByProfileNameAsync(settings.ProfileName, ct);

            await Task.WhenAll(basalTask, carbRatioTask, sensitivityTask, targetRangeTask);

            basal = basalTask.Result.FirstOrDefault();
            carbRatio = carbRatioTask.Result.FirstOrDefault();
            sensitivity = sensitivityTask.Result.FirstOrDefault();
            targetRange = targetRangeTask.Result.FirstOrDefault();
        }

        var profileData = new ProfileData
        {
            Dia = settings.Dia,
            CarbsHr = settings.CarbsHr,
            Delay = settings.Delay,
            Timezone = settings.Timezone,
            Units = settings.Units,
            PerGIValues = settings.PerGIValues,
            CarbsHrHigh = settings.CarbsHrHigh,
            CarbsHrMedium = settings.CarbsHrMedium,
            CarbsHrLow = settings.CarbsHrLow,
            DelayHigh = settings.DelayHigh,
            DelayMedium = settings.DelayMedium,
            DelayLow = settings.DelayLow,
            Basal = MapScheduleEntries(basal?.Entries),
            CarbRatio = MapScheduleEntries(carbRatio?.Entries),
            Sens = MapScheduleEntries(sensitivity?.Entries),
            TargetLow = MapTargetLow(targetRange?.Entries),
            TargetHigh = MapTargetHigh(targetRange?.Entries),
        };

        // Extract the profile record-level ID from the legacy ID prefix (before the colon)
        var profileId = settings.LegacyId?.Contains(':') == true
            ? settings.LegacyId.Split(':')[0]
            : settings.LegacyId ?? settings.Id.ToString();

        return new Profile
        {
            Id = profileId,
            DefaultProfile = settings.ProfileName,
            StartDate = settings.StartDate ?? settings.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Mills = settings.Mills,
            CreatedAt = settings.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Units = settings.Units ?? "mg/dL",
            EnteredBy = settings.EnteredBy,
            LoopSettings = settings.LoopSettings,
            IsExternallyManaged = settings.IsExternallyManaged,
            Store = new Dictionary<string, ProfileData>
            {
                [settings.ProfileName] = profileData
            }
        };
    }

    private static List<TimeValue> MapScheduleEntries(List<ScheduleEntry>? entries)
    {
        if (entries is null or { Count: 0 })
            return [];

        return entries.Select(e => new TimeValue
        {
            Time = e.Time,
            Value = e.Value,
            TimeAsSeconds = e.TimeAsSeconds,
        }).ToList();
    }

    private static List<TimeValue> MapTargetLow(List<TargetRangeEntry>? entries)
    {
        if (entries is null or { Count: 0 })
            return [];

        return entries.Select(e => new TimeValue
        {
            Time = e.Time,
            Value = e.Low,
            TimeAsSeconds = e.TimeAsSeconds,
        }).ToList();
    }

    private static List<TimeValue> MapTargetHigh(List<TargetRangeEntry>? entries)
    {
        if (entries is null or { Count: 0 })
            return [];

        return entries.Select(e => new TimeValue
        {
            Time = e.Time,
            Value = e.High,
            TimeAsSeconds = e.TimeAsSeconds,
        }).ToList();
    }
}
