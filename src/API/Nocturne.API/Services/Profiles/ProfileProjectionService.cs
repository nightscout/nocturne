using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;

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
    public async Task<Profile?> GetCurrentProfileAsync(CancellationToken ct = default) =>
        (await GetProfilesAsync(count: 1, skip: 0, ct: ct)).FirstOrDefault();

    /// <inheritdoc />
    public async Task<Profile?> GetProfileByIdAsync(string id, CancellationToken ct = default) =>
        await ResolveDocumentIdAsync(id, ct) is { } documentId
        && await _therapyRepo.GetDocumentRowsAsync(documentId, ct) is { Count: > 0 } rows
            ? await AssembleDocumentAsync(rows, ct)
            : null;

    /// <inheritdoc />
    /// <remarks>
    /// Exact keys are tried before the served <see cref="MongoObjectId.Coerce"/> forms, so a stored
    /// ObjectId wins over a prefix or hash match.
    /// </remarks>
    public async Task<string?> ResolveDocumentIdAsync(string id, CancellationToken ct = default)
    {
        var settings = await _therapyRepo.GetByLegacyIdAsync(id, ct);

        if (settings is null && Guid.TryParse(id, out var guid))
            settings = await _therapyRepo.GetByIdAsync(guid, ct);

        if (settings is not null)
            return TherapySettings.DocumentIdOf(settings);

        if (await _therapyRepo.GetDocumentRowsAsync(id, ct) is { Count: > 0 } rows)
            return TherapySettings.DocumentIdOf(rows[0]);

        if (!MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
            return null;

        if (await _therapyRepo.GetByGuidRangeAsync(low, high, ct) is { } ranged)
            return TherapySettings.DocumentIdOf(ranged);

        return (await _therapyRepo.GetDocumentIdsAsync(ct)).FirstOrDefault(d => MongoObjectId.Coerce(d) == id);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Profile>> GetProfilesAsync(
        int count = 10, int skip = 0, bool descending = true, CancellationToken ct = default)
    {
        var profiles = new List<Profile>();
        foreach (var rows in await _therapyRepo.GetDocumentsAsync(count, skip, descending, ct))
            profiles.Add(await AssembleDocumentAsync(rows, ct));

        return profiles;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A document is stamped by the newest of its rows, so it is found through whichever row moved:
    /// each of the five tables pages through its own history page, and every schedule row leads back
    /// to the settings it can be assembled into (<see cref="OwnersAsync"/>). Past the horizon, the
    /// earliest last millisecond among the tables whose page filled, a table holds rows not yet
    /// read, so only documents stamped at or before it are known to be complete. When none are, no
    /// document is stamped in (cursor, horizon] and the read resumes from the horizon. A deleted
    /// settings row re-sends its document's live stores at the delete's stamp, since the client knows
    /// the document by one identifier, or, when no store of it is live, a deleted document with
    /// <c>isValid: false</c>. A schedule row, live or deleted, only leads to the live settings that
    /// own it. Profile-switch snapshots are not documents and are never delivered.
    /// </remarks>
    public async Task<ModifiedSincePage<Profile>> GetProfilesModifiedSinceAsync(
        long cursorMills, int limit, CancellationToken ct = default)
    {
        var cursor = cursorMills;
        using var committed = HistoryHorizon.Share();

        while (true)
        {
            var settingsPage = await _therapyRepo.GetModifiedSinceAsync(cursor, limit, ct);
            var basal = await _basalRepo.GetModifiedSinceAsync(cursor, limit, ct);
            var carbRatio = await _carbRatioRepo.GetModifiedSinceAsync(cursor, limit, ct);
            var sensitivity = await _sensitivityRepo.GetModifiedSinceAsync(cursor, limit, ct);
            var targetRange = await _targetRangeRepo.GetModifiedSinceAsync(cursor, limit, ct);

            var horizon = Horizon(
                limit,
                Records(settingsPage),
                Records(basal),
                Records(carbRatio),
                Records(sensitivity),
                Records(targetRange));
            List<IV4Record> schedules =
            [
                .. Records(basal), .. Records(carbRatio), .. Records(sensitivity), .. Records(targetRange),
            ];

            var candidates = new Dictionary<Guid, HistoryRecord<TherapySettings>>();
            foreach (var settings in settingsPage)
                candidates.TryAdd(settings.Record.Id, settings);
            foreach (var settings in await OwnersAsync(schedules, ct))
                candidates.TryAdd(settings.Id, new HistoryRecord<TherapySettings>(settings, Deleted: false));

            var stamped = new Dictionary<string, Profile>(StringComparer.Ordinal);
            foreach (var document in candidates.Values
                         .Where(c => !TherapySettings.IsProfileSwitchSnapshot(c.Record))
                         .GroupBy(c => TherapySettings.DocumentIdOf(c.Record), StringComparer.Ordinal))
            {
                var live = await _therapyRepo.GetDocumentRowsAsync(document.Key, ct);
                Profile profile;
                if (live.Count > 0)
                {
                    profile = await AssembleDocumentAsync(live, ct);
                    var deletedAt = document.Where(c => c.Deleted).Select(c => (long?)Stamp(c.Record)).Max();
                    profile.SrvModified = Math.Max(profile.SrvModified ?? 0, deletedAt ?? 0);
                }
                else
                {
                    profile = await AssembleDocumentAsync(document.Select(c => c.Record).ToList(), ct);
                    profile.IsValid = false;
                }

                // A document is assembled from its live rows, which the history reads did not cut.
                if (profile.SrvModified is { } modified
                    && modified > cursor
                    && (horizon is null || modified <= horizon)
                    && committed.Admits(modified))
                    stamped[document.Key] = profile;
            }

            if (stamped.Count == 0)
            {
                if (horizon is null)
                    return new ModifiedSincePage<Profile>([], null);

                cursor = horizon.Value;
                continue;
            }

            var ordered = stamped
                .OrderBy(p => p.Value.SrvModified)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Value)
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
    private static IReadOnlyList<IV4Record> Records<T>(IReadOnlyList<HistoryRecord<T>> page)
        where T : IV4Record =>
        page.Select(r => (IV4Record)r.Record).ToList();

    private static long? Horizon(int limit, params IReadOnlyList<IV4Record>[] pages) =>
        pages
            .Where(page => page.Count >= limit)
            .Select(page => (long?)Stamp(page[^1]))
            .Min();

    private static long Stamp(IV4Record record) =>
        new DateTimeOffset(record.ModifiedAt, TimeSpan.Zero).ToUnixTimeMilliseconds();

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
    public Task<long> CountProfilesAsync(string? find = null, CancellationToken ct = default) =>
        _therapyRepo.CountDocumentsAsync(ct);

    /// <summary>
    /// One legacy profile document, with a store per row of <paramref name="rows"/>.
    /// </summary>
    /// <remarks>
    /// The document names the tenant's <see cref="TherapySettings.IsDefault"/> row's store when it holds
    /// that row, as <see cref="Resolvers.ProfileStoreTimeline"/> reads it; else its recorded
    /// <see cref="TherapySettings.DocumentDefaultProfile"/>; else, for a row decomposed before that was
    /// recorded, its newest store. It reads its document-level fields off its first store's row. Its
    /// <c>startDate</c> is the stored one only where its versions start there (a row decomposed before
    /// <c>startDate</c> led may record another), so a client that saves it back does not move them.
    /// </remarks>
    private async Task<Profile> AssembleDocumentAsync(IReadOnlyList<TherapySettings> rows, CancellationToken ct)
    {
        var ordered = rows
            .OrderByDescending(r => r.IsDefault)
            .ThenByDescending(r => r.Timestamp)
            .ThenBy(r => r.ProfileName, StringComparer.Ordinal)
            .ToList();
        var head = ordered[0];

        var store = new Dictionary<string, ProfileData>();
        var srvModified = long.MinValue;
        foreach (var settings in ordered)
        {
            var (data, modified) = await AssembleStoreAsync(settings, ct);
            store[settings.ProfileName] = data;
            srvModified = Math.Max(srvModified, modified);
        }

        return new Profile
        {
            Id = TherapySettings.DocumentIdOf(head),
            DefaultProfile = head.IsDefault
                ? head.ProfileName
                : rows.Select(r => r.DocumentDefaultProfile).FirstOrDefault(d => d is not null) ?? head.ProfileName,
            StartDate = UploaderTimestamp.ParseUtcDateTime(head.StartDate) == head.Timestamp
                ? head.StartDate
                : head.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Mills = head.Mills,
            CreatedAt = head.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            SrvModified = srvModified,
            Units = head.Units ?? "mg/dL",
            EnteredBy = head.EnteredBy,
            LoopSettings = head.LoopSettings,
            IsAPNSProduction = head.IsAPNSProduction,
            IsExternallyManaged = head.IsExternallyManaged,
            Store = store,
        };
    }

    /// <summary>
    /// The store <paramref name="settings"/> and its schedule siblings make, and the newest write
    /// stamp among them.
    /// </summary>
    private async Task<(ProfileData Data, long Modified)> AssembleStoreAsync(
        TherapySettings settings, CancellationToken ct)
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
            Sens = MapScheduleEntries(sensitivity.Result?.Entries, settings.Units),
            TargetLow = MapTargetLow(targetRange.Result?.Entries, settings.Units),
            TargetHigh = MapTargetHigh(targetRange.Result?.Entries, settings.Units),
        };

        var modified = new IV4Record?[] { settings, basal.Result, carbRatio.Result, sensitivity.Result, targetRange.Result }
            .OfType<IV4Record>()
            .Max(Stamp);

        return (profileData, modified);
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

    /// <param name="glucoseUnits">
    /// The profile's glucose units, for the one schedule (ISF) whose values are mg/dL per unit in V4 but
    /// display units in the legacy document. Left <c>null</c> for basal and carb ratio, which are not
    /// glucose-unit-dependent.
    /// </param>
    private static List<TimeValue> MapScheduleEntries(List<ScheduleEntry>? entries, string? glucoseUnits = null)
    {
        if (entries is null or { Count: 0 })
            return [];

        return entries.Select(e => new TimeValue
        {
            Time = e.Time,
            Value = ProfileDecomposer.FromMgdl(e.Value, glucoseUnits),
            TimeAsSeconds = e.TimeAsSeconds,
        }).ToList();
    }

    private static List<TimeValue> MapTargetLow(List<TargetRangeEntry>? entries, string? glucoseUnits)
    {
        if (entries is null or { Count: 0 })
            return [];

        return entries.Select(e => new TimeValue
        {
            Time = e.Time,
            Value = ProfileDecomposer.FromMgdl(e.Low, glucoseUnits),
            TimeAsSeconds = e.TimeAsSeconds,
        }).ToList();
    }

    private static List<TimeValue> MapTargetHigh(List<TargetRangeEntry>? entries, string? glucoseUnits)
    {
        if (entries is null or { Count: 0 })
            return [];

        return entries.Select(e => new TimeValue
        {
            Time = e.Time,
            Value = ProfileDecomposer.FromMgdl(e.High, glucoseUnits),
            TimeAsSeconds = e.TimeAsSeconds,
        }).ToList();
    }
}
