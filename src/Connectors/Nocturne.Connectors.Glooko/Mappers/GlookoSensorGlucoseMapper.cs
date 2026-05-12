using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Glooko.Mappers;

public class GlookoSensorGlucoseMapper
{
    private readonly GlookoConnectorConfiguration _config;
    private readonly string _connectorSource;
    private readonly ILogger _logger;
    private readonly GlookoTimeMapper _timeMapper;

    public GlookoSensorGlucoseMapper(
        GlookoConnectorConfiguration config,
        string connectorSource,
        GlookoTimeMapper timeMapper,
        ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
        _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IEnumerable<SensorGlucose> TransformBatchDataToSensorGlucose(GlookoBatchData batchData)
    {
        var results = new List<SensorGlucose>();
        if (batchData?.Readings == null) return results;

        foreach (var reading in batchData.Readings)
        {
            var sg = ParseSensorGlucose(reading);
            if (sg != null) results.Add(sg);
        }

        return results;
    }

    public IEnumerable<SensorGlucose> TransformV3ToSensorGlucose(GlookoV3GraphResponse graphData, string? meterUnits)
    {
        var results = new List<SensorGlucose>();
        if (graphData?.Series == null) return results;

        var series = graphData.Series;
        var allCgm = (series.CgmHigh ?? [])
            .Concat(series.CgmNormal ?? [])
            .Concat(series.CgmLow ?? [])
            .OrderBy(p => p.X);

        foreach (var reading in allCgm.Where(r => !r.Calculated))
        {
            var timestamp = _timeMapper.GetCorrectedGlookoTime(reading.X);
            var mgdl = ConvertToMgdl(reading.Y, meterUnits);
            if (mgdl <= 0) continue;

            var now = DateTime.UtcNow;
            results.Add(new SensorGlucose
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp,
                LegacyId = $"glooko_v3_{reading.X}",
                Device = _connectorSource,
                DataSource = _connectorSource,
                Mgdl = mgdl,
                Direction = GlucoseDirection.Flat,
                CreatedAt = now,
                ModifiedAt = now
            });
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} CGM sensor glucose records from v3 data",
            _connectorSource, results.Count);

