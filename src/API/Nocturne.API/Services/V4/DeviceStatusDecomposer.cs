using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Decomposes legacy <see cref="DeviceStatus"/> records into typed v4 snapshot tables.
/// Extracts APS (<see cref="V4Models.ApsSnapshot"/> for OpenAPS/AAPS/Trio and Loop), pump
/// (<see cref="V4Models.PumpSnapshot"/>), and uploader (<see cref="V4Models.UploaderSnapshot"/>)
/// snapshots, and persists them with idempotent create-or-update via <c>LegacyId</c> matching.
/// Active device overrides are delegated to <see cref="IStateSpanService"/> as
/// <see cref="StateSpanCategory.Override"/> spans.
/// </summary>
/// <seealso cref="IDeviceStatusDecomposer"/>
/// <seealso cref="IDecomposer{T}"/>
public class DeviceStatusDecomposer : IDeviceStatusDecomposer, IDecomposer<DeviceStatus>
{
    private readonly NocturneDbContext _dbContext;
    private readonly IApsSnapshotRepository _apsRepo;
    private readonly IPumpSnapshotRepository _pumpRepo;
    private readonly IUploaderSnapshotRepository _uploaderRepo;
    private readonly IDeviceStatusExtrasRepository _extrasRepo;
    private readonly IStateSpanService _stateSpanService;
    private readonly IDeviceService _deviceService;
    private readonly ILogger<DeviceStatusDecomposer> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <param name="dbContext">EF Core context used to persist <see cref="DecompositionBatchEntity"/> records.</param>
    /// <param name="apsRepo">Repository for <see cref="V4Models.ApsSnapshot"/> records.</param>
    /// <param name="pumpRepo">Repository for <see cref="V4Models.PumpSnapshot"/> records.</param>
    /// <param name="uploaderRepo">Repository for <see cref="V4Models.UploaderSnapshot"/> records.</param>
    /// <param name="extrasRepo">Repository for <see cref="V4Models.DeviceStatusExtras"/> records.</param>
    /// <param name="stateSpanService">Service used to upsert override state spans extracted from device status.</param>
    /// <param name="deviceService">Service that resolves or creates canonical device references.</param>
    /// <param name="logger">Logger instance for this decomposer.</param>
    public DeviceStatusDecomposer(
        NocturneDbContext dbContext,
        IApsSnapshotRepository apsRepo,
        IPumpSnapshotRepository pumpRepo,
        IUploaderSnapshotRepository uploaderRepo,
        IDeviceStatusExtrasRepository extrasRepo,
        IStateSpanService stateSpanService,
        IDeviceService deviceService,
        ILogger<DeviceStatusDecomposer> logger)
    {
        _dbContext = dbContext;
        _apsRepo = apsRepo;
        _pumpRepo = pumpRepo;
        _uploaderRepo = uploaderRepo;
        _extrasRepo = extrasRepo;
        _stateSpanService = stateSpanService;
        _deviceService = deviceService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<V4Models.DecompositionResult> DecomposeAsync(DeviceStatus ds, CancellationToken ct = default)
    {
        var batch = new DecompositionBatchEntity
        {
            TenantId = _dbContext.TenantId,
            Source = "device_status_decomposer",
            SourceRecordId = ds.Id,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.DecompositionBatches.Add(batch);
        await _dbContext.SaveChangesAsync(ct);

        var result = new V4Models.DecompositionResult
        {
            CorrelationId = batch.Id
        };

        // AAPS sends "date" instead of "mills" — normalize before decomposition
        if (ds.Mills == 0 && ds.Date is > 0)
            ds.Mills = ds.Date.Value;

        var legacyId = ds.Id;

        if (ds.OpenAps != null)
        {
            await DecomposeApsFromOpenApsAsync(ds, legacyId, result, ct);
        }
        else if (ds.Loop != null)
        {
            await DecomposeApsFromLoopAsync(ds, legacyId, result, ct);
        }

        if (ds.Pump != null)
        {
            await DecomposePumpAsync(ds, legacyId, result, ct);
        }

        if (ds.Uploader != null || ds.UploaderBattery.HasValue)
        {
            await DecomposeUploaderAsync(ds, legacyId, result, ct);
        }

        if (ds.Override is { Active: true })
        {
            await DecomposeOverrideAsync(ds, legacyId, result, ct);
        }

        await DecomposeExtrasAsync(ds, result, ct);

        return result;
    }

    #region APS Decomposition

    private async Task DecomposeApsFromOpenApsAsync(
        DeviceStatus ds, string? legacyId, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var command = ds.OpenAps!.Enacted ?? ds.OpenAps.Suggested;
        var predBGs = command?.PredBGs;
        var apsSystem = DetectOpenApsVariant(ds);

        var model = new V4Models.ApsSnapshot
        {
            Timestamp = ResolveTimestamp(ds),
            UtcOffset = ds.UtcOffset,
            Device = ds.Device,
            LegacyId = legacyId,
            AidAlgorithm = apsSystem,
            Iob = ds.OpenAps.Iob?.Iob,
            BasalIob = ds.OpenAps.Iob?.BasalIob,
            BolusIob = ds.OpenAps.Iob?.BolusIob,
            Cob = ds.OpenAps.Cob ?? command?.COB,
            CurrentBg = command?.Bg,
            EventualBg = command?.EventualBG,
            TargetBg = command?.TargetBG,
            RecommendedBolus = command?.InsulinReq,
            SensitivityRatio = command?.SensitivityRatio,
            Enacted = ds.OpenAps.Enacted != null
                && (ds.OpenAps.Enacted.Received == true || ds.OpenAps.Enacted.Recieved == true),
            EnactedRate = ds.OpenAps.Enacted?.Rate,
            EnactedDuration = ds.OpenAps.Enacted?.Duration,
            EnactedBolusVolume = ds.OpenAps.Enacted?.Smb is > 0
                ? ds.OpenAps.Enacted.Smb
                : ds.OpenAps.Enacted?.Units,
            SuggestedJson = SerializeOrNull(ds.OpenAps.Suggested),
            EnactedJson = SerializeOrNull(ds.OpenAps.Enacted),
            // Trio uses oref multi-curve predictions exclusively; PredictedDefaultJson
            // is reserved for Loop's single combined curve. OpenAPS/AAPS duplicate IOB
            // into the default slot for backwards compatibility.
            PredictedDefaultJson = apsSystem == V4Models.AidAlgorithm.Trio
                ? null
                : SerializeOrNull(predBGs?.IOB),
            PredictedIobJson = SerializeOrNull(predBGs?.IOB),
            PredictedZtJson = SerializeOrNull(predBGs?.ZT),
            PredictedCobJson = SerializeOrNull(predBGs?.COB),
            PredictedUamJson = SerializeOrNull(predBGs?.UAM),
            PredictedStartTimestamp = ParseTimestampToDateTime(command?.Timestamp),
            AidVersion = ds.OpenAps?.Version,
        };

        await UpsertApsSnapshotAsync(legacyId, model, result, ct);
    }

    private async Task DecomposeApsFromLoopAsync(
        DeviceStatus ds, string? legacyId, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var model = new V4Models.ApsSnapshot
        {
            Timestamp = ResolveTimestamp(ds),
            UtcOffset = ds.UtcOffset,
            Device = ds.Device,
            LegacyId = legacyId,
            AidAlgorithm = V4Models.AidAlgorithm.Loop,
            Iob = ds.Loop!.Iob?.Iob,
            BasalIob = ds.Loop.Iob?.BasalIob,
            BolusIob = null,
            Cob = ds.Loop.Cob?.Cob,
            CurrentBg = ds.Loop.Predicted?.Values?.FirstOrDefault(),
            EventualBg = ds.Loop.Predicted?.Values?.LastOrDefault(),
            RecommendedBolus = ds.Loop.RecommendedBolus,
            Enacted = ds.Loop.Enacted?.Received == true,
            EnactedRate = ds.Loop.Enacted?.Rate,
            EnactedDuration = ds.Loop.Enacted?.Duration,
            EnactedBolusVolume = ds.Loop.Enacted?.BolusVolume,
            SuggestedJson = SerializeOrNull(ds.Loop.Recommended),
            EnactedJson = SerializeOrNull(ds.Loop.Enacted),
            PredictedDefaultJson = SerializeOrNull(ds.Loop.Predicted?.Values),
            PredictedStartTimestamp = ParseTimestampToDateTime(ds.Loop.Predicted?.StartDate),
            LoopJson = SerializeOrNull(ds.Loop),
            // Loop's version is captured from the device string (e.g. "Loop/3.0"), not from the Loop data object
            AidVersion = null,
        };

        await UpsertApsSnapshotAsync(legacyId, model, result, ct);
    }

    private async Task UpsertApsSnapshotAsync(
        string? legacyId, V4Models.ApsSnapshot model, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var existing = legacyId != null
            ? await _apsRepo.GetByLegacyIdAsync(legacyId, ct)
            : null;

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _apsRepo.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing ApsSnapshot {Id} from legacy device status {LegacyId}", existing.Id, legacyId);
        }
        else
        {
            var created = await _apsRepo.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created ApsSnapshot from legacy device status {LegacyId}", legacyId);
        }
    }

    #endregion

    #region Pump Decomposition

    private async Task DecomposePumpAsync(
        DeviceStatus ds, string? legacyId, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var model = new V4Models.PumpSnapshot
        {
            Timestamp = ResolveTimestamp(ds),
            UtcOffset = ds.UtcOffset,
            Device = ds.Device,
            LegacyId = legacyId,
            Manufacturer = ds.Pump!.Manufacturer,
            Model = ds.Pump.Model,
            Reservoir = ds.Pump.Reservoir,
            ReservoirDisplay = ds.Pump.ReservoirDisplayOverride,
            BatteryPercent = ds.Pump.Battery?.Percent,
            BatteryVoltage = ds.Pump.Battery?.Voltage,
            Bolusing = ds.Pump.Status?.Bolusing,
            Suspended = ds.Pump.Status?.Suspended,
            PumpStatus = ds.Pump.Status?.Status,
            Clock = ds.Pump.Clock,
            Iob = ds.Pump.Iob?.Iob,
            BolusIob = ds.Pump.Iob?.BolusIob,
            AdditionalProperties = ds.Pump.Extended is { Count: > 0 }
                ? ds.Pump.Extended.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value)
                : null,
        };

        model.DeviceId = await _deviceService.ResolveAsync(
            V4Models.DeviceCategory.InsulinPump,
            ds.Pump?.Manufacturer,
            ds.Pump?.Model,
            ds.Mills, ct);

        var existing = legacyId != null
            ? await _pumpRepo.GetByLegacyIdAsync(legacyId, ct)
            : null;

        V4Models.PumpSnapshot persisted;
        if (existing != null)
        {
            model.Id = existing.Id;
            persisted = await _pumpRepo.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(persisted);
            _logger.LogDebug("Updated existing PumpSnapshot {Id} from legacy device status {LegacyId}", existing.Id, legacyId);
        }
        else
        {
            persisted = await _pumpRepo.CreateAsync(model, ct);
            result.CreatedRecords.Add(persisted);
            _logger.LogDebug("Created PumpSnapshot from legacy device status {LegacyId}", legacyId);
        }

        await DecomposePumpSuspensionAsync(ds, persisted, result, ct);
    }

