using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.API.Helpers;
using Nocturne.Core.Contracts.Audit;
using System.Linq;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.API.Services.Entries;

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
internal class EntryDecomposer : DecomposerBase, IEntryDecomposer
{
    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly IMeterGlucoseRepository _meterGlucoseRepository;
    private readonly ICalibrationRepository _calibrationRepository;
    private readonly IGlucoseProcessingResolver _glucoseResolver;
    private readonly IPatientDeviceStamper _patientDeviceStamper;
    private readonly IAuditContext _auditContext;

    public EntryDecomposer(
        ISensorGlucoseRepository sensorGlucoseRepository,
        IMeterGlucoseRepository meterGlucoseRepository,
        ICalibrationRepository calibrationRepository,
        IGlucoseProcessingResolver glucoseResolver,
        IPatientDeviceStamper patientDeviceStamper,
        IAuditContext auditContext,
        ILogger<EntryDecomposer> logger)
        : base(logger)
    {
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _meterGlucoseRepository = meterGlucoseRepository;
        _calibrationRepository = calibrationRepository;
        _glucoseResolver = glucoseResolver;
        _patientDeviceStamper = patientDeviceStamper;
        _auditContext = auditContext;
    }

    /// <summary>The tables an entry decomposes into, all keyed by its legacy id.</summary>
    private StoredCorrelationLookup[] EntryTables =>
    [
        _sensorGlucoseRepository.GetCorrelationIdsByLegacyIdAsync,
        _meterGlucoseRepository.GetCorrelationIdsByLegacyIdAsync,
        _calibrationRepository.GetCorrelationIdsByLegacyIdAsync,
    ];

    /// <inheritdoc />
    /// <remarks>
    /// A batch of one under the caller's attribution, since a user's edit is their trail
    /// (<see cref="DecomposerBase.SystemAttributedBatchWrites"/>); its id names the stored reading as
    /// reads resolve it (<see cref="EntryTable{TRecord}.Named"/>).
    /// </remarks>
    public Task<DecompositionResult> DecomposeAsync(Entry entry, WriteOrigin origin, CancellationToken ct = default)
        => DecomposeBatchAsync([entry], origin, edit: true, ct);

    /// <inheritdoc />
    public Task<DecompositionResult> DecomposeBatchAsync(
        IReadOnlyList<Entry> entries, WriteOrigin origin, CancellationToken ct = default)
        => DecomposeBatchAsync(entries, origin, edit: false, ct);

    /// <summary>
    /// Four phases with a fixed set of queries whatever the batch's size: the correlation ids its
    /// legacy ids are stored under and the rows its served-shaped ids name are read, one query per
    /// table each; every entry is mapped; the readings are attributed to devices in one stamp per
    /// reading table; and each table is written in one bulk upsert.
    /// </summary>
    /// <remarks>
    /// <paramref name="edit"/> marks a user's edit of the readings its ids name, which may carry no
    /// legacy id or another one. Otherwise an upload's id that only names a reading with no legacy id
    /// is that reading's echo and is skipped (<see cref="EntryTable{TRecord}.Echoes"/>).
    /// </remarks>
    private async Task<DecompositionResult> DecomposeBatchAsync(
        IReadOnlyList<Entry> entries, WriteOrigin origin, bool edit, CancellationToken ct)
    {
        var result = new DecompositionResult();
        if (entries.Count == 0)
            return result;

        var sensor = new EntryTable<SensorGlucose>(_sensorGlucoseRepository);
        var meter = new EntryTable<MeterGlucose>(_meterGlucoseRepository);
        var calibration = new EntryTable<Calibration>(_calibrationRepository);

        var correlationIds = await GetStoredCorrelationIdsAsync(entries.Select(e => e.Id), EntryTables, ct);
        await sensor.ReadServedAsync(ServedIds("sgv"), ct);
        await meter.ReadServedAsync(ServedIds("mbg"), ct);
        await calibration.ReadServedAsync(ServedIds("cal"), ct);

        var unsupportedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            switch (entry.Type?.ToLowerInvariant())
            {
                case "sgv":
                    await sensor.AddAsync(entry, edit, correlationIds, id => BuildSensorGlucoseAsync(entry, id, ct), result);
                    break;
                case "mbg":
                    await meter.AddAsync(entry, edit, correlationIds, id => Task.FromResult(MapToMeterGlucose(entry, id)), result);
                    break;
                case "cal":
                    await calibration.AddAsync(entry, edit, correlationIds, id => Task.FromResult(MapToCalibration(entry, id)), result);
                    break;
                default:
                    result.SkippedUnsupported++;
                    unsupportedTypes.Add(LogText.Sanitize(entry.Type));
                    break;
            }
        }

