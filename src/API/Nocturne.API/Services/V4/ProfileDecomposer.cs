using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Decomposes legacy <see cref="Profile"/> records into five v4 granular models per named store entry:
/// <see cref="V4Models.TherapySettings"/>, <see cref="V4Models.BasalSchedule"/>,
/// <see cref="V4Models.CarbRatioSchedule"/>, <see cref="V4Models.SensitivitySchedule"/>, and
/// <see cref="V4Models.TargetRangeSchedule"/>.
/// Iterates through the <see cref="Profile.Store"/> dictionary and uses a composite
/// <c>LegacyId</c> of the form <c>"{profileId}:{storeName}"</c> for idempotent upserts, one
/// create-or-update round per table for the whole batch.
/// </summary>
/// <seealso cref="IProfileDecomposer"/>
/// <seealso cref="IDecomposer{T}"/>
public class ProfileDecomposer : DecomposerBase, IProfileDecomposer, IDecomposer<Profile>
{
    private readonly ITherapySettingsRepository _therapySettingsRepo;
    private readonly IBasalScheduleRepository _basalScheduleRepo;
    private readonly ICarbRatioScheduleRepository _carbRatioScheduleRepo;
    private readonly ISensitivityScheduleRepository _sensitivityScheduleRepo;
    private readonly ITargetRangeScheduleRepository _targetRangeScheduleRepo;
    private readonly ITempBasalRateReresolver _tempBasalRates;

    /// <param name="therapySettingsRepo">Repository for <see cref="V4Models.TherapySettings"/> records.</param>
    /// <param name="basalScheduleRepo">Repository for <see cref="V4Models.BasalSchedule"/> records.</param>
    /// <param name="carbRatioScheduleRepo">Repository for <see cref="V4Models.CarbRatioSchedule"/> records.</param>
    /// <param name="sensitivityScheduleRepo">Repository for <see cref="V4Models.SensitivitySchedule"/> records.</param>
    /// <param name="targetRangeScheduleRepo">Repository for <see cref="V4Models.TargetRangeSchedule"/> records.</param>
    /// <param name="tempBasalRates">Re-resolves the percent temp basals a written basal schedule covers.</param>
    /// <param name="logger">Logger instance for this decomposer.</param>
    public ProfileDecomposer(
        ITherapySettingsRepository therapySettingsRepo,
        IBasalScheduleRepository basalScheduleRepo,
        ICarbRatioScheduleRepository carbRatioScheduleRepo,
        ISensitivityScheduleRepository sensitivityScheduleRepo,
        ITargetRangeScheduleRepository targetRangeScheduleRepo,
        ITempBasalRateReresolver tempBasalRates,
        ILogger<ProfileDecomposer> logger)
        : base(logger)
    {
        _therapySettingsRepo = therapySettingsRepo;
        _basalScheduleRepo = basalScheduleRepo;
        _carbRatioScheduleRepo = carbRatioScheduleRepo;
        _sensitivityScheduleRepo = sensitivityScheduleRepo;
        _targetRangeScheduleRepo = targetRangeScheduleRepo;
        _tempBasalRates = tempBasalRates;
    }

    /// <inheritdoc />
    public Task<V4Models.DecompositionResult> DecomposeAsync(Profile profile, WriteOrigin origin, CancellationToken ct = default)
        => DecomposeBatchAsync([profile], origin, ct);

    /// <inheritdoc />
    /// <remarks>
    /// No system attribution here (see <see cref="DecomposerBase.SystemAttributedBatchWrites"/>).
    /// <para>
    /// The therapy settings row anchors each group's correlation id, and the four schedules are
    /// stamped with whatever it resolves to. Reading it back rather than reusing the minted id is
    /// what keeps an unchanged re-upsert free of writes, and stamping the schedules from it is
    /// what keeps the group whole: the five tables are written in five separate saves, so a
    /// sibling lost to a cancelled sync is recreated on the next one, and it has to rejoin the
    /// group rather than fork it. ProfileProjectionService loads the schedules by this id. A refused
    /// anchor leaves its schedules nothing to converge on, so they are not written: under the minted
    /// id they would fork off the settings row they belong to rather than join it.
    /// </para>
    /// </remarks>
    public async Task<V4Models.DecompositionResult> DecomposeBatchAsync(
        IReadOnlyList<Profile> profiles, WriteOrigin origin, CancellationToken ct = default)
    {
        var changed = new List<(string ProfileName, DateTime Start)>();
        var result = await DecomposeCoreAsync(profiles, origin, changed, ct);
        await _tempBasalRates.AfterSchedulesWrittenAsync(changed, ct);
        return result;
    }

