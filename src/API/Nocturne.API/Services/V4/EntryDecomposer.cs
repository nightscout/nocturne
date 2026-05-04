using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Decomposes legacy <see cref="Entry"/> records into v4 granular models.
/// Maps <see cref="Entry.Type"/> to the appropriate v4 type:
/// <c>sgv</c> → <see cref="SensorGlucose"/>,
/// <c>mbg</c> → <see cref="MeterGlucose"/>,
/// <c>cal</c> → <see cref="Calibration"/>.
/// Supports idempotent create-or-update via <c>LegacyId</c> matching.
/// </summary>
/// <seealso cref="IEntryDecomposer"/>
/// <seealso cref="IDecomposer{T}"/>
public class EntryDecomposer : IEntryDecomposer, IDecomposer<Entry>
{
    private readonly NocturneDbContext _dbContext;
    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly IMeterGlucoseRepository _meterGlucoseRepository;
    private readonly ICalibrationRepository _calibrationRepository;
    private readonly IGlucoseProcessingResolver _glucoseResolver;
    private readonly ILogger<EntryDecomposer> _logger;

    /// <param name="dbContext">EF Core context used to persist <see cref="DecompositionBatchEntity"/> records.</param>
    /// <param name="sensorGlucoseRepository">Repository for <see cref="SensorGlucose"/> records.</param>
    /// <param name="meterGlucoseRepository">Repository for <see cref="MeterGlucose"/> records.</param>
    /// <param name="calibrationRepository">Repository for <see cref="Calibration"/> records.</param>
    /// <param name="glucoseResolver">Resolves glucose processing type and smoothed/unsmoothed values from v1/v3 hints or source defaults.</param>
    /// <param name="logger">Logger instance for this decomposer.</param>
    public EntryDecomposer(
        NocturneDbContext dbContext,
        ISensorGlucoseRepository sensorGlucoseRepository,
        IMeterGlucoseRepository meterGlucoseRepository,
        ICalibrationRepository calibrationRepository,
        IGlucoseProcessingResolver glucoseResolver,
        ILogger<EntryDecomposer> logger)
    {
        _dbContext = dbContext;
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _meterGlucoseRepository = meterGlucoseRepository;
        _calibrationRepository = calibrationRepository;
        _glucoseResolver = glucoseResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DecompositionResult> DecomposeAsync(Entry entry, CancellationToken ct = default)
    {
        var batch = new DecompositionBatchEntity
        {
            TenantId = _dbContext.TenantId,
            Source = "entry_decomposer",
            SourceRecordId = entry.Id,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.DecompositionBatches.Add(batch);
        await _dbContext.SaveChangesAsync(ct);

        var result = new DecompositionResult
        {
            CorrelationId = batch.Id
        };

        var entryType = entry.Type?.ToLowerInvariant();

        switch (entryType)
        {
            case "sgv":
                await DecomposeSgvAsync(entry, result, ct);
                break;
            case "mbg":
                await DecomposeMbgAsync(entry, result, ct);
                break;
            case "cal":
                await DecomposeCalAsync(entry, result, ct);
                break;
            default:
                _logger.LogWarning("Unknown entry type '{Type}' for entry {Id}, skipping decomposition", entry.Type, entry.Id);
                break;
        }

        return result;
    }

    private async Task DecomposeSgvAsync(Entry entry, DecompositionResult result, CancellationToken ct)
    {
        var existing = entry.Id != null
            ? await _sensorGlucoseRepository.GetByLegacyIdAsync(entry.Id, ct)
            : null;

        var model = MapToSensorGlucose(entry, result.CorrelationId);

        // Extract glucose processing hints from v1/v3 additional properties
        string? gpHint = null;
        double? smoothedHint = null;
        double? unsmoothedHint = null;

        if (entry.AdditionalProperties is { } props)
        {
            if (TryGetString(props, "glucoseProcessing", out var gpStr))
                gpHint = gpStr;
            if (TryGetDouble(props, "smoothedMgdl", out var sm))
                smoothedHint = sm;
            if (TryGetDouble(props, "unsmoothedMgdl", out var um))
                unsmoothedHint = um;
        }

        await _glucoseResolver.ResolveAsync(model, gpHint, smoothedHint, unsmoothedHint, ct);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _sensorGlucoseRepository.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing SensorGlucose {Id} from legacy entry {LegacyId}", existing.Id, entry.Id);
        }
        else
        {
            var created = await _sensorGlucoseRepository.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created SensorGlucose from legacy entry {LegacyId}", entry.Id);
        }
    }

    private async Task DecomposeMbgAsync(Entry entry, DecompositionResult result, CancellationToken ct)
    {
        var existing = entry.Id != null
            ? await _meterGlucoseRepository.GetByLegacyIdAsync(entry.Id, ct)
            : null;

        var model = MapToMeterGlucose(entry, result.CorrelationId);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _meterGlucoseRepository.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing MeterGlucose {Id} from legacy entry {LegacyId}", existing.Id, entry.Id);
        }
        else
        {
            var created = await _meterGlucoseRepository.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created MeterGlucose from legacy entry {LegacyId}", entry.Id);
        }
    }

