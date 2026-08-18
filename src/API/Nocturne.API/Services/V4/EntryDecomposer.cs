using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
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
    private readonly IPatientDeviceStamper _patientDeviceStamper;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<EntryDecomposer> _logger;

    /// <param name="dbContext">EF Core context used for entry bulk-delete operations.</param>
    /// <param name="sensorGlucoseRepository">Repository for <see cref="SensorGlucose"/> records.</param>
    /// <param name="meterGlucoseRepository">Repository for <see cref="MeterGlucose"/> records.</param>
    /// <param name="calibrationRepository">Repository for <see cref="Calibration"/> records.</param>
    /// <param name="glucoseResolver">Resolves glucose processing type and smoothed/unsmoothed values from v1/v3 hints or source defaults.</param>
    /// <param name="patientDeviceStamper">Attributes decomposed records to the patient device active at their timestamp.</param>
    /// <param name="logger">Logger instance for this decomposer.</param>
    public EntryDecomposer(
        NocturneDbContext dbContext,
        ISensorGlucoseRepository sensorGlucoseRepository,
        IMeterGlucoseRepository meterGlucoseRepository,
        ICalibrationRepository calibrationRepository,
        IGlucoseProcessingResolver glucoseResolver,
        IPatientDeviceStamper patientDeviceStamper,
        IAuditContext auditContext,
        ILogger<EntryDecomposer> logger)
    {
        _dbContext = dbContext;
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _meterGlucoseRepository = meterGlucoseRepository;
        _calibrationRepository = calibrationRepository;
        _glucoseResolver = glucoseResolver;
        _patientDeviceStamper = patientDeviceStamper;
        _auditContext = auditContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DecompositionResult> DecomposeAsync(Entry entry, WriteOrigin origin, CancellationToken ct = default)
    {
        var result = new DecompositionResult
        {
            CorrelationId = Guid.CreateVersion7()
        };

        var entryType = entry.Type?.ToLowerInvariant();

        switch (entryType)
        {
            case "sgv":
                await DecomposeSgvAsync(entry, result, origin, ct);
                break;
            case "mbg":
                await DecomposeMbgAsync(entry, result, origin, ct);
                break;
            case "cal":
                await DecomposeCalAsync(entry, result, origin, ct);
                break;
            default:
                _logger.LogWarning("Unknown entry type '{Type}' for entry {Id}, skipping decomposition", entry.Type, entry.Id);
                break;
        }

        return result;
    }

    private async Task DecomposeSgvAsync(Entry entry, DecompositionResult result, WriteOrigin origin, CancellationToken ct)
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
        // Carry an earlier attribution forward on re-upload: the rebuilt model starts null and a
        // later ambiguous stamp (e.g. a second device registered since) must not wipe the stored FK.
        model.PatientDeviceId = existing?.PatientDeviceId;
        await _patientDeviceStamper.StampAsync([model], DeviceAttributionCategories.SensorGlucose, model.DataSource, ct);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _sensorGlucoseRepository.UpdateAsync(existing.Id, model, origin, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing SensorGlucose {Id} from legacy entry {LegacyId}", existing.Id, entry.Id);
        }
        else
        {
            var created = await _sensorGlucoseRepository.CreateAsync(model, origin, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created SensorGlucose from legacy entry {LegacyId}", entry.Id);
        }
    }

    private async Task DecomposeMbgAsync(Entry entry, DecompositionResult result, WriteOrigin origin, CancellationToken ct)
    {
        var existing = entry.Id != null
            ? await _meterGlucoseRepository.GetByLegacyIdAsync(entry.Id, ct)
            : null;

        var model = MapToMeterGlucose(entry, result.CorrelationId);
        model.PatientDeviceId = existing?.PatientDeviceId;
        await _patientDeviceStamper.StampAsync([model], DeviceAttributionCategories.MeterGlucose, model.DataSource, ct);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _meterGlucoseRepository.UpdateAsync(existing.Id, model, origin, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing MeterGlucose {Id} from legacy entry {LegacyId}", existing.Id, entry.Id);
        }
        else
        {
            var created = await _meterGlucoseRepository.CreateAsync(model, origin, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created MeterGlucose from legacy entry {LegacyId}", entry.Id);
        }
    }

    private async Task DecomposeCalAsync(Entry entry, DecompositionResult result, WriteOrigin origin, CancellationToken ct)
    {
        var existing = entry.Id != null
            ? await _calibrationRepository.GetByLegacyIdAsync(entry.Id, ct)
            : null;

        var model = MapToCalibration(entry, result.CorrelationId);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _calibrationRepository.UpdateAsync(existing.Id, model, origin, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing Calibration {Id} from legacy entry {LegacyId}", existing.Id, entry.Id);
        }
        else
        {
            var created = await _calibrationRepository.CreateAsync(model, origin, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created Calibration from legacy entry {LegacyId}", entry.Id);
        }
    }

    /// <inheritdoc />
    public async Task<DecompositionResult> DecomposeBatchAsync(
        IReadOnlyList<Entry> entries, WriteOrigin origin, CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return new DecompositionResult();

        var correlationId = Guid.CreateVersion7();
        var result = new DecompositionResult { CorrelationId = correlationId };

        var sgvList = new List<SensorGlucose>();
        var mbgList = new List<MeterGlucose>();
        var calList = new List<Calibration>();

        foreach (var entry in entries)
        {
            switch (entry.Type?.ToLowerInvariant())
            {
                case "sgv":
                {
                    var model = MapToSensorGlucose(entry, correlationId);

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
                    sgvList.Add(model);
                    break;
                }
                case "mbg":
                    mbgList.Add(MapToMeterGlucose(entry, correlationId));
                    break;
                case "cal":
                    calList.Add(MapToCalibration(entry, correlationId));
                    break;
                default:
                    _logger.LogDebug("Skipping entry with unknown type: {Type}", entry.Type);
                    break;
            }
        }

        if (sgvList.Count > 0)
            await _patientDeviceStamper.StampAsync(sgvList, DeviceAttributionCategories.SensorGlucose, batchSource: null, ct);
        if (mbgList.Count > 0)
            await _patientDeviceStamper.StampAsync(mbgList, DeviceAttributionCategories.MeterGlucose, batchSource: null, ct);

        using (SystemAuditScope.Push(_auditContext))
        {
            if (sgvList.Count > 0)
            {
                var created = await _sensorGlucoseRepository.BulkCreateAsync(sgvList, origin, ct);
                result.CreatedRecords.AddRange(created);
            }

            if (mbgList.Count > 0)
            {
                var created = await _meterGlucoseRepository.BulkCreateAsync(mbgList, origin, ct);
                result.CreatedRecords.AddRange(created);
            }

            if (calList.Count > 0)
            {
                var created = await _calibrationRepository.BulkCreateAsync(calList, origin, ct);
                result.CreatedRecords.AddRange(created);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default)
    {
        var deleted = 0;
        deleted += await _sensorGlucoseRepository.DeleteByLegacyIdAsync(legacyId, origin, ct);
        deleted += await _meterGlucoseRepository.DeleteByLegacyIdAsync(legacyId, origin, ct);
        deleted += await _calibrationRepository.DeleteByLegacyIdAsync(legacyId, origin, ct);

        if (deleted > 0)
            _logger.LogDebug("Soft-deleted {Count} v4 records for legacy entry {LegacyId}", deleted, legacyId);

        return deleted;
    }

    /// <inheritdoc />
    public async Task<long> BulkDeleteAsync(string? find, WriteOrigin origin, CancellationToken ct = default)
    {
        // origin is accepted for interface uniformity; bulk clear-by-time-range stays a coarse op that
        // does NOT route through the per-record chokepoint (it fires EntryService's OnBulkDeletedAsync).
        var findQuery = Core.Models.Queries.FindQuery.Parse(find);
        var (fromMills, toMills) = (findQuery.FromMills, findQuery.ToMills);

        // find is client-controlled; strip line breaks so it can't forge log entries
        var findForLog = find?.ReplaceLineEndings(" ");

        // A find[type]=x equality narrows the sweep to that record type; any other field filter
        // cannot be honored by a by-time sweep, so refuse rather than wipe non-matching records.
        var typeFilter = findQuery.GetEqualityValue("type");
        if (findQuery.HasFieldFiltersExcept("type"))
        {
            _logger.LogWarning("BulkDelete refused: find query carries field filters the by-time sweep cannot honor. find={Find}", findForLog);
            return 0;
        }

        // NIGHTSCOUT-COMPAT: Legacy Nightscout allowed arbitrary MongoDB find queries for
        // bulk delete. If the caller passed a non-empty find query but we couldn't extract
        // any time bounds or a type filter, refuse to delete — otherwise we'd wipe all records.
        // Null/empty find intentionally deletes everything (matches "delete all" semantics).
        var hasFind = !string.IsNullOrEmpty(find) && find != "{}";
        var hasTimeBounds = fromMills.HasValue || toMills.HasValue;

        if (hasFind && !hasTimeBounds && typeFilter is null)
        {
            _logger.LogWarning("BulkDelete refused: find query has no parseable time range, would delete all records. find={Find}", findForLog);
            return 0;
        }

        DateTime? from = fromMills.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(fromMills.Value).UtcDateTime
            : null;
        DateTime? to = toMills.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(toMills.Value).UtcDateTime
            : null;

        var sgDeleted = typeFilter is null or "sgv"
            ? await _sensorGlucoseRepository.DeleteByTimeRangeAsync(from, to, ct) : 0;
        var mgDeleted = typeFilter is null or "mbg"
            ? await _meterGlucoseRepository.DeleteByTimeRangeAsync(from, to, ct) : 0;
        var calDeleted = typeFilter is null or "cal"
            ? await _calibrationRepository.DeleteByTimeRangeAsync(from, to, ct) : 0;

        var total = (long)sgDeleted + mgDeleted + calDeleted;
        _logger.LogInformation("BulkDelete: removed {Total} v4 records (sg={Sg}, mg={Mg}, cal={Cal}) for find={Find}",
            total, sgDeleted, mgDeleted, calDeleted, findForLog);

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
    /// Converts a Nightscout direction string (e.g. <c>"SingleUp"</c>, <c>"NOT COMPUTABLE"</c>) to the
    /// typed <see cref="GlucoseDirection"/> enum. Returns <see langword="null"/> for unknown or empty
    /// values, and for legacy values V4 does not model (triple arrows, CGM error).
    /// </summary>
    /// <param name="direction">The raw direction string from the legacy entry.</param>
    /// <returns>The corresponding <see cref="GlucoseDirection"/> value, or <see langword="null"/> if unrecognised.</returns>
    internal static GlucoseDirection? MapDirection(string? direction) =>
        DirectionExtensions.TryParse(direction, out var parsed) ? parsed.ToGlucoseDirection() : null;

}