    /// <summary>
    /// Whether <paramref name="storeName"/> has the <c>{name}@@@@@{mills}</c> shape of a Profile
    /// Switch's inline profile. Nightscout only ever builds such a store in memory
    /// (lib/profilefunctions.js); one arriving stored is a pre-activation instance's switch snapshot,
    /// which is the switch's <see cref="V4Models.ProfileActivation"/> snapshot here and no profile.
    /// </summary>
    internal static bool IsProfileSwitchStoreName(string storeName) => ProfileSwitchStoreName.IsMatch(storeName);

    private static readonly Regex ProfileSwitchStoreName = new("@@@@@[0-9]+$", RegexOptions.Compiled);

    /// <param name="changed">Receives the store versions whose basal schedule or settings changed.</param>
    private async Task<V4Models.DecompositionResult> DecomposeCoreAsync(
        IReadOnlyList<Profile> profiles,
        WriteOrigin origin,
        List<(string ProfileName, DateTime Start)> changed,
        CancellationToken ct)
    {
        var firstMinted = Guid.CreateVersion7();
        var result = new V4Models.DecompositionResult { CorrelationId = firstMinted };

        var entries = new List<StoreEntry>();
        var first = true;
        foreach (var profile in profiles)
        {
            if (profile.Store.Count == 0)
            {
                Logger.LogWarning("Profile {Id} has no store entries, skipping decomposition", profile.Id);
                continue;
            }

            // One id per profile, as the single-profile path always minted, so a profile's stores
            // created together share it and two profiles created together do not.
            var minted = first ? firstMinted : Guid.CreateVersion7();
            first = false;
            foreach (var (storeName, profileData) in profile.Store)
            {
                if (IsProfileSwitchStoreName(storeName))
                {
                    Logger.LogDebug("Skipped profile switch store {StoreName} of profile {Id}", storeName, profile.Id);
                    continue;
                }

                entries.Add(new StoreEntry(profile, storeName, profileData, $"{profile.Id}:{storeName}", minted));
            }
        }

        if (entries.Count == 0)
            return result;

        var claim = await ResolveDefaultClaimAsync(entries, ct);
        var storedDefaults = claim.Claims
            ? []
            : (await _therapySettingsRepo.GetDefaultsAsync(ct))
                .Select(d => d.LegacyId).OfType<string>().ToHashSet(StringComparer.Ordinal);

        var writtenSince = DateTime.UtcNow;
        var anchors = await _therapySettingsRepo.BulkUpsertByLegacyIdAsync(
            entries.Select(e => MapToTherapySettings(
                e.Profile, e.Data, e.StoreName, e.LegacyId,
                claim.Claims ? e.LegacyId == claim.LegacyId : storedDefaults.Contains(e.LegacyId),
                e.MintedCorrelationId)).ToList(),
            origin, preserveStoredCorrelationId: true, ct);
        Record(result, anchors);
        changed.AddRange(ChangedSince(anchors, writtenSince));

        if (claim.Claims)
        {
            var claimedId = claim.LegacyId is not null && anchors.Outcomes.TryGetValue(claim.LegacyId, out var claimed)
                ? claimed.Record.Id
                : (Guid?)null;
            await _therapySettingsRepo.SetDefaultAsync(claimedId, ct);
        }

        var groups = entries
            .Where(e => anchors.Outcomes.ContainsKey(e.LegacyId))
            .Select(e => (Entry: e, CorrelationId: anchors.Outcomes[e.LegacyId].Record.CorrelationId ?? e.MintedCorrelationId))
            .ToList();
        if (groups.Count < entries.Count)
        {
            Logger.LogDebug(
                "Skipped schedules for {Count} profile store(s) whose therapy settings were not written: identity held by a deleted row, or the legacy id repeated in the batch",
                entries.Count - groups.Count);
        }

        if (groups.Count == 0)
            return result;

        var basal = await _basalScheduleRepo.BulkUpsertByLegacyIdAsync(
            groups.Select(g => MapToBasalSchedule(g.Entry.Profile, g.Entry.Data, g.Entry.StoreName, g.Entry.LegacyId, g.CorrelationId)).ToList(),
            origin, ct: ct);
        Record(result, basal);
        Record(result, await _carbRatioScheduleRepo.BulkUpsertByLegacyIdAsync(
            groups.Select(g => MapToCarbRatioSchedule(g.Entry.Profile, g.Entry.Data, g.Entry.StoreName, g.Entry.LegacyId, g.CorrelationId)).ToList(),
            origin, ct: ct));
        Record(result, await _sensitivityScheduleRepo.BulkUpsertByLegacyIdAsync(
            groups.Select(g => MapToSensitivitySchedule(g.Entry.Profile, g.Entry.Data, g.Entry.StoreName, g.Entry.LegacyId, g.CorrelationId)).ToList(),
            origin, ct: ct));
        Record(result, await _targetRangeScheduleRepo.BulkUpsertByLegacyIdAsync(
            groups.Select(g => MapToTargetRangeSchedule(g.Entry.Profile, g.Entry.Data, g.Entry.StoreName, g.Entry.LegacyId, g.CorrelationId, Logger)).ToList(),
            origin, ct: ct));

        changed.AddRange(ChangedSince(basal, writtenSince));

        return result;
    }

