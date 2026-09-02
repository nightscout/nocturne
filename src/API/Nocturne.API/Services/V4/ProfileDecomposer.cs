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
/// <c>LegacyId</c> of the form <c>"{profileId}:{storeName}"</c> for idempotent upserts.
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

    /// <param name="therapySettingsRepo">Repository for <see cref="V4Models.TherapySettings"/> records.</param>
    /// <param name="basalScheduleRepo">Repository for <see cref="V4Models.BasalSchedule"/> records.</param>
    /// <param name="carbRatioScheduleRepo">Repository for <see cref="V4Models.CarbRatioSchedule"/> records.</param>
    /// <param name="sensitivityScheduleRepo">Repository for <see cref="V4Models.SensitivitySchedule"/> records.</param>
    /// <param name="targetRangeScheduleRepo">Repository for <see cref="V4Models.TargetRangeSchedule"/> records.</param>
    /// <param name="logger">Logger instance for this decomposer.</param>
    public ProfileDecomposer(
        ITherapySettingsRepository therapySettingsRepo,
        IBasalScheduleRepository basalScheduleRepo,
        ICarbRatioScheduleRepository carbRatioScheduleRepo,
        ISensitivityScheduleRepository sensitivityScheduleRepo,
        ITargetRangeScheduleRepository targetRangeScheduleRepo,
        ILogger<ProfileDecomposer> logger)
        : base(logger)
    {
        _therapySettingsRepo = therapySettingsRepo;
        _basalScheduleRepo = basalScheduleRepo;
        _carbRatioScheduleRepo = carbRatioScheduleRepo;
        _sensitivityScheduleRepo = sensitivityScheduleRepo;
        _targetRangeScheduleRepo = targetRangeScheduleRepo;
    }

    /// <inheritdoc />
    public async Task<V4Models.DecompositionResult> DecomposeAsync(Profile profile, WriteOrigin origin, CancellationToken ct = default)
    {
        var result = new V4Models.DecompositionResult
        {
            CorrelationId = Guid.CreateVersion7()
        };

        if (profile.Store.Count == 0)
        {
            Logger.LogWarning("Profile {Id} has no store entries, skipping decomposition", profile.Id);
            return result;
        }

        // No system attribution here — there is no batch path to take it on (see
        // DecomposerBase.SystemAttributedBatchWrites): a profile write is a user's profile edit,
        // and byte-identical re-upserts diff to empty (bookkeeping columns are [AuditIgnored])
        // and are skipped.
        foreach (var (storeName, profileData) in profile.Store)
        {
            var legacyId = $"{profile.Id}:{storeName}";
            var isDefault = string.Equals(storeName, profile.DefaultProfile, StringComparison.OrdinalIgnoreCase);

            await UpsertByLegacyIdAsync(
                _therapySettingsRepo, legacyId,
                MapToTherapySettings(profile, profileData, storeName, legacyId, isDefault, result.CorrelationId),
                result, origin, ct);
            await UpsertByLegacyIdAsync(
                _basalScheduleRepo, legacyId,
                MapToBasalSchedule(profile, profileData, storeName, legacyId, result.CorrelationId),
                result, origin, ct);
            await UpsertByLegacyIdAsync(
                _carbRatioScheduleRepo, legacyId,
                MapToCarbRatioSchedule(profile, profileData, storeName, legacyId, result.CorrelationId),
                result, origin, ct);
            await UpsertByLegacyIdAsync(
                _sensitivityScheduleRepo, legacyId,
                MapToSensitivitySchedule(profile, profileData, storeName, legacyId, result.CorrelationId),
                result, origin, ct);
            await UpsertByLegacyIdAsync(
                _targetRangeScheduleRepo, legacyId,
                MapToTargetRangeSchedule(profile, profileData, storeName, legacyId, result.CorrelationId),
                result, origin, ct);
        }

        return result;
    }

    #region Mapping Methods

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
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(profile.Mills).UtcDateTime,
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
            IsDefault = isDefault,
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
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(profile.Mills).UtcDateTime,
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
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(profile.Mills).UtcDateTime,
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
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(profile.Mills).UtcDateTime,
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
        Guid? correlationId)
    {
        return new V4Models.TargetRangeSchedule
        {
            LegacyId = legacyId,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(profile.Mills).UtcDateTime,
            ProfileName = storeName,
            Entries = MergeTargets(profileData.TargetLow, profileData.TargetHigh, profileData.Units ?? profile.Units),
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
    /// </remarks>
    /// <param name="lows">The low-target time-value entries from the profile store.</param>
    /// <param name="highs">The high-target time-value entries from the profile store.</param>
    /// <param name="units">The profile's glucose units ("mg/dl" or "mmol"); mmol values are converted to mg/dL.</param>
    /// <returns>A merged list of <see cref="V4Models.TargetRangeEntry"/> with <c>Low</c> and <c>High</c> fields in mg/dL.</returns>
    internal static List<V4Models.TargetRangeEntry> MergeTargets(List<TimeValue> lows, List<TimeValue> highs, string? units)
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

    #endregion

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
