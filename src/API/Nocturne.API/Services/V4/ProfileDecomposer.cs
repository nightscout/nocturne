using Microsoft.Extensions.Logging;
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
public class ProfileDecomposer : IProfileDecomposer, IDecomposer<Profile>
{
    private readonly ITherapySettingsRepository _therapySettingsRepo;
    private readonly IBasalScheduleRepository _basalScheduleRepo;
    private readonly ICarbRatioScheduleRepository _carbRatioScheduleRepo;
    private readonly ISensitivityScheduleRepository _sensitivityScheduleRepo;
    private readonly ITargetRangeScheduleRepository _targetRangeScheduleRepo;
    private readonly ILogger<ProfileDecomposer> _logger;

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
    {
        _therapySettingsRepo = therapySettingsRepo;
        _basalScheduleRepo = basalScheduleRepo;
        _carbRatioScheduleRepo = carbRatioScheduleRepo;
        _sensitivityScheduleRepo = sensitivityScheduleRepo;
        _targetRangeScheduleRepo = targetRangeScheduleRepo;
        _logger = logger;
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
            _logger.LogWarning("Profile {Id} has no store entries, skipping decomposition", profile.Id);
            return result;
        }

        // No SystemAuditScope here: profiles persist ONLY as these five granular records,
        // so on the HTTP path (v1/v3 profile create/update) their audit rows are the entire
        // mutation trail for a user's profile edit. Connector re-syncs are suppressed by the
        // sync scope's system audit context instead, and byte-identical re-upserts diff to
        // empty (bookkeeping columns are [AuditIgnored]) and are skipped.
        foreach (var (storeName, profileData) in profile.Store)
        {
            var legacyId = $"{profile.Id}:{storeName}";
            var isDefault = string.Equals(storeName, profile.DefaultProfile, StringComparison.OrdinalIgnoreCase);

            await DecomposeTherapySettingsAsync(profile, profileData, storeName, legacyId, isDefault, result, origin, ct);
            await DecomposeBasalScheduleAsync(profile, profileData, storeName, legacyId, result, origin, ct);
            await DecomposeCarbRatioScheduleAsync(profile, profileData, storeName, legacyId, result, origin, ct);
            await DecomposeSensitivityScheduleAsync(profile, profileData, storeName, legacyId, result, origin, ct);
            await DecomposeTargetRangeScheduleAsync(profile, profileData, storeName, legacyId, result, origin, ct);
        }

        return result;
    }

    #region Decomposition Methods

    private async Task DecomposeTherapySettingsAsync(
        Profile profile,
        ProfileData profileData,
        string storeName,
        string legacyId,
        bool isDefault,
        V4Models.DecompositionResult result,
        WriteOrigin origin, CancellationToken ct)
    {
        var existing = await _therapySettingsRepo.GetByLegacyIdAsync(legacyId, ct);
        var model = MapToTherapySettings(profile, profileData, storeName, legacyId, isDefault, result.CorrelationId);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _therapySettingsRepo.UpdateAsync(existing.Id, model, origin, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing TherapySettings {Id} from legacy profile {LegacyId}", existing.Id, legacyId);
        }
        else
        {
            var created = await _therapySettingsRepo.CreateAsync(model, origin, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created TherapySettings from legacy profile {LegacyId}", legacyId);
        }
    }

    private async Task DecomposeBasalScheduleAsync(
        Profile profile,
        ProfileData profileData,
        string storeName,
        string legacyId,
        V4Models.DecompositionResult result,
        WriteOrigin origin, CancellationToken ct)
    {
        var existing = await _basalScheduleRepo.GetByLegacyIdAsync(legacyId, ct);
        var model = MapToBasalSchedule(profile, profileData, storeName, legacyId, result.CorrelationId);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _basalScheduleRepo.UpdateAsync(existing.Id, model, origin, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing BasalSchedule {Id} from legacy profile {LegacyId}", existing.Id, legacyId);
        }
        else
        {
            var created = await _basalScheduleRepo.CreateAsync(model, origin, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created BasalSchedule from legacy profile {LegacyId}", legacyId);
        }
    }

    private async Task DecomposeCarbRatioScheduleAsync(
        Profile profile,
        ProfileData profileData,
        string storeName,
        string legacyId,
        V4Models.DecompositionResult result,
        WriteOrigin origin, CancellationToken ct)
    {
        var existing = await _carbRatioScheduleRepo.GetByLegacyIdAsync(legacyId, ct);
        var model = MapToCarbRatioSchedule(profile, profileData, storeName, legacyId, result.CorrelationId);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _carbRatioScheduleRepo.UpdateAsync(existing.Id, model, origin, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing CarbRatioSchedule {Id} from legacy profile {LegacyId}", existing.Id, legacyId);
        }
        else
        {
            var created = await _carbRatioScheduleRepo.CreateAsync(model, origin, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created CarbRatioSchedule from legacy profile {LegacyId}", legacyId);
        }
    }

    private async Task DecomposeSensitivityScheduleAsync(
        Profile profile,
        ProfileData profileData,
        string storeName,
        string legacyId,
        V4Models.DecompositionResult result,
        WriteOrigin origin, CancellationToken ct)
    {
        var existing = await _sensitivityScheduleRepo.GetByLegacyIdAsync(legacyId, ct);
        var model = MapToSensitivitySchedule(profile, profileData, storeName, legacyId, result.CorrelationId);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _sensitivityScheduleRepo.UpdateAsync(existing.Id, model, origin, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing SensitivitySchedule {Id} from legacy profile {LegacyId}", existing.Id, legacyId);
        }
        else
        {
            var created = await _sensitivityScheduleRepo.CreateAsync(model, origin, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created SensitivitySchedule from legacy profile {LegacyId}", legacyId);
        }
    }

    private async Task DecomposeTargetRangeScheduleAsync(
        Profile profile,
        ProfileData profileData,
        string storeName,
        string legacyId,
        V4Models.DecompositionResult result,
        WriteOrigin origin, CancellationToken ct)
    {
        var existing = await _targetRangeScheduleRepo.GetByLegacyIdAsync(legacyId, ct);
        var model = MapToTargetRangeSchedule(profile, profileData, storeName, legacyId, result.CorrelationId);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _targetRangeScheduleRepo.UpdateAsync(existing.Id, model, origin, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing TargetRangeSchedule {Id} from legacy profile {LegacyId}", existing.Id, legacyId);
        }
        else
        {
            var created = await _targetRangeScheduleRepo.CreateAsync(model, origin, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created TargetRangeSchedule from legacy profile {LegacyId}", legacyId);
        }
    }

    #endregion

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
            ? (Func<double, double>)(value => Math.Round(value * MgdlPerMmol))
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
    /// mg/dL per mmol/L. Matches the factor the V4 glucose models use (<see cref="V4Models.SensorGlucose"/> et al.).
    /// </summary>
    private const double MgdlPerMmol = 18.0182;

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
            ? (Func<double, double>)(value => Math.Round(value * MgdlPerMmol))
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
            _logger.LogDebug("Deleted {Count} V4 records for legacy profile {LegacyId}", deleted, legacyId);

        return deleted;
    }
}