    /// <summary>
    /// Whether this batch settles the tenant's default profile (see <see cref="V4Models.TherapySettings.IsDefault"/>),
    /// and which store it settles on.
    /// </summary>
    /// <remarks>
    /// Nightscout reads the default from the newest profile document (<c>startDate</c> desc, then
    /// <c>_id</c> desc) as <c>store[defaultProfile]</c>, an exact key lookup. So only the batch's
    /// newest document claims, and only when nothing already stored is newer; a document whose
    /// <c>defaultProfile</c> names none of its stores claims with no store, leaving no default, as
    /// Nightscout finds none, and so does a claim whose store row was not written. A batch that does
    /// not claim keeps the stored flags as they are, so re-syncing an older document neither takes
    /// the default nor drops a user's choice.
    /// </remarks>
    private async Task<DefaultClaim> ResolveDefaultClaimAsync(List<StoreEntry> entries, CancellationToken ct)
    {
        var newest = entries
            .Select(e => e.Profile)
            .Distinct()
            .OrderByDescending(VersionStart)
            .ThenByDescending(p => p.Id, StringComparer.Ordinal)
            .First();

        var storedNewest = await _therapySettingsRepo.GetNewestDocumentRowAsync(ct);
        var claims = storedNewest is null
            || storedNewest.Timestamp <= VersionStart(newest)
            || storedNewest.LegacyId?.StartsWith($"{newest.Id}:", StringComparison.Ordinal) == true;
        if (!claims)
            return new DefaultClaim(false, null);

        var store = newest.Store.Keys.FirstOrDefault(k => string.Equals(k, newest.DefaultProfile, StringComparison.Ordinal));
        return new DefaultClaim(true, store is null ? null : $"{newest.Id}:{store}");
    }

    /// <summary>The outcome of <see cref="ResolveDefaultClaimAsync"/>.</summary>
    private sealed record DefaultClaim(bool Claims, string? LegacyId);

    /// <summary>One named profile inside one legacy profile document, with the id minted for that document.</summary>
    private sealed record StoreEntry(
        Profile Profile, string StoreName, ProfileData Data, string LegacyId, Guid MintedCorrelationId);

    /// <summary>The upserted versions a write created or changed, rather than left as they were.</summary>
    private static IEnumerable<(string ProfileName, DateTime Start)> ChangedSince<TRecord>(
        LegacyUpsertBatch<TRecord> batch, DateTime writtenSince)
        where TRecord : class, V4Models.IV4Record, V4Models.IProfileScoped =>
        batch.Outcomes.Values
            .Where(o => o.Created || o.Record.ModifiedAt >= writtenSince)
            .Select(o => (o.Record.ProfileName, o.Record.Timestamp));

    private static void Record<TRecord>(
        V4Models.DecompositionResult result, LegacyUpsertBatch<TRecord> batch)
        where TRecord : class, V4Models.IV4Record
    {
        result.SkippedDeleted += batch.SkippedDeleted;
        foreach (var outcome in batch.Outcomes.Values)
        {
            if (outcome.Created)
                result.CreatedRecords.Add(outcome.Record);
            else
                result.UpdatedRecords.Add(outcome.Record);
        }
    }

