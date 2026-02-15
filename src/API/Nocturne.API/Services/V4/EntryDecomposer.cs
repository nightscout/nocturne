using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Decomposes legacy Entry records into v4 granular models.
/// Maps Entry.Type "sgv" -> SensorGlucose, "mbg" -> MeterGlucose, "cal" -> Calibration.
/// Supports idempotent create-or-update via LegacyId matching.
/// </summary>
public class EntryDecomposer : IEntryDecomposer
{
    private readonly SensorGlucoseRepository _sensorGlucoseRepository;
    private readonly MeterGlucoseRepository _meterGlucoseRepository;
    private readonly CalibrationRepository _calibrationRepository;
    private readonly ILogger<EntryDecomposer> _logger;

    public EntryDecomposer(
        SensorGlucoseRepository sensorGlucoseRepository,
        MeterGlucoseRepository meterGlucoseRepository,
        CalibrationRepository calibrationRepository,
        ILogger<EntryDecomposer> logger)
    {
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _meterGlucoseRepository = meterGlucoseRepository;
        _calibrationRepository = calibrationRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DecompositionResult> DecomposeAsync(Entry entry, CancellationToken ct = default)
    {
        var result = new DecompositionResult
        {
            CorrelationId = Guid.CreateVersion7()
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

    internal static SensorGlucose MapToSensorGlucose(Entry entry, Guid? correlationId)
    {
        return new SensorGlucose
        {
            LegacyId = entry.Id,
            Mills = entry.Mills,
            Mgdl = entry.Sgv ?? entry.Mgdl,
            Mmol = entry.Mmol,
            Direction = MapDirection(entry.Direction),
            Trend = MapTrend(entry.Trend),
            TrendRate = entry.TrendRate,
            Noise = entry.Noise,
            Device = entry.Device,
            App = entry.App,
            DataSource = entry.DataSource,
            UtcOffset = entry.UtcOffset,
            CorrelationId = correlationId
        };
    }

    internal static MeterGlucose MapToMeterGlucose(Entry entry, Guid? correlationId)
    {
        return new MeterGlucose
        {
            LegacyId = entry.Id,
            Mills = entry.Mills,
            Mgdl = entry.Mbg ?? entry.Mgdl,
            Mmol = entry.Mmol,
            Device = entry.Device,
            App = entry.App,
            DataSource = entry.DataSource,
            UtcOffset = entry.UtcOffset,
            CorrelationId = correlationId
        };
    }

    internal static Calibration MapToCalibration(Entry entry, Guid? correlationId)
    {
        return new Calibration
        {
            LegacyId = entry.Id,
            Mills = entry.Mills,
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

    internal static GlucoseTrend? MapTrend(int? trend)
    {
        if (trend is null)
            return null;

        if (Enum.IsDefined(typeof(GlucoseTrend), trend.Value))
            return (GlucoseTrend)trend.Value;

        return null;
    }
}
