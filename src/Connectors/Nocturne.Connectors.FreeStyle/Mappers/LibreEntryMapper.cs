using Microsoft.Extensions.Logging;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Models;
using Nocturne.Connectors.FreeStyle.Utilities;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.FreeStyle.Mappers;

public class LibreEntryMapper
{
    private static readonly Dictionary<int, Direction> TrendArrowMap = new()
    {
        { 1, Direction.SingleDown },
        { 2, Direction.FortyFiveDown },
        { 3, Direction.Flat },
        { 4, Direction.FortyFiveUp },
        { 5, Direction.SingleUp }
    };

    private readonly ILogger? _logger;

    public LibreEntryMapper(ILogger? logger = null)
    {
        _logger = logger;
    }

    public Entry ConvertLibreEntry(LibreGlucoseMeasurement measurement)
    {
        try
        {
            var timestamp = LibreTimestampParser.Parse(measurement.FactoryTimestamp);

            var direction = TrendArrowMap.GetValueOrDefault(
                measurement.TrendArrow,
                Direction.NotComputable
            );

            // Generate deterministic ID based on device + timestamp for deduplication
            var id = $"libre_{measurement.FactoryTimestamp}";

            return new Entry
            {
                Id = id,
                Date = timestamp,
                Sgv = measurement.ValueInMgPerDl,
                Direction = direction.ToString(),
                Device = LibreLinkUpConstants.Configuration.DeviceIdentifier,
                Type = LibreLinkUpConstants.Configuration.EntryType
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error converting LibreLinkUp entry: {@Entry}", measurement);
            return new Entry
            {
                Type = LibreLinkUpConstants.Configuration.EntryType,
                Device = LibreLinkUpConstants.Configuration.DeviceIdentifier
            };
        }
    }
}