    #region Mapping Methods

    /// <summary>
    /// The instant every version decomposed from <paramref name="profile"/> starts: its <c>startDate</c>,
    /// else its <c>mills</c>, else its <c>created_at</c>, else the epoch.
    /// </summary>
    /// <remarks>
    /// <c>startDate</c> leads as in Nightscout, which sets a document's <c>mills</c> from it on load
    /// (profilefunctions.js), so an editor that changes it and echoes the <c>mills</c> it read moves
    /// the document.
    /// </remarks>
    internal static DateTime VersionStart(Profile profile)
    {
        if (UploaderTimestamp.ParseUtcDateTime(profile.StartDate) is { } start)
            return start;

        // NIGHTSCOUT-COMPAT: Nightscout gives a document with no readable startDate no time (NaN), so it
        // is never in force at a time and is read only when it heads the list. Its mills or created_at
        // place it where its client meant it instead. A zone-less startDate is read as UTC where
        // Nightscout reads it in the server's zone, which is UTC in its container image.
        if (profile.Mills != 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(profile.Mills).UtcDateTime;

        return UploaderTimestamp.ParseUtcDateTime(profile.CreatedAt) ?? DateTime.UnixEpoch;
    }

    internal static V4Models.TherapySettings MapToTherapySettings(
        Profile profile,
        ProfileData profileData,
        string storeName,
        string legacyId,
        bool isDefault,
        Guid? correlationId)
    {
        return new V4Models.TherapySettings
        {
            LegacyId = legacyId,
            Timestamp = VersionStart(profile),
            ProfileName = storeName,
            Timezone = profileData.Timezone,
            Units = profileData.Units ?? profile.Units,
            Dia = profileData.Dia,
            CarbsHr = profileData.CarbsHr,
            Delay = profileData.Delay,
            PerGIValues = profileData.PerGIValues,
            CarbsHrHigh = profileData.CarbsHrHigh,
            CarbsHrMedium = profileData.CarbsHrMedium,
            CarbsHrLow = profileData.CarbsHrLow,
            DelayHigh = profileData.DelayHigh,
            DelayMedium = profileData.DelayMedium,
            DelayLow = profileData.DelayLow,
            LoopSettings = profile.LoopSettings,
            IsAPNSProduction = profile.IsAPNSProduction,
            IsDefault = isDefault,
            DocumentDefaultProfile = profile.DefaultProfile,
            EnteredBy = profile.EnteredBy,
            IsExternallyManaged = profile.IsExternallyManaged,
            StartDate = profile.StartDate,
            Device = profile.EnteredBy,
            CorrelationId = correlationId,
        };
    }

    internal static V4Models.BasalSchedule MapToBasalSchedule(
        Profile profile,
        ProfileData profileData,
        string storeName,
        string legacyId,
        Guid? correlationId)
    {
        return new V4Models.BasalSchedule
        {
            LegacyId = legacyId,
            Timestamp = VersionStart(profile),
            ProfileName = storeName,
            Entries = ConvertTimeValues(profileData.Basal),
            Device = profile.EnteredBy,
            CorrelationId = correlationId,
        };
    }

    internal static V4Models.CarbRatioSchedule MapToCarbRatioSchedule(
        Profile profile,
        ProfileData profileData,
        string storeName,
        string legacyId,
        Guid? correlationId)
    {
        return new V4Models.CarbRatioSchedule
        {
            LegacyId = legacyId,
            Timestamp = VersionStart(profile),
            ProfileName = storeName,
            Entries = ConvertTimeValues(profileData.CarbRatio),
            Device = profile.EnteredBy,
            CorrelationId = correlationId,
        };
    }

    internal static V4Models.SensitivitySchedule MapToSensitivitySchedule(
        Profile profile,
        ProfileData profileData,
        string storeName,
        string legacyId,
        Guid? correlationId)
    {
        return new V4Models.SensitivitySchedule
        {
            LegacyId = legacyId,
            Timestamp = VersionStart(profile),
            ProfileName = storeName,
            Entries = ConvertSensitivityValues(profileData.Sens, profileData.Units ?? profile.Units),
            Device = profile.EnteredBy,
            CorrelationId = correlationId,
        };
    }

    internal static V4Models.TargetRangeSchedule MapToTargetRangeSchedule(
        Profile profile,
        ProfileData profileData,
        string storeName,
        string legacyId,
        Guid? correlationId,
        ILogger? logger = null)
    {
        var entries = MergeTargets(
            profileData.TargetLow, profileData.TargetHigh, profileData.Units ?? profile.Units, out var undone);
        if (undone)
        {
            logger?.LogWarning(
                "Profile store {StoreName} ({LegacyId}) is labelled mmol but its targets are mg/dL: stored them as mg/dL. Its ISF is stored as uploaded and may be 18 times too large",
                storeName,
                legacyId);
        }

        return new V4Models.TargetRangeSchedule
        {
            LegacyId = legacyId,
            Timestamp = VersionStart(profile),
            ProfileName = storeName,
            Entries = entries,
            Device = profile.EnteredBy,
            CorrelationId = correlationId,
        };
    }

    #endregion

    #region Conversion Helpers

    /// <summary>
    /// Converts a list of legacy <see cref="TimeValue"/> entries into v4 <see cref="V4Models.ScheduleEntry"/> records,
    /// normalising each value's time representation via <see cref="TimeValue.EnsureTimeAsSeconds"/>.
    /// </summary>
    /// <param name="timeValues">The legacy time-value list (e.g. basal, carb-ratio, or sensitivity entries).</param>
    /// <returns>A list of <see cref="V4Models.ScheduleEntry"/> with <c>Time</c>, <c>Value</c>, and <c>TimeAsSeconds</c> populated.</returns>
    internal static List<V4Models.ScheduleEntry> ConvertTimeValues(List<TimeValue> timeValues)
    {
        return timeValues.Select(tv =>
        {
            tv.EnsureTimeAsSeconds();
            return new V4Models.ScheduleEntry
            {
                Time = tv.Time,
                Value = tv.Value,
                TimeAsSeconds = tv.TimeAsSeconds,
            };
        }).ToList();
    }

    /// <summary>
    /// Converts insulin sensitivity (ISF) time-values into v4 <see cref="V4Models.ScheduleEntry"/>
    /// records, normalising mmol profiles to mg/dL per unit.
    /// </summary>
    /// <remarks>
    /// Unlike basal (U/hr) and carb-ratio (g/U), ISF is glucose-unit-dependent: a mmol profile
    /// stores it as mmol/L per unit. <see cref="Services.Profiles.Resolvers.SensitivityResolver"/>
    /// and its consumers treat the schedule as mg/dL per unit (its default is 50), so mmol values
    /// are converted here at write time rather than each reader guessing.
    /// </remarks>
    /// <param name="timeValues">The sensitivity time-value entries from the profile store.</param>
    /// <param name="units">The profile's glucose units ("mg/dl" or "mmol"); mmol values are converted to mg/dL.</param>
    /// <returns>A list of <see cref="V4Models.ScheduleEntry"/> with <c>Value</c> in mg/dL per unit.</returns>
    internal static List<V4Models.ScheduleEntry> ConvertSensitivityValues(List<TimeValue> timeValues, string? units)
    {
        var toMgdl = IsMmol(units)
            ? (Func<double, double>)(value => Math.Round(value * GlucoseConstants.MgdlPerMmol))
            : value => value;

        return timeValues.Select(tv =>
        {
            tv.EnsureTimeAsSeconds();
            return new V4Models.ScheduleEntry
            {
                Time = tv.Time,
                Value = toMgdl(tv.Value),
                TimeAsSeconds = tv.TimeAsSeconds,
            };
        }).ToList();
    }

    /// <summary>
    /// Merges separate low- and high-target <see cref="TimeValue"/> lists into a single list of
    /// <see cref="V4Models.TargetRangeEntry"/> records. When a matching high entry is not found for a
    /// given time slot, the low value is used as the high value as a safe fallback.
    /// </summary>
    /// <remarks>
    /// Nightscout profile target ranges are stored in the profile's display units, but the V4
    /// <see cref="V4Models.TargetRangeEntry"/> contract is mg/dL — every reader (alert engine,
    /// <c>TargetRangeResolver</c>, report statistics) compares against mg/dL. mmol profiles are
    /// therefore normalised to mg/dL here at write time, so no reader has to know the source units.
    /// A mmol store whose targets come out above any real target is a client's stale copy carrying
    /// mg/dL labelled mmol, and is stored with that conversion undone
    /// (<see cref="UndoRepeatedTargetConversions"/>). Its ISF is not: a correct mmol ISF can convert to
    /// the same numbers.
    /// </remarks>
    /// <param name="lows">The low-target time-value entries from the profile store.</param>
    /// <param name="highs">The high-target time-value entries from the profile store.</param>
    /// <param name="units">The profile's glucose units ("mg/dl" or "mmol"); mmol values are converted to mg/dL.</param>
    /// <returns>A merged list of <see cref="V4Models.TargetRangeEntry"/> with <c>Low</c> and <c>High</c> fields in mg/dL.</returns>
    internal static List<V4Models.TargetRangeEntry> MergeTargets(List<TimeValue> lows, List<TimeValue> highs, string? units) =>
        MergeTargets(lows, highs, units, out _);

    /// <param name="undone">Whether an extra conversion was undone.</param>
    internal static List<V4Models.TargetRangeEntry> MergeTargets(
        List<TimeValue> lows, List<TimeValue> highs, string? units, out bool undone)
    {
        var merged = MergeTargetsConverted(lows, highs, units);
        var targets = IsMmol(units) ? UndoRepeatedTargetConversions(merged) : merged;
        undone = !ReferenceEquals(targets, merged);
        return targets;
    }

    private static List<V4Models.TargetRangeEntry> MergeTargetsConverted(List<TimeValue> lows, List<TimeValue> highs, string? units)
    {
        var toMgdl = IsMmol(units)
            ? (Func<double, double>)(value => Math.Round(value * GlucoseConstants.MgdlPerMmol))
            : value => value;
        var highLookup = highs.ToDictionary(h => h.Time, h => h.Value);

        return lows.Select(low =>
        {
            low.EnsureTimeAsSeconds();
            return new V4Models.TargetRangeEntry
            {
                Time = low.Time,
                Low = toMgdl(low.Value),
                High = toMgdl(highLookup.TryGetValue(low.Time, out var high) ? high : low.Value),
                TimeAsSeconds = low.TimeAsSeconds,
            };
        }).ToList();
    }

    /// <summary>
    /// Whether a profile's units string denotes mmol/L (matching the forms Nightscout profiles use).
    /// </summary>
    internal static bool IsMmol(string? units) =>
        units is not null
        && (units.Equals("mmol", StringComparison.OrdinalIgnoreCase)
            || units.Equals("mmol/l", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Converts a stored mg/dL glucose value back to the profile's display units: the inverse of the
    /// write-time normalisation in <see cref="ConvertSensitivityValues"/> and <see cref="MergeTargets"/>.
    /// mmol values are rounded to one decimal place, the precision Nightscout profiles use.
    /// </summary>
    internal static double FromMgdl(double mgdl, string? units) =>
        IsMmol(units) ? Math.Round(mgdl / GlucoseConstants.MgdlPerMmol, 1) : mgdl;

    /// <summary>The highest stored mg/dL target one mmol conversion of a real target reaches (22.2 mmol/L).</summary>
    internal const double MaxSingleConversionMgdl = 400;

    private const int MaxRepeatedConversions = 4;

    /// <summary>
    /// Mmol targets that took the mmol-to-mg/dL conversion more than once, undone: <c>round(v / 18.0182)</c>
    /// per extra conversion while every target exceeds <see cref="MaxSingleConversionMgdl"/>, each step
    /// only when every value is <c>round(m * 18.0182)</c> of a whole <c>m</c>. Migration
    /// <c>RepairDoubleConvertedMmolProfiles</c> applies the same rule to stored rows.
    /// </summary>
    /// <returns>The targets undone, or as given when there is nothing it can undo.</returns>
    internal static List<V4Models.TargetRangeEntry> UndoRepeatedTargetConversions(
        List<V4Models.TargetRangeEntry> targets)
    {
        var current = targets;
        var steps = 0;
        while (current.Count > 0
               && current.Min(t => Math.Min(t.Low, t.High)) > MaxSingleConversionMgdl
               && steps < MaxRepeatedConversions)
        {
            if (!current.All(t => IsConversionImage(t.Low) && IsConversionImage(t.High)))
                return targets;

            current = current.Select(t => new V4Models.TargetRangeEntry
            {
                Time = t.Time,
                Low = UndoConversion(t.Low),
                High = UndoConversion(t.High),
                TimeAsSeconds = t.TimeAsSeconds,
            }).ToList();
            steps++;
        }

        return steps > 0 && current.Max(t => Math.Max(t.Low, t.High)) <= MaxSingleConversionMgdl
            ? current
            : targets;
    }

    private static double UndoConversion(double mgdl) =>
        Math.Round(mgdl / GlucoseConstants.MgdlPerMmol, MidpointRounding.AwayFromZero);

    private static bool IsConversionImage(double mgdl) =>
        Math.Abs(UndoConversion(mgdl) * GlucoseConstants.MgdlPerMmol - mgdl) <= 0.5;

    #endregion

    /// <inheritdoc />
    /// <remarks>
    /// A row that does not carry the <c>"{_id}:{storeName}"</c> key (a v4 row served under its own id)
    /// is first keyed under it, with its schedules, so the upsert updates it in place rather than
    /// inserting a copy beside it. Dropped stores are soft-deleted rather than the whole document
    /// deleted and rewritten: the recreation guard would then refuse every store it rewrote.
    /// </remarks>
    public async Task<bool> ReplaceDocumentAsync(Profile profile, WriteOrigin origin, CancellationToken ct = default)
    {
        var stores = profile.Store.Keys.Where(store => !IsProfileSwitchStoreName(store)).ToList();
        if (stores.Count == 0)
            return false;

        var documentId = profile.Id!;
        var stored = new List<(string LegacyId, string ProfileName, DateTime Start)>();
        foreach (var row in await _therapySettingsRepo.GetDocumentRowsAsync(documentId, ct))
        {
            stored.Add((
                IsKeyedUnder(row, documentId) ? row.LegacyId! : await KeyUnderDocumentAsync(row, documentId, origin, ct),
                row.ProfileName,
                row.Timestamp));
        }

        var changed = new List<(string ProfileName, DateTime Start)>();
        var result = await DecomposeCoreAsync([profile], origin, changed, ct);
        var written = result.CreatedRecords.Concat(result.UpdatedRecords).OfType<V4Models.TherapySettings>().Count();
        if (written < stores.Count)
        {
            await _tempBasalRates.AfterSchedulesWrittenAsync(changed, ct);
            return false;
        }

        var carried = stores.Select(store => $"{documentId}:{store}").ToHashSet(StringComparer.Ordinal);
        foreach (var dropped in stored.Where(row => !carried.Contains(row.LegacyId)))
        {
            await _therapySettingsRepo.DeleteByLegacyIdAsync(dropped.LegacyId, origin, ct);
            await _basalScheduleRepo.DeleteByLegacyIdAsync(dropped.LegacyId, origin, ct);
            await _carbRatioScheduleRepo.DeleteByLegacyIdAsync(dropped.LegacyId, origin, ct);
            await _sensitivityScheduleRepo.DeleteByLegacyIdAsync(dropped.LegacyId, origin, ct);
            await _targetRangeScheduleRepo.DeleteByLegacyIdAsync(dropped.LegacyId, origin, ct);
            changed.Add((dropped.ProfileName, dropped.Start));
        }

        await _tempBasalRates.AfterSchedulesWrittenAsync(changed, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> DeleteDocumentAsync(string documentId, WriteOrigin origin, CancellationToken ct = default)
    {
        var rows = await _therapySettingsRepo.GetDocumentRowsAsync(documentId, ct);
        var unkeyed = rows.Where(row => !IsKeyedUnder(row, documentId)).ToList();

        var deleted = await DeleteByLegacyIdAsync(documentId, origin, ct);
        foreach (var row in unkeyed)
        {
            deleted += await DeleteSiblingsAsync(_basalScheduleRepo, row, origin, ct);
            deleted += await DeleteSiblingsAsync(_carbRatioScheduleRepo, row, origin, ct);
            deleted += await DeleteSiblingsAsync(_sensitivityScheduleRepo, row, origin, ct);
            deleted += await DeleteSiblingsAsync(_targetRangeScheduleRepo, row, origin, ct);
            await _therapySettingsRepo.DeleteAsync(row.Id, origin, ct);
            deleted++;
        }

        if (deleted > 0)
            await _tempBasalRates.AfterSchedulesWrittenAsync(rows.Select(r => (r.ProfileName, r.Timestamp)).ToList(), ct);
        return deleted;
    }

    private static bool IsKeyedUnder(V4Models.TherapySettings row, string documentId) =>
        row.LegacyId?.StartsWith(documentId + ":", StringComparison.Ordinal) == true;

    /// <summary>
    /// Keys an unkeyed settings row, and the schedules <see cref="SiblingsAsync"/> finds for it, under
    /// <c>"{documentId}:{profileName}"</c>.
    /// </summary>
    /// <returns>The legacy id the row now carries.</returns>
    private async Task<string> KeyUnderDocumentAsync(
        V4Models.TherapySettings row, string documentId, WriteOrigin origin, CancellationToken ct)
    {
        var legacyId = $"{documentId}:{row.ProfileName}";
        await KeySiblingsAsync(_basalScheduleRepo, row, legacyId, origin, ct);
        await KeySiblingsAsync(_carbRatioScheduleRepo, row, legacyId, origin, ct);
        await KeySiblingsAsync(_sensitivityScheduleRepo, row, legacyId, origin, ct);
        await KeySiblingsAsync(_targetRangeScheduleRepo, row, legacyId, origin, ct);
        row.LegacyId = legacyId;
        await _therapySettingsRepo.UpdateAsync(row.Id, row, origin, ct);
        return legacyId;
    }

    private static async Task KeySiblingsAsync<TRecord>(
        IProfileScopedRepository<TRecord> repository, V4Models.TherapySettings row, string legacyId,
        WriteOrigin origin, CancellationToken ct)
        where TRecord : class, V4Models.IV4Record, V4Models.IProfileScoped
    {
        foreach (var sibling in await SiblingsAsync(repository, row, ct))
        {
            sibling.LegacyId = legacyId;
            await repository.UpdateAsync(sibling.Id, sibling, origin, ct);
        }
    }

    private static async Task<int> DeleteSiblingsAsync<TRecord>(
        IProfileScopedRepository<TRecord> repository, V4Models.TherapySettings row,
        WriteOrigin origin, CancellationToken ct)
        where TRecord : class, V4Models.IV4Record, V4Models.IProfileScoped
    {
        var siblings = await SiblingsAsync(repository, row, ct);
        foreach (var sibling in siblings)
            await repository.DeleteAsync(sibling.Id, origin, ct);
        return siblings.Count;
    }

    /// <summary>
    /// The schedules that belong to an unkeyed settings row and to no other: those sharing its
    /// correlation id and profile name, or its exact legacy id. A schedule the projection reaches only
    /// by profile name may be shared with another row, so it is left alone.
    /// </summary>
    private static async Task<List<TRecord>> SiblingsAsync<TRecord>(
        IProfileScopedRepository<TRecord> repository, V4Models.TherapySettings row, CancellationToken ct)
        where TRecord : class, V4Models.IV4Record, V4Models.IProfileScoped
    {
        var siblings = row.CorrelationId is { } correlationId
            ? (await repository.GetByCorrelationIdAsync(correlationId, ct))
                .Where(s => s.ProfileName == row.ProfileName)
                .ToList()
            : [];

        if (!string.IsNullOrEmpty(row.LegacyId)
            && await repository.GetByLegacyIdAsync(row.LegacyId, ct) is { } byLegacyId
            && byLegacyId.ProfileName == row.ProfileName
            && siblings.All(s => s.Id != byLegacyId.Id))
        {
            siblings.Add(byLegacyId);
        }

        return siblings;
    }

    /// <inheritdoc />
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default)
    {
        var prefix = legacyId + ":";
        var deleted = 0;

        deleted += await _therapySettingsRepo.DeleteByLegacyIdPrefixAsync(prefix, origin, ct);
        deleted += await _basalScheduleRepo.DeleteByLegacyIdPrefixAsync(prefix, origin, ct);
        deleted += await _carbRatioScheduleRepo.DeleteByLegacyIdPrefixAsync(prefix, origin, ct);
        deleted += await _sensitivityScheduleRepo.DeleteByLegacyIdPrefixAsync(prefix, origin, ct);
        deleted += await _targetRangeScheduleRepo.DeleteByLegacyIdPrefixAsync(prefix, origin, ct);

        if (deleted > 0)
            Logger.LogDebug("Deleted {Count} V4 records for legacy profile {LegacyId}", deleted, legacyId);

        return deleted;
    }
}
