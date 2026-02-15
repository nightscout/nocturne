using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Glooko.Mappers;

public class GlookoEntryMapper
{
    private readonly GlookoConnectorConfiguration _config;
    private readonly string _connectorSource;
    private readonly ILogger _logger;

    public GlookoEntryMapper(
        GlookoConnectorConfiguration config,
        string connectorSource,
        ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IEnumerable<Entry> TransformBatchDataToEntries(GlookoBatchData batchData)
    {
        var entries = new List<Entry>();
        if (batchData?.Readings != null)
            foreach (var reading in batchData.Readings)
            {
                var entry = ParseEntry(reading);
                if (entry != null) entries.Add(entry);
            }

        return entries;
    }

    public IEnumerable<Entry> TransformV3ToEntries(
        GlookoV3GraphResponse graphData,
        string? meterUnits)
    {
        var entries = new List<Entry>();

        if (graphData?.Series == null)
            return entries;

        var series = graphData.Series;
        var allCgm = (series.CgmHigh ?? Array.Empty<GlookoV3GlucoseDataPoint>())
            .Concat(series.CgmNormal ?? Array.Empty<GlookoV3GlucoseDataPoint>())
            .Concat(series.CgmLow ?? Array.Empty<GlookoV3GlucoseDataPoint>())
            .OrderBy(p => p.X);

        foreach (var reading in allCgm.Where(reading => !reading.Calculated))
        {
            var timestamp = GetCorrectedGlookoTime(reading.X);
            var sgvMgdl = ConvertToMgdl(reading.Y, meterUnits);

            entries.Add(
                new Entry
                {
                    Id = $"glooko_v3_{reading.X}",
                    Date = timestamp,
                    Sgv = (int)Math.Round(sgvMgdl),
                    Type = "sgv",
                    Device = _connectorSource,
                    Direction = Direction.Flat.ToString()
                }
            );
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} CGM entries from v3 data",
            _connectorSource,
            entries.Count
        );

        return entries;
    }

    private Entry? ParseEntry(GlookoCgmReading reading)
    {
        try
        {
            if (string.IsNullOrEmpty(reading.Timestamp) || reading.Value <= 0) return null;

            var date = DateTime.Parse(reading.Timestamp).ToUniversalTime();

            if (_config.TimezoneOffset != 0) date = date.AddHours(-_config.TimezoneOffset);

            return new Entry
            {
                Date = date,
                Sgv = reading.Value,
                Type = "sgv",
                Device = _connectorSource,
                Direction = ParseTrendToDirection(reading.Trend).ToString(),
                Id = $"glooko_{date.Ticks}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error parsing Glooko CGM reading: {ex.Message}");
            return null;
        }
    }

    private static Direction ParseTrendToDirection(string? trend)
    {
        if (string.IsNullOrWhiteSpace(trend))
            return Direction.Flat;

        return trend.ToUpperInvariant() switch
        {
            "DOUBLEUP" or "DOUBLE_UP" => Direction.DoubleUp,
            "SINGLEUP" or "SINGLE_UP" => Direction.SingleUp,
            "FORTYFIVEUP" or "FORTY_FIVE_UP" => Direction.FortyFiveUp,
            "FLAT" => Direction.Flat,
            "FORTYFIVEDOWN" or "FORTY_FIVE_DOWN" => Direction.FortyFiveDown,
            "SINGLEDOWN" or "SINGLE_DOWN" => Direction.SingleDown,
            "DOUBLEDOWN" or "DOUBLE_DOWN" => Direction.DoubleDown,
            "TRIPLEUP" or "TRIPLE_UP" => Direction.TripleUp,
            "TRIPLEDOWN" or "TRIPLE_DOWN" => Direction.TripleDown,
            "NOT COMPUTABLE" or "NOTCOMPUTABLE" => Direction.NotComputable,
            "RATE OUT OF RANGE" or "RATEOUTOFRANGE" => Direction.RateOutOfRange,
            _ => Direction.Flat
        };
    }

    private DateTime GetCorrectedGlookoTime(long unixSeconds)
    {
        var rawUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        return GetCorrectedGlookoTime(rawUtc);
    }

    private DateTime GetCorrectedGlookoTime(DateTime rawDate)
    {
        var offsetHours = _config.TimezoneOffset;
        var corrected = rawDate.AddHours(-offsetHours);
        _logger.LogDebug(
            "GetCorrectedGlookoTime: Raw={Raw}, ConfigOffset={ConfigOffset}, Result={Result}",
            rawDate,
            _config.TimezoneOffset,
            corrected
        );
        return corrected;
    }

    private static double ConvertToMgdl(double value, string? meterUnits)
    {
        return meterUnits?.ToLowerInvariant() == "mmol" ? value * 18.0182 : value;
    }
}