        if (result.SkippedUnsupported > 0)
        {
            Logger.LogWarning(
                "Skipped {Count} entries whose type Nocturne does not store: {Types}",
                result.SkippedUnsupported, string.Join(", ", unsupportedTypes));
        }

        await AttributeAsync(sensor.Models, DeviceAttributionCategories.SensorGlucose, ct);
        await AttributeAsync(meter.Models, DeviceAttributionCategories.MeterGlucose, ct);

        using (edit ? null : SystemAttributedBatchWrites(_auditContext))
        {
            await sensor.WriteAsync(result, origin, ct);
            await meter.WriteAsync(result, origin, ct);
            await calibration.WriteAsync(result, origin, ct);
        }

        return result;

        IEnumerable<string> ServedIds(string type) => entries
            .Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase))
            .Select(e => edit ? MongoObjectId.Coerce(e.Id) : e.Id)
            .Where(id => edit ? MongoObjectId.IsObjectId(id) : MongoObjectId.IsGuidPrefixShaped(id))
            .OfType<string>();
    }

    private async Task AttributeAsync<TRecord>(
        List<TRecord> records, IReadOnlyList<DeviceCategory> categories, CancellationToken ct)
        where TRecord : IV4Record, IDeviceAttributed
    {
        if (records.Count == 0)
            return;
        await _patientDeviceStamper.StampAsync(records.Cast<IDeviceAttributed>().ToList(), categories, batchSource: null, ct);
        CarryAttributionAcrossResends(records);
    }

    /// <summary>
    /// Glucose processing type and smoothed/unsmoothed values are resolved from the hints a v1/v3
    /// entry carries in its additional properties.
    /// </summary>
    private async Task<SensorGlucose> BuildSensorGlucoseAsync(Entry entry, Guid? correlationId, CancellationToken ct)
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
        return model;
    }

    /// <summary>
    /// One reading table's part of a batch: the rows its served-shaped ids name, and the models it
    /// writes by legacy id or onto a stored row.
    /// </summary>
    private sealed class EntryTable<TRecord>(ILegacyKeyedRepository<TRecord> repository)
        where TRecord : class, IV4Record
    {
        private IReadOnlyList<TRecord> _served = [];
        private readonly List<TRecord> _upserts = [];
        private readonly List<(TRecord Stored, TRecord Model)> _ontoStored = [];

        /// <summary>Every model the batch writes to the table, in batch order.</summary>
        public List<TRecord> Models { get; } = [];

        public async Task ReadServedAsync(IEnumerable<string> objectIds, CancellationToken ct)
        {
            var ids = objectIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ids.Count > 0)
                _served = await repository.GetByServedIdsAsync(ids, ct);
        }

        /// <summary>
        /// Whether <paramref name="id"/> is the 24-hex form <see cref="MongoObjectId.FromGuid"/> gave a
        /// stored reading that has no legacy id: the id Nightscout write-back sent it under, now pulled
        /// back by the connector. Keyed on <c>LegacyId</c>, the bulk upsert cannot see that reading and
        /// would store it a second time, and an echo must not overwrite it. A reading with a legacy id
        /// was written back under that id, which the bulk upsert already matches.
        /// </summary>
        private bool Echoes(string? id) => id is not null && ByPrefix(id) is { LegacyId: null };

        /// <summary>
        /// The stored reading an edit's <paramref name="id"/> names when no reading carries it as its
        /// legacy id, as <c>EntryReadService.GetStoredByIdAsync</c> resolves it: a uuid names the reading
        /// with that id, else the one keyed under its ObjectId; a 24-hex id names the reading whose id
        /// it prefixes.
        /// </summary>
        public TRecord? Named(string id)
        {
            if (_served.Any(r => string.Equals(r.LegacyId, id, StringComparison.Ordinal)))
                return null;
            if (Guid.TryParse(id, out var uuid))
            {
                var coerced = MongoObjectId.FromGuid(uuid);
                return _served.FirstOrDefault(r => r.Id == uuid)
                    ?? _served.FirstOrDefault(r => string.Equals(r.LegacyId, coerced, StringComparison.Ordinal));
            }
            return ByPrefix(id);
        }

        private TRecord? ByPrefix(string id) => _served
            .Where(r => string.Equals(MongoObjectId.FromGuid(r.Id), id, StringComparison.OrdinalIgnoreCase))
            .MinBy(r => r.Id);

        /// <summary>
        /// Maps <paramref name="entry"/> under the correlation id of the row it writes, unless it is an
        /// upload's echo. An edit naming a row by another id writes under that row's legacy id, or onto
        /// the row itself when it has none.
        /// </summary>
        public async Task AddAsync(
            Entry entry, bool edit, Dictionary<string, Guid> correlationIds,
            Func<Guid?, Task<TRecord>> map, DecompositionResult result)
        {
            if (!edit && Echoes(entry.Id))
                return;

            var stored = edit && entry.Id is { } id ? Named(id) : null;
            var model = await map(stored?.CorrelationId ?? CorrelationIdFor(entry.Id, correlationIds));
            Models.Add(model);
            result.CorrelationId ??= model.CorrelationId;
            if (stored is null)
            {
                _upserts.Add(model);
            }
            else if (stored.LegacyId is { } legacyId)
            {
                model.LegacyId = legacyId;
                _upserts.Add(model);
            }
            else
            {
                model.LegacyId = null;
                _ontoStored.Add((stored, model));
            }
        }

        public async Task WriteAsync(DecompositionResult result, WriteOrigin origin, CancellationToken ct)
        {
            await BulkUpsertAsync(repository, _upserts, result, origin, ct);
            foreach (var (stored, model) in _ontoStored)
                await WriteOntoStoredAsync(repository, stored, model, result, origin, ct);
        }
    }

    /// <summary>
    /// A legacy id sent more than once in a batch is written as its last version; a version that
    /// resolved no device keeps the one an earlier version resolved, as a re-send keeps the device of
    /// the row it updates.
    /// </summary>
    private static void CarryAttributionAcrossResends<TRecord>(List<TRecord> records)
        where TRecord : IV4Record, IDeviceAttributed
    {
        var resolved = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.LegacyId is not { } legacyId)
                continue;
            if (record.PatientDeviceId is { } deviceId)
                resolved[legacyId] = deviceId;
            else if (resolved.TryGetValue(legacyId, out var earlier))
                record.PatientDeviceId = earlier;
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default)
    {
        var deleted = 0;
        deleted += await _sensorGlucoseRepository.DeleteByLegacyIdAsync(legacyId, origin, ct);
        deleted += await _meterGlucoseRepository.DeleteByLegacyIdAsync(legacyId, origin, ct);
        deleted += await _calibrationRepository.DeleteByLegacyIdAsync(legacyId, origin, ct);

        if (deleted > 0)
            Logger.LogDebug("Soft-deleted {Count} v4 records for legacy entry {LegacyId}", deleted, legacyId);

        return deleted;
    }

    /// <inheritdoc />
    public Task<int> DeleteStoredAsync(IV4Record stored, WriteOrigin origin, CancellationToken ct = default)
        => stored.LegacyId is { } legacyId
            ? DeleteByLegacyIdAsync(legacyId, origin, ct)
            : stored switch
            {
                SensorGlucose => DeleteUnkeyedAsync(_sensorGlucoseRepository, stored.Id, origin, ct),
                MeterGlucose => DeleteUnkeyedAsync(_meterGlucoseRepository, stored.Id, origin, ct),
                Calibration => DeleteUnkeyedAsync(_calibrationRepository, stored.Id, origin, ct),
                _ => throw new ArgumentException($"{stored.GetType().Name} is not an entry record", nameof(stored)),
            };

    private static async Task<int> DeleteUnkeyedAsync<TRecord>(
        IV4Repository<TRecord> repository, Guid id, WriteOrigin origin, CancellationToken ct)
        where TRecord : class, IV4Record
    {
        try
        {
            await repository.DeleteAsync(id, origin, ct);
            return 1;
        }
        catch (KeyNotFoundException)
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task<long> BulkDeleteAsync(string? find, WriteOrigin origin, CancellationToken ct = default)
    {
        var findQuery = Core.Models.Queries.FindQuery.Parse(find);
        var (fromMills, toMills) = (findQuery.FromMills, findQuery.ToMills);

        if (findQuery.IsEmpty)
        {
            Logger.LogWarning("BulkDelete refused: the find has no conditions");
            return 0;
        }

        // A find[type]=x equality narrows the sweep to that record type.
        var typeFilter = findQuery.GetEqualityValue("type");
        if (typeFilter is not null && !LegacyEntryTypes.All.Contains(typeFilter))
        {
            Logger.LogWarning("BulkDelete refused: the find names a record type the sweep does not cover");
            return 0;
        }

        // Any other field filter cannot be honored by a by-time sweep, so refuse rather than wipe
        // non-matching records.
        if (findQuery.HasFieldFiltersExcept("type"))
        {
            Logger.LogWarning("BulkDelete refused: the find carries field filters the by-time sweep cannot honor");
            return 0;
        }

        // NIGHTSCOUT-COMPAT: Legacy Nightscout allowed arbitrary MongoDB find queries for
        // bulk delete. If the caller passed a find but we couldn't extract any time bounds or
        // a type filter, refuse to delete, otherwise we'd wipe all records.
        if (!fromMills.HasValue && !toMills.HasValue && typeFilter is null)
        {
            Logger.LogWarning("BulkDelete refused: the find has no parseable time range or type");
            return 0;
        }

        DateTime? from = fromMills.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(fromMills.Value).UtcDateTime
            : null;
        DateTime? to = toMills.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(toMills.Value).UtcDateTime
            : null;

        var sgDeleted = typeFilter is null or "sgv"
            ? await _sensorGlucoseRepository.DeleteByTimeRangeAsync(from, to, origin, ct) : 0;
        var mgDeleted = typeFilter is null or "mbg"
            ? await _meterGlucoseRepository.DeleteByTimeRangeAsync(from, to, origin, ct) : 0;
        var calDeleted = typeFilter is null or "cal"
            ? await _calibrationRepository.DeleteByTimeRangeAsync(from, to, origin, ct) : 0;

        var total = (long)sgDeleted + mgDeleted + calDeleted;
        Logger.LogInformation("BulkDelete: removed {Total} v4 records (sg={Sg}, mg={Mg}, cal={Cal}) from={From} to={To} type={Type}",
            total, sgDeleted, mgDeleted, calDeleted, from, to, typeFilter);

        return total;
    }

    /// <summary>The mg/dL value an entry is stored with, or 0 for a type that stores none.</summary>
    /// <remarks>
    /// <c>sgv</c> and <c>mbg</c> are the fields a legacy client edits; a PATCH or GET-edit-PUT carries a
    /// stale <c>mgdl</c> beside them.
    /// </remarks>
    internal static double WrittenMgdl(Entry entry) => entry.Type?.ToLowerInvariant() switch
    {
        "mbg" => entry.Mbg ?? entry.Mgdl,
        "cal" => 0,
        _ => entry.Sgv ?? entry.Mgdl,
    };

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
            Mgdl = WrittenMgdl(entry),
            Direction = MapDirection(entry.Direction),
            TrendRate = entry.TrendRate,
            Noise = entry.Noise,
            Filtered = entry.Filtered,
            Unfiltered = entry.Unfiltered,
            Delta = entry.Delta,
            Rssi = entry.Rssi,
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
            Mgdl = WrittenMgdl(entry),
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
