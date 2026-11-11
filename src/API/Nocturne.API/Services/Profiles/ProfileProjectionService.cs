using Nocturne.API.Services.Profiles.Resolvers;
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
            ? (await AssembleDocumentsAsync([rows], ct))[0]
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
        int count = 10, int skip = 0, bool descending = true, CancellationToken ct = default) =>
        await AssembleDocumentsAsync(await _therapyRepo.GetDocumentsAsync(count, skip, descending, ct), ct);

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
    /// own it.
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

            var grouped = candidates.Values
                .GroupBy(c => TherapySettings.DocumentIdOf(c.Record), StringComparer.Ordinal)
                .ToList();
            var liveRows = await _therapyRepo.GetDocumentRowsAsync(grouped.Select(g => g.Key).ToList(), ct);

            var documents = new List<(string Key, IReadOnlyList<TherapySettings> Rows, bool Live, long? DeletedAt)>();
            foreach (var document in grouped)
            {
                documents.Add(liveRows.TryGetValue(document.Key, out var live) && live.Count > 0
                    ? (document.Key, live, true,
                        document.Where(c => c.Deleted).Select(c => (long?)Stamp(c.Record)).Max())
                    : (document.Key, document.Select(c => c.Record).ToList(), false, null));
            }

            var profiles = await AssembleDocumentsAsync(documents.Select(d => d.Rows).ToList(), ct);
            var stamped = new Dictionary<string, Profile>(StringComparer.Ordinal);
            foreach (var (document, profile) in documents.Zip(profiles))
            {
                if (document.Live)
                    profile.SrvModified = Math.Max(profile.SrvModified ?? 0, document.DeletedAt ?? 0);
                else
                    profile.IsValid = false;

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

    private static IReadOnlyList<IV4Record> Records<T>(IReadOnlyList<HistoryRecord<T>> page)
        where T : IV4Record =>
        page.Select(r => (IV4Record)r.Record).ToList();

    /// <summary>
    /// The earliest last millisecond among the <paramref name="pages"/> that came back full, or
    /// <c>null</c> when every table was read to its end.
    /// </summary>
    private static long? Horizon(int limit, params IReadOnlyList<IV4Record>[] pages) =>
        pages
            .Where(page => page.Count >= limit)
            .Select(page => (long?)Stamp(page[^1]))
            .Min();

    private static long Stamp(IV4Record record) =>
        record.ModifiedAt.ToMills();

    /// <summary>
    /// Every settings record <see cref="SchedulesForAsync{TRecord}"/> could pair with one of
    /// <paramref name="schedules"/>: the same correlation and store name, the same legacy id, or,
    /// for settings without a correlation id, the same store name. A superset is harmless, since
    /// each candidate is assembled and judged by its own stamp.
    /// </summary>
    private async Task<List<TherapySettings>> OwnersAsync(
        IReadOnlyList<IV4Record> schedules, CancellationToken ct)
    {
        var namesByCorrelation = schedules
            .Where(s => s.CorrelationId is not null)
            .GroupBy(s => s.CorrelationId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(s => ((IProfileScoped)s).ProfileName).ToHashSet());

        var owners = (await _therapyRepo.GetByCorrelationIdsAsync(namesByCorrelation.Keys, ct))
            .Where(t => t.CorrelationId is { } id && namesByCorrelation[id].Contains(t.ProfileName))
            .ToList();

        owners.AddRange(await _therapyRepo.GetByLegacyIdsAsync(
            schedules.Select(s => s.LegacyId).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!), ct));

        owners.AddRange((await _therapyRepo.GetByProfileNamesAsync(
                schedules.Select(s => ((IProfileScoped)s).ProfileName), ct))
            .Where(t => t.CorrelationId is null));

        return owners;
    }

    /// <inheritdoc />
    public Task<long> CountProfilesAsync(string? find = null, CancellationToken ct = default) =>
        _therapyRepo.CountDocumentsAsync(ct);

    /// <summary>
    /// One legacy profile document per entry of <paramref name="documents"/>, with a store per row,
    /// reading each schedule kind for every row at once (<see cref="SchedulesForAsync{TRecord}"/>).
    /// </summary>
    /// <remarks>
    /// A document names the store <see cref="Resolvers.ProfileStoreTimeline"/> reads as its default, so
    /// v1 and v3 agree with every therapy resolver. A document that does not hold that store (a row
    /// written on its own through the V4 API, which keeps the default of the document before it) or
    /// that is not in the tenant's timeline (a deleted one) names the default it reads on its own. It
    /// reads its document-level fields off its first store's row. Its
    /// <c>startDate</c> is the stored one only where its versions start there (a row decomposed before
    /// <c>startDate</c> led may record another), so a client that saves it back does not move them.
    /// </remarks>
    private async Task<List<Profile>> AssembleDocumentsAsync(
        IReadOnlyList<IReadOnlyList<TherapySettings>> documents, CancellationToken ct)
    {
        var rows = documents.SelectMany(d => d).ToList();
        var basal = SchedulesForAsync(_basalRepo, rows, ct);
        var carbRatio = SchedulesForAsync(_carbRatioRepo, rows, ct);
        var sensitivity = SchedulesForAsync(_sensitivityRepo, rows, ct);
        var targetRange = SchedulesForAsync(_targetRangeRepo, rows, ct);
        var storeIndex = _therapyRepo.GetStoreIndexAsync(ct);

        await Task.WhenAll(basal, carbRatio, sensitivity, targetRange, storeIndex);

        var schedules = new PageSchedules(basal.Result, carbRatio.Result, sensitivity.Result, targetRange.Result);
        var timeline = ProfileStoreTimeline.From(storeIndex.Result);
        return documents.Select(d => AssembleDocument(d, schedules, timeline)).ToList();
    }

    private static Profile AssembleDocument(
        IReadOnlyList<TherapySettings> rows, PageSchedules schedules, ProfileStoreTimeline timeline)
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
            var (data, modified) = AssembleStore(settings, schedules);
            store[settings.ProfileName] = data;
            srvModified = Math.Max(srvModified, modified);
        }

        var id = TherapySettings.DocumentIdOf(head);
        var defaultProfile = timeline.DocumentDefaultOf(id) is { } inForce && store.ContainsKey(inForce)
            ? inForce
            : ProfileStoreTimeline.From(rows.Select(StoreRow).ToList()).DocumentDefaultOf(id)!;

        return new Profile
        {
            Id = id,
            DefaultProfile = defaultProfile,
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

    private static ProfileStoreRow StoreRow(TherapySettings settings) =>
        new(
            TherapySettings.DocumentIdOf(settings), settings.LegacyId is not null, settings.ProfileName,
            settings.Timestamp, settings.IsDefault, settings.DocumentDefaultProfile);

    /// <summary>
    /// The store <paramref name="settings"/> and its schedule siblings make, and the newest write
    /// stamp among them.
    /// </summary>
    private static (ProfileData Data, long Modified) AssembleStore(TherapySettings settings, PageSchedules schedules)
    {
        var basal = schedules.Basal.GetValueOrDefault(settings);
        var carbRatio = schedules.CarbRatio.GetValueOrDefault(settings);
        var sensitivity = schedules.Sensitivity.GetValueOrDefault(settings);
        var targetRange = schedules.TargetRange.GetValueOrDefault(settings);

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
            Sens = MapScheduleEntries(sensitivity?.Entries, settings.Units),
            TargetLow = MapTargetLow(targetRange?.Entries, settings.Units),
            TargetHigh = MapTargetHigh(targetRange?.Entries, settings.Units),
        };

        var modified = new IV4Record?[] { settings, basal, carbRatio, sensitivity, targetRange }
            .OfType<IV4Record>()
            .Max(Stamp);

        return (profileData, modified);
    }

    /// <summary>Each kind's schedule per settings row of a page, keyed by the row instance.</summary>
    private sealed record PageSchedules(
        IReadOnlyDictionary<TherapySettings, BasalSchedule> Basal,
        IReadOnlyDictionary<TherapySettings, CarbRatioSchedule> CarbRatio,
        IReadOnlyDictionary<TherapySettings, SensitivitySchedule> Sensitivity,
        IReadOnlyDictionary<TherapySettings, TargetRangeSchedule> TargetRange);

    /// <summary>
    /// The one schedule of its kind belonging to each of <paramref name="rows"/> that has one: the
    /// correlated sibling when the settings record carries a correlation id, otherwise the newest row
    /// filed under the profile name, otherwise the row stored under the legacy id every sibling in the
    /// group shares. At most one query per rule, whatever the number of rows.
    /// </summary>
    /// <remarks>
    /// The store name is compared as a column rather than read off the legacy id: a profile name may
    /// itself contain a colon, so the <c>"{profileId}:{storeName}"</c> composite is not unambiguously
    /// parseable. Without the legacy-id step a schedule the correlation id does not reach (the
    /// siblings are written in separate transactions, so a group can be read mid-convergence) leaves
    /// an AID consumer of v1/v3 profile with an empty basal schedule rather than an error.
    /// </remarks>
    private static async Task<Dictionary<TherapySettings, TRecord>> SchedulesForAsync<TRecord>(
        IProfileScopedRepository<TRecord> repository,
        IReadOnlyList<TherapySettings> rows,
        CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped
    {
        var correlated = (await repository.GetByCorrelationIdsAsync(
                rows.Select(r => r.CorrelationId).OfType<Guid>(), ct))
            .ToLookup(s => s.CorrelationId);
        var newest = (await repository.GetNewestByProfileNamesAsync(
                rows.Where(r => r.CorrelationId is null).Select(r => r.ProfileName), ct))
            .ToDictionary(s => s.ProfileName, StringComparer.Ordinal);

        var found = new Dictionary<TherapySettings, TRecord>(ReferenceEqualityComparer.Instance);
        var unmatched = new List<TherapySettings>();
        foreach (var settings in rows)
        {
            var schedule = settings.CorrelationId is { } correlationId
                ? correlated[correlationId].FirstOrDefault(s => s.ProfileName == settings.ProfileName)
                : newest.GetValueOrDefault(settings.ProfileName);

            if (schedule is not null)
                found[settings] = schedule;
            else if (!string.IsNullOrEmpty(settings.LegacyId))
                unmatched.Add(settings);
        }

        if (unmatched.Count == 0)
            return found;

        var byLegacyId = new Dictionary<string, TRecord>(StringComparer.Ordinal);
        foreach (var candidate in await repository.GetByLegacyIdsAsync(unmatched.Select(s => s.LegacyId!), ct))
            byLegacyId.TryAdd(candidate.LegacyId!, candidate);

        foreach (var settings in unmatched)
        {
            if (byLegacyId.TryGetValue(settings.LegacyId!, out var candidate)
                && candidate.ProfileName == settings.ProfileName)
                found[settings] = candidate;
        }

        return found;
    }

    /// <param name="entries">
    /// The V4 schedule's entries; <c>null</c> or empty maps to an empty schedule.
    /// </param>
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
