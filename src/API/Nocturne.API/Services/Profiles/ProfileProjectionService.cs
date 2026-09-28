using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
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
    /// <remarks>
    /// A profile is stamped by the newest of its rows, so it is found through whichever row moved:
    /// each of the five tables pages through its own history page, and every schedule row leads back
    /// to the settings it can be assembled into (<see cref="OwnersAsync"/>). Past the horizon — the
    /// earliest last millisecond among the tables whose page filled — a table holds rows not yet
    /// read, so only profiles stamped at or before it are known to be complete. When none are, no
    /// profile is stamped in (cursor, horizon] and the read resumes from the horizon.
    /// </remarks>
    public async Task<ModifiedSincePage<Profile>> GetProfilesModifiedSinceAsync(
        long cursorMills, int limit, CancellationToken ct = default)
    {
        var cursor = cursorMills;

        while (true)
        {
            var settingsPage = await _therapyRepo.GetModifiedSinceAsync(cursor, limit, ct);
            var basal = await _basalRepo.GetModifiedSinceAsync(cursor, limit, ct);
            var carbRatio = await _carbRatioRepo.GetModifiedSinceAsync(cursor, limit, ct);
            var sensitivity = await _sensitivityRepo.GetModifiedSinceAsync(cursor, limit, ct);
            var targetRange = await _targetRangeRepo.GetModifiedSinceAsync(cursor, limit, ct);

            var horizon = Horizon(limit, settingsPage, basal, carbRatio, sensitivity, targetRange);
            List<IV4Record> schedules = [.. basal, .. carbRatio, .. sensitivity, .. targetRange];

            var candidates = new Dictionary<Guid, TherapySettings>();
            foreach (var settings in settingsPage.Concat(await OwnersAsync(schedules, ct)))
                candidates.TryAdd(settings.Id, settings);

            var stamped = new List<(Profile Profile, Guid SettingsId)>();
            foreach (var settings in candidates.Values)
            {
                var profile = await AssembleProfileAsync(settings, ct);
                if (profile.SrvModified > cursor && (horizon is null || profile.SrvModified <= horizon))
                    stamped.Add((profile, settings.Id));
            }

            if (stamped.Count == 0)
            {
                if (horizon is null)
                    return new ModifiedSincePage<Profile>([], null);

                cursor = horizon.Value;
                continue;
            }

            var ordered = stamped
                .OrderBy(p => p.Profile.SrvModified)
                .ThenBy(p => p.SettingsId)
                .Select(p => p.Profile)
                .ToList();

            var page = ordered.Count <= limit
                ? ordered
                : ordered.TakeWhile((p, i) => i < limit || p.SrvModified == ordered[limit - 1].SrvModified).ToList();

            return new ModifiedSincePage<Profile>(page, page[^1].SrvModified);
        }
    }

    /// <summary>
    /// The earliest last millisecond among the <paramref name="pages"/> that came back full, or
    /// <c>null</c> when every table was read to its end.
    /// </summary>
    private static long? Horizon(int limit, params IReadOnlyList<IV4Record>[] pages) =>
        pages
            .Where(page => page.Count >= limit)
            .Select(page => (long?)new DateTimeOffset(page[^1].ModifiedAt, TimeSpan.Zero).ToUnixTimeMilliseconds())
            .Min();

    /// <summary>
    /// Every settings record <see cref="ScheduleForProfileAsync{TRecord}"/> could pair with one of
    /// <paramref name="schedules"/>: the same correlation and store name, the same legacy id, or —
    /// for settings without a correlation id — the same store name. A superset is harmless, since
    /// each candidate is assembled and judged by its own stamp.
    /// </summary>
    private async Task<List<TherapySettings>> OwnersAsync(
        IReadOnlyList<IV4Record> schedules, CancellationToken ct)
    {
        var owners = new List<TherapySettings>();

        foreach (var group in schedules
                     .Where(s => s.CorrelationId is not null)
                     .GroupBy(s => s.CorrelationId!.Value))
        {
            var names = group.Select(s => ((IProfileScoped)s).ProfileName).ToHashSet();
            owners.AddRange((await _therapyRepo.GetByCorrelationIdAsync(group.Key, ct))
                .Where(t => names.Contains(t.ProfileName)));
        }

        foreach (var legacyId in schedules
                     .Select(s => s.LegacyId)
                     .Where(id => !string.IsNullOrEmpty(id))
                     .Distinct())
        {
            if (await _therapyRepo.GetByLegacyIdAsync(legacyId!, ct) is { } settings)
                owners.Add(settings);
        }

        foreach (var name in schedules.Select(s => ((IProfileScoped)s).ProfileName).Distinct())
        {
            owners.AddRange((await _therapyRepo.GetByProfileNameAsync(name, ct))
                .Where(t => t.CorrelationId is null));
        }

        return owners;
    }

    /// <inheritdoc />
    public async Task<long> CountProfilesAsync(string? find = null, CancellationToken ct = default)
    {
        return await _therapyRepo.CountAsync(from: null, to: null, ct: ct);
    }

    /// <summary>
    /// Assembles a <see cref="Profile"/> from a <see cref="TherapySettings"/> record and its
    /// schedule siblings.
    /// </summary>
    private async Task<Profile> AssembleProfileAsync(TherapySettings settings, CancellationToken ct)
    {
        var basal = ScheduleForProfileAsync(_basalRepo, settings, ct);
        var carbRatio = ScheduleForProfileAsync(_carbRatioRepo, settings, ct);
        var sensitivity = ScheduleForProfileAsync(_sensitivityRepo, settings, ct);
        var targetRange = ScheduleForProfileAsync(_targetRangeRepo, settings, ct);

        await Task.WhenAll(basal, carbRatio, sensitivity, targetRange);

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
            Basal = MapScheduleEntries(basal.Result?.Entries),
            CarbRatio = MapScheduleEntries(carbRatio.Result?.Entries),
            Sens = MapScheduleEntries(sensitivity.Result?.Entries),
            TargetLow = MapTargetLow(targetRange.Result?.Entries),
            TargetHigh = MapTargetHigh(targetRange.Result?.Entries),
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
            SrvModified = new DateTimeOffset(
                    new IV4Record?[] { settings, basal.Result, carbRatio.Result, sensitivity.Result, targetRange.Result }
                        .Max(r => r?.ModifiedAt ?? DateTime.MinValue),
                    TimeSpan.Zero)
                .ToUnixTimeMilliseconds(),
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

    /// <summary>
    /// The one schedule of its kind belonging to <paramref name="settings"/>: the correlated sibling
    /// when the settings record carries a correlation id, otherwise the newest row filed under the
    /// profile name, otherwise the row stored under the legacy id every sibling in the group shares.
    /// </summary>
    /// <remarks>
    /// The store name is compared as a column rather than read off the legacy id: a profile name may
    /// itself contain a colon, so the <c>"{profileId}:{storeName}"</c> composite is not unambiguously
    /// parseable. Without the legacy-id step a schedule the correlation id does not reach — the
    /// siblings are written in separate transactions, so a group can be read mid-convergence — leaves
    /// an AID consumer of v1/v3 profile with an empty basal schedule rather than an error.
    /// </remarks>
    private static async Task<TRecord?> ScheduleForProfileAsync<TRecord>(
        IProfileScopedRepository<TRecord> repository,
        TherapySettings settings,
        CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped
    {
        var found = settings.CorrelationId is { } correlationId
            ? (await repository.GetByCorrelationIdAsync(correlationId, ct))
                .FirstOrDefault(s => s.ProfileName == settings.ProfileName)
            : (await repository.GetByProfileNameAsync(settings.ProfileName, ct)).FirstOrDefault();

        if (found is not null || string.IsNullOrEmpty(settings.LegacyId))
            return found;

        var candidate = await repository.GetByLegacyIdAsync(settings.LegacyId, ct);
        return candidate is not null && candidate.ProfileName == settings.ProfileName ? candidate : null;
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