    /// <summary>
    /// Detects pump-suspension transitions and emits/closes a
    /// <see cref="StateSpanCategory.PumpMode"/> / <see cref="PumpModeState.Suspended"/> state span.
    /// </summary>
    /// <remarks>
    /// <para>Compares the just-upserted <see cref="V4Models.PumpSnapshot"/> against the most-recent
    /// prior snapshot strictly before its timestamp. On a <c>false → true</c> transition (or first
    /// observation with <c>Suspended == true</c>), opens a new span. On <c>true → false</c>, closes
    /// the open span. Equal-state comparisons are no-ops.</para>
    /// <para>First observation: when there is no prior snapshot, opening on
    /// <c>Suspended == true</c> anchors the span at the first observed timestamp — there is no
    /// transition signal to anchor on otherwise.</para>
    /// <para>Idempotency: the open span carries a deterministic
    /// <c>OriginalId = "pump-suspended:{snapshotId}"</c> so re-decomposing the same legacy
    /// <see cref="DeviceStatus"/> will upsert (not duplicate) the row.</para>
    /// <para>Assumes a single insulin pump per tenant — the open-span lookup does not filter by
    /// <c>Source</c>, so a second pump's resume could close a first pump's open span. Out of scope
    /// per the alerting model (one tenant = one diabetic person).</para>
    /// </remarks>
    private async Task DecomposePumpSuspensionAsync(
        DeviceStatus ds,
        V4Models.PumpSnapshot newSnapshot,
        V4Models.DecompositionResult result,
        CancellationToken ct)
    {
        var prior = await _pumpRepo.GetLatestBeforeAsync(newSnapshot.Timestamp, ct);
        var priorSuspended = prior?.Suspended ?? false;
        var nowSuspended = newSnapshot.Suspended ?? false;

        if (priorSuspended == nowSuspended)
            return;

        // Prefer pump's own clock for the transition timestamp; fall back to ingestion timestamp.
        var transitionAt = ParseTimestampToDateTime(newSnapshot.Clock) ?? newSnapshot.Timestamp;

        if (!priorSuspended && nowSuspended)
        {
            var span = new StateSpan
            {
                Category = StateSpanCategory.PumpMode,
                State = PumpModeState.Suspended.ToString(),
                StartTimestamp = transitionAt,
                EndTimestamp = null,
                Source = ds.Device,
                OriginalId = $"pump-suspended:{newSnapshot.Id}",
            };

            var upserted = await _stateSpanService.UpsertStateSpanAsync(span, ct);
            result.CreatedRecords.Add(upserted);
            _logger.LogDebug(
                "Opened PumpMode/Suspended StateSpan for snapshot {SnapshotId} (legacy {LegacyId})",
                newSnapshot.Id, newSnapshot.LegacyId);
        }
        else // priorSuspended && !nowSuspended
        {
            var openSpans = await _stateSpanService.GetStateSpansAsync(
                category: StateSpanCategory.PumpMode,
                state: PumpModeState.Suspended.ToString(),
                active: true,
                count: 1,
                cancellationToken: ct);

            var openSpan = openSpans.FirstOrDefault();
            if (openSpan is null)
            {
                _logger.LogWarning(
                    "PumpMode/Suspended transition true→false detected but no open StateSpan to close (snapshot {SnapshotId})",
                    newSnapshot.Id);
                return;
            }

            openSpan.EndTimestamp = transitionAt;
            var closed = await _stateSpanService.UpsertStateSpanAsync(openSpan, ct);
            result.UpdatedRecords.Add(closed);
            _logger.LogDebug(
                "Closed PumpMode/Suspended StateSpan {SpanId} at {EndTimestamp}",
                openSpan.Id, transitionAt);
        }
    }