        return results;
    }

    /// <summary>
    /// Maps V3 BG meter series (bgHigh, bgNormal, bgLow) to BGCheck records.
    /// Values use the same encoding as CGM: Y is in display units, Value is mg/dL × 100.
    /// </summary>
    public IEnumerable<BGCheck> TransformV3ToBGChecks(GlookoV3GraphResponse graphData, string? meterUnits)
    {
        var results = new List<BGCheck>();
        if (graphData?.Series == null) return results;

        var series = graphData.Series;
        var allBg = (series.BgHigh ?? [])
            .Concat(series.BgNormal ?? [])
            .Concat(series.BgLow ?? [])
            .OrderBy(p => p.X);

        foreach (var reading in allBg)
        {
            var timestamp = _timeMapper.GetCorrectedGlookoTime(reading.X);
            var mgdl = reading.Value / 100.0;
            if (mgdl <= 0) continue;

            var legacyId = !string.IsNullOrEmpty(reading.Id)
                ? $"glooko_v3_bg_{reading.Id}"
                : $"glooko_v3_bg_{reading.X}";

            var now = DateTime.UtcNow;
            results.Add(new BGCheck
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp,
                LegacyId = legacyId,
                Device = _connectorSource,
                DataSource = _connectorSource,
                Glucose = mgdl,
                Units = GlucoseUnit.MgDl,
                GlucoseType = GlucoseType.Finger,
                CreatedAt = now,
                ModifiedAt = now
            });
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} BG checks from v3 data",
            _connectorSource, results.Count);

        return results;
    }

    /// <summary>
    /// Maps V2 meter readings to BGCheck records (finger stick / manual blood glucose).
    /// Values are always mg/dL × 100 from Glooko, regardless of account meter units.
    /// </summary>
    public IEnumerable<BGCheck> TransformBatchDataToBGChecks(GlookoBatchData batchData)
    {
        var results = new List<BGCheck>();
        if (batchData?.MeterReadings == null) return results;

        foreach (var reading in batchData.MeterReadings)
        {
            var bgCheck = ParseBGCheck(reading);
            if (bgCheck != null) results.Add(bgCheck);
        }

        return results;
    }

    // ── Parsing helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Parses and timezone-corrects a Glooko V2 timestamp string.
    /// Returns null if the timestamp is missing or unparseable.
    /// </summary>
    private DateTime? ParseV2Timestamp(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
            return null;

        if (!DateTime.TryParse(timestamp, out var parsedDate))
        {
            _logger.LogWarning("Failed to parse Glooko timestamp: '{Timestamp}'", timestamp);
            return null;
        }

        var date = parsedDate.ToUniversalTime();
        if (_config.TimezoneOffset != 0)
            date = date.AddHours(-_config.TimezoneOffset);

        return date;
    }

    private SensorGlucose? ParseSensorGlucose(GlookoCgmReading reading)
    {
        try
        {
            if (reading.Value <= 0 || reading.SoftDeleted == true) return null;

            var date = ParseV2Timestamp(reading.Timestamp);
            if (date == null) return null;

            // Glooko V2 CGM values are mg/dL × 100 (integer encoding for 2-decimal precision)
            var mgdl = reading.Value / 100.0;

            var legacyId = !string.IsNullOrEmpty(reading.Guid)
                ? $"glooko_cgm_{reading.Guid}"
                : $"glooko_{date.Value.Ticks}";

            var now = DateTime.UtcNow;
            return new SensorGlucose
            {
                Id = Guid.CreateVersion7(),
                Timestamp = date.Value,
                LegacyId = legacyId,
                Device = _connectorSource,
                DataSource = _connectorSource,
                Mgdl = mgdl,
                Direction = ParseTrendToDirection(reading.Trend),
                CreatedAt = now,
                ModifiedAt = now
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing Glooko CGM reading");
            return null;
        }
    }

    private BGCheck? ParseBGCheck(GlookoMeterReading reading)
    {
        try
        {
            if (reading.Value <= 0 || reading.SoftDeleted == true) return null;

            var date = ParseV2Timestamp(reading.Timestamp);
            if (date == null) return null;

            // Glooko meter values are mg/dL × 100 (integer encoding for 2-decimal precision)
            var mgdl = reading.Value / 100.0;

            var legacyId = !string.IsNullOrEmpty(reading.Guid)
                ? $"glooko_meter_{reading.Guid}"
                : $"glooko_meter_{date.Value.Ticks}";

            var now = DateTime.UtcNow;
            return new BGCheck
            {
                Id = Guid.CreateVersion7(),
                Timestamp = date.Value,
                LegacyId = legacyId,
                Device = _connectorSource,
                DataSource = _connectorSource,
                Glucose = mgdl,
                Units = GlucoseUnit.MgDl,
                GlucoseType = GlucoseType.Finger,
                CreatedAt = now,
                ModifiedAt = now
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing Glooko meter reading");
            return null;
        }
    }

    private static GlucoseDirection ParseTrendToDirection(string? trend)
    {
        if (string.IsNullOrWhiteSpace(trend)) return GlucoseDirection.Flat;

        return trend.ToUpperInvariant() switch
        {
            "DOUBLEUP" or "DOUBLE_UP" => GlucoseDirection.DoubleUp,
            "SINGLEUP" or "SINGLE_UP" => GlucoseDirection.SingleUp,
            "FORTYFIVEUP" or "FORTY_FIVE_UP" => GlucoseDirection.FortyFiveUp,
            "FLAT" => GlucoseDirection.Flat,
            "FORTYFIVEDOWN" or "FORTY_FIVE_DOWN" => GlucoseDirection.FortyFiveDown,
            "SINGLEDOWN" or "SINGLE_DOWN" => GlucoseDirection.SingleDown,
            "DOUBLEDOWN" or "DOUBLE_DOWN" => GlucoseDirection.DoubleDown,
            "NOT COMPUTABLE" or "NOTCOMPUTABLE" => GlucoseDirection.NotComputable,
            "RATE OUT OF RANGE" or "RATEOUTOFRANGE" => GlucoseDirection.RateOutOfRange,
            _ => GlucoseDirection.Flat
        };
    }

    private static double ConvertToMgdl(double value, string? meterUnits) =>
        meterUnits?.ToLowerInvariant() == "mmoll" ? value * 18.0182 : value;
}