    private async Task DecomposeCalAsync(Entry entry, DecompositionResult result, CancellationToken ct)
    {
        var existing = entry.Id != null
            ? await _calibrationRepository.GetByLegacyIdAsync(entry.Id, ct)
            : null;

        var model = MapToCalibration(entry, result.CorrelationId);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _calibrationRepository.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing Calibration {Id} from legacy entry {LegacyId}", existing.Id, entry.Id);
        }
        else
        {
            var created = await _calibrationRepository.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created Calibration from legacy entry {LegacyId}", entry.Id);
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var deleted = 0;
        deleted += await _sensorGlucoseRepository.DeleteByLegacyIdAsync(legacyId, ct);
        deleted += await _meterGlucoseRepository.DeleteByLegacyIdAsync(legacyId, ct);
        deleted += await _calibrationRepository.DeleteByLegacyIdAsync(legacyId, ct);

        if (deleted > 0)
            _logger.LogDebug("Deleted {Count} v4 records for legacy entry {LegacyId}", deleted, legacyId);

        return deleted;
    }

    /// <inheritdoc />
    public async Task<long> BulkDeleteAsync(string? find, CancellationToken ct = default)
    {
        var (fromMills, toMills) = Core.Models.Entries.EntryDomainLogic.ParseTimeRangeFromFind(find);

        // ParseTimeRangeFromFind extracts $gte/$lte from any field, not just
        // time fields. A query like {"sgv":{"$gte":180}} would parse from=180 (nonsensical as a
        // timestamp). Reject values below year 2000 in millis as clearly not time bounds.
        const long MinPlausibleMills = 946684800000L; // 2000-01-01T00:00:00Z
        if (fromMills.HasValue && fromMills.Value < MinPlausibleMills)
            fromMills = null;
        if (toMills.HasValue && toMills.Value < MinPlausibleMills)
            toMills = null;

        // NIGHTSCOUT-COMPAT: Legacy Nightscout allowed arbitrary MongoDB find queries for
        // bulk delete (e.g. {"sgv":{"$gte":180}}). After V4 migration we only support
        // time-range filters. If the caller passed a non-empty find query but we couldn't
        // extract any time bounds, refuse to delete — otherwise we'd wipe all records.
        // Null/empty find intentionally deletes everything (matches "delete all" semantics).
        var hasFind = !string.IsNullOrEmpty(find) && find != "{}";
        var hasTimeBounds = fromMills.HasValue || toMills.HasValue;

        if (hasFind && !hasTimeBounds)
        {
            _logger.LogWarning("BulkDelete refused: find query has no parseable time range, would delete all records. find={Find}", find);
            return 0;
        }

        DateTime? from = fromMills.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(fromMills.Value).UtcDateTime
            : null;
        DateTime? to = toMills.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(toMills.Value).UtcDateTime
            : null;

        var sgDeleted = await _sensorGlucoseRepository.DeleteByTimeRangeAsync(from, to, ct);
        var mgDeleted = await _meterGlucoseRepository.DeleteByTimeRangeAsync(from, to, ct);
        var calDeleted = await _calibrationRepository.DeleteByTimeRangeAsync(from, to, ct);

        var total = (long)sgDeleted + mgDeleted + calDeleted;
        _logger.LogInformation("BulkDelete: removed {Total} v4 records (sg={Sg}, mg={Mg}, cal={Cal}) for find={Find}",
            total, sgDeleted, mgDeleted, calDeleted, find);

        return total;
    }