    #endregion

    #region Uploader Decomposition

    private async Task DecomposeUploaderAsync(
        DeviceStatus ds, string? legacyId, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var model = new V4Models.UploaderSnapshot
        {
            Timestamp = ResolveTimestamp(ds),
            UtcOffset = ds.UtcOffset,
            Device = ds.Device,
            LegacyId = legacyId,
            Name = ds.Uploader?.Name,
            Battery = ds.Uploader?.Battery ?? ds.UploaderBattery,
            BatteryVoltage = ds.Uploader?.BatteryVoltage,
            IsCharging = ds.IsCharging,
            Temperature = ds.Uploader?.Temperature,
            Type = ds.Uploader?.Type,
        };

        model.DeviceId = await _deviceService.ResolveAsync(
            V4Models.DeviceCategory.Uploader,
            ds.Uploader?.Name,
            ds.Uploader?.Type ?? "unknown",
            ds.Mills, ct);

        var existing = legacyId != null
            ? await _uploaderRepo.GetByLegacyIdAsync(legacyId, ct)
            : null;

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _uploaderRepo.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing UploaderSnapshot {Id} from legacy device status {LegacyId}", existing.Id, legacyId);
        }
        else
        {
            var created = await _uploaderRepo.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created UploaderSnapshot from legacy device status {LegacyId}", legacyId);
        }
    }

    #endregion

    #region Override Decomposition

    private async Task DecomposeOverrideAsync(
        DeviceStatus ds, string? legacyId, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var timestamp = ResolveTimestamp(ds);
        var stateSpan = new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartTimestamp = timestamp,
            EndTimestamp = ds.Override!.Duration is > 0
                ? timestamp.AddMinutes(ds.Override.Duration.Value)
                : null,
            Source = ds.Device,
            OriginalId = legacyId,
            Metadata = BuildOverrideMetadata(ds.Override),
        };

        var upserted = await _stateSpanService.UpsertStateSpanAsync(stateSpan, ct);
        result.CreatedRecords.Add(upserted);
        _logger.LogDebug("Delegated Override from device status {LegacyId} to IStateSpanService", legacyId);
    }

    #endregion

    #region Extras Decomposition

    private async Task DecomposeExtrasAsync(
        DeviceStatus ds, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var extras = new Dictionary<string, object?>();

        if (ds.XDripJs != null)
            extras["xdripjs"] = ds.XDripJs;
        if (ds.RadioAdapter != null)
            extras["radioAdapter"] = ds.RadioAdapter;
        if (ds.Connect != null)
            extras["connect"] = ds.Connect;
        if (ds.Cgm != null)
            extras["cgm"] = ds.Cgm;
        if (ds.Meter != null)
            extras["meter"] = ds.Meter;
        if (ds.InsulinPen != null)
            extras["insulinPen"] = ds.InsulinPen;
        if (ds.MmTune != null)
            extras["mmtune"] = ds.MmTune;
        // RileyLinks live on the Loop object, which is already fully serialized into
        // ApsSnapshot.LoopJson when ds.Loop is present — no need to duplicate here.

        // Capture unknown top-level keys from JSON deserialization
        if (ds.ExtensionData != null)
        {
            foreach (var kvp in ds.ExtensionData)
                extras[kvp.Key] = kvp.Value;
        }

        if (extras.Count == 0 || result.CorrelationId is not { } correlationId)
            return;

        var model = new V4Models.DeviceStatusExtras
        {
            CorrelationId = correlationId,
            Timestamp = ResolveTimestamp(ds),
            Extras = extras,
        };

        var created = await _extrasRepo.CreateAsync(model, ct);
        result.CreatedRecords.Add(created);
        _logger.LogDebug("Created DeviceStatusExtras with {Count} keys for correlation {CorrelationId}",
            extras.Count, result.CorrelationId);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Distinguishes between vanilla OpenAPS, AAPS, and Trio based on payload heuristics.
    /// All three post under the "openaps" devicestatus key.
    /// - AAPS: uploader name contains "AndroidAPS"
    /// - Trio: openaps block includes a version field
    /// - Vanilla OpenAPS: neither of the above
    /// </summary>
    internal static V4Models.AidAlgorithm DetectOpenApsVariant(DeviceStatus ds)
    {
        if (ds.Uploader?.Name?.Contains("AndroidAPS", StringComparison.OrdinalIgnoreCase) == true)
            return V4Models.AidAlgorithm.AndroidAps;

        if (!string.IsNullOrEmpty(ds.OpenAps?.Version))
            return V4Models.AidAlgorithm.Trio;

        return V4Models.AidAlgorithm.OpenAps;
    }

    private static string? SerializeOrNull<T>(T? obj) where T : class
    {
        return obj is null ? null : JsonSerializer.Serialize(obj, JsonOptions);
    }

    private static string? SerializeOrNull(double[]? array)
    {
        return array is null ? null : JsonSerializer.Serialize(array, JsonOptions);
    }

    private static string? SerializeOrNull(List<double>? list)
    {
        return list is null ? null : JsonSerializer.Serialize(list, JsonOptions);
    }

    /// <summary>
    /// Resolves the best available timestamp for a device status record.
    /// Priority: Mills (already normalized from date) > OpenAPS IOB time >
    /// OpenAPS enacted/suggested timestamp > Loop predicted start date > Pump clock > CreatedAt > now.
    /// </summary>
    internal static DateTime ResolveTimestamp(DeviceStatus ds)
    {
        if (ds.Mills > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ds.Mills).UtcDateTime;

        // Try OpenAPS IOB time
        if (ParseTimestampToDateTime(ds.OpenAps?.Iob?.Time) is { } iobTime)
            return iobTime;

        // Try OpenAPS enacted/suggested timestamp
        var command = ds.OpenAps?.Enacted ?? ds.OpenAps?.Suggested;
        if (ParseTimestampToDateTime(command?.Timestamp) is { } commandTime)
            return commandTime;

        // Try Loop predicted start date
        if (ParseTimestampToDateTime(ds.Loop?.Predicted?.StartDate) is { } loopTime)
            return loopTime;

        // Try pump clock
        if (ParseTimestampToDateTime(ds.Pump?.Clock) is { } pumpTime)
            return pumpTime;

        // Try CreatedAt
        if (ParseTimestampToDateTime(ds.CreatedAt) is { } createdTime)
            return createdTime;

        return DateTime.UtcNow;
    }

    private static DateTime? ParseTimestampToDateTime(string? timestamp)
    {
        if (string.IsNullOrEmpty(timestamp))
            return null;
        return DateTimeOffset.TryParse(timestamp, out var dto) ? dto.UtcDateTime : null;
    }

    private static Dictionary<string, object>? BuildOverrideMetadata(OverrideStatus overrideStatus)
    {
        var metadata = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(overrideStatus.Name))
            metadata["name"] = overrideStatus.Name;

        if (overrideStatus.Multiplier.HasValue)
            metadata["multiplier"] = overrideStatus.Multiplier.Value;

        if (overrideStatus.CurrentCorrectionRange?.MinValue.HasValue == true)
            metadata["currentCorrectionRange.minValue"] = overrideStatus.CurrentCorrectionRange.MinValue.Value;

        if (overrideStatus.CurrentCorrectionRange?.MaxValue.HasValue == true)
            metadata["currentCorrectionRange.maxValue"] = overrideStatus.CurrentCorrectionRange.MaxValue.Value;

        return metadata.Count > 0 ? metadata : null;
    }

    #endregion

    /// <inheritdoc />
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var deleted = 0;

        // Look up correlation ID from any snapshot with this legacy ID before deleting
        var apsSnapshot = await _apsRepo.GetByLegacyIdAsync(legacyId, ct);
        var correlationId = apsSnapshot?.CorrelationId;
        if (correlationId == null)
        {
            var pumpSnapshot = await _pumpRepo.GetByLegacyIdAsync(legacyId, ct);
            correlationId = pumpSnapshot?.CorrelationId;
        }
        if (correlationId == null)
        {
            var uploaderSnapshot = await _uploaderRepo.GetByLegacyIdAsync(legacyId, ct);
            correlationId = uploaderSnapshot?.CorrelationId;
        }

        deleted += await _apsRepo.DeleteByLegacyIdAsync(legacyId, ct);
        deleted += await _pumpRepo.DeleteByLegacyIdAsync(legacyId, ct);
        deleted += await _uploaderRepo.DeleteByLegacyIdAsync(legacyId, ct);

        if (correlationId.HasValue)
            deleted += await _extrasRepo.DeleteByCorrelationIdAsync(correlationId.Value, ct);

        if (deleted > 0)
            _logger.LogDebug("Deleted {Count} v4 snapshot records for legacy device status {LegacyId}", deleted, legacyId);

        return deleted;
    }
}