    /// <summary>Maps a legacy <see cref="Entry"/> of type <c>sgv</c> to a <see cref="SensorGlucose"/> model.</summary>
    /// <param name="entry">The legacy entry to map.</param>
    /// <param name="correlationId">Optional correlation identifier linking records created in the same decomposition pass.</param>
    /// <returns>A new <see cref="SensorGlucose"/> populated from the entry.</returns>
    internal static SensorGlucose MapToSensorGlucose(Entry entry, Guid? correlationId)
    {
        return new SensorGlucose
        {
            LegacyId = entry.Id,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills).UtcDateTime,
            Mgdl = entry.Sgv ?? entry.Mgdl,
            Direction = MapDirection(entry.Direction),
            TrendRate = entry.TrendRate,
            Noise = entry.Noise,
            Device = entry.Device,
            App = entry.App,
            DataSource = entry.DataSource,
            UtcOffset = entry.UtcOffset,
            CorrelationId = correlationId
        };
    }

    /// <summary>Maps a legacy <see cref="Entry"/> of type <c>mbg</c> to a <see cref="MeterGlucose"/> model.</summary>
    /// <param name="entry">The legacy entry to map.</param>
    /// <param name="correlationId">Optional correlation identifier linking records created in the same decomposition pass.</param>
    /// <returns>A new <see cref="MeterGlucose"/> populated from the entry.</returns>
    internal static MeterGlucose MapToMeterGlucose(Entry entry, Guid? correlationId)
    {
        return new MeterGlucose
        {
            LegacyId = entry.Id,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills).UtcDateTime,
            Mgdl = entry.Mbg ?? entry.Mgdl,
            Device = entry.Device,
            App = entry.App,
            DataSource = entry.DataSource,
            UtcOffset = entry.UtcOffset,
            CorrelationId = correlationId
        };
    }

    /// <summary>Maps a legacy <see cref="Entry"/> of type <c>cal</c> to a <see cref="Calibration"/> model.</summary>
    /// <param name="entry">The legacy entry to map.</param>
    /// <param name="correlationId">Optional correlation identifier linking records created in the same decomposition pass.</param>
    /// <returns>A new <see cref="Calibration"/> populated from the entry.</returns>
    internal static Calibration MapToCalibration(Entry entry, Guid? correlationId)
    {
        return new Calibration
        {
            LegacyId = entry.Id,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills).UtcDateTime,
            Slope = entry.Slope,
            Intercept = entry.Intercept,
            Scale = entry.Scale,
            Device = entry.Device,
            App = entry.App,
            DataSource = entry.DataSource,
            UtcOffset = entry.UtcOffset,
            CorrelationId = correlationId
        };
    }

    private static bool TryGetString(Dictionary<string, object> props, string key, out string value)
    {
        value = default!;
        if (!props.TryGetValue(key, out var obj))
            return false;

        if (obj is string s) { value = s; return true; }
        if (obj is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.String)
        { value = el.GetString()!; return true; }

        return false;
    }

    private static bool TryGetDouble(Dictionary<string, object> props, string key, out double value)
    {
        value = default;
        if (!props.TryGetValue(key, out var obj))
            return false;

        if (obj is double d) { value = d; return true; }
        if (obj is System.Text.Json.JsonElement el && el.TryGetDouble(out var elVal))
        { value = elVal; return true; }

        return false;
    }

    /// <summary>
    /// Converts a Nightscout direction string (e.g. <c>"SingleUp"</c>, <c>"Flat"</c>) to the typed
    /// <see cref="GlucoseDirection"/> enum. Returns <see langword="null"/> for unknown or empty values.
    /// </summary>
    /// <param name="direction">The raw direction string from the legacy entry.</param>
    /// <returns>The corresponding <see cref="GlucoseDirection"/> value, or <see langword="null"/> if unrecognised.</returns>
    internal static GlucoseDirection? MapDirection(string? direction)
    {
        if (string.IsNullOrEmpty(direction))
            return null;

        return direction switch
        {
            "NONE" => GlucoseDirection.None,
            "DoubleUp" => GlucoseDirection.DoubleUp,
            "SingleUp" => GlucoseDirection.SingleUp,
            "FortyFiveUp" => GlucoseDirection.FortyFiveUp,
            "Flat" => GlucoseDirection.Flat,
            "FortyFiveDown" => GlucoseDirection.FortyFiveDown,
            "SingleDown" => GlucoseDirection.SingleDown,
            "DoubleDown" => GlucoseDirection.DoubleDown,
            "NOT COMPUTABLE" => GlucoseDirection.NotComputable,
            "RATE OUT OF RANGE" => GlucoseDirection.RateOutOfRange,
            _ => Enum.TryParse<GlucoseDirection>(direction, ignoreCase: true, out var parsed)
                ? parsed
                : null
        };
    }

}
