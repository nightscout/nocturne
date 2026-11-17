using Microsoft.Extensions.Logging;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Models;
using Nocturne.Connectors.FreeStyle.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.Connectors.FreeStyle.Mappers;

public class LibreSensorGlucoseMapper(ILogger? logger = null)
{
    private static readonly Dictionary<int, GlucoseDirection> TrendArrowMap = new()
    {
        { 1, GlucoseDirection.SingleDown },
        { 2, GlucoseDirection.FortyFiveDown },
        { 3, GlucoseDirection.Flat },
        { 4, GlucoseDirection.FortyFiveUp },
        { 5, GlucoseDirection.SingleUp }
    };

    public SensorGlucose? ConvertMeasurement(LibreGlucoseMeasurement measurement, DateTime now)
    {
        try
        {
            var timestamp = LibreTimestampParser.Parse(measurement.FactoryTimestamp);
            var direction = TrendArrowMap.GetValueOrDefault(measurement.TrendArrow, GlucoseDirection.NotComputable);
            var mgdl = (double)measurement.ValueInMgPerDl;

            return new SensorGlucose
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp,
                LegacyId = $"libre_{measurement.FactoryTimestamp}",
                Device = LibreLinkUpConstants.Configuration.DeviceIdentifier,
                DataSource = DataSources.LibreConnector,
                Mgdl = mgdl,
                Direction = direction,
                CreatedAt = now,
                ModifiedAt = now
            };
        }
        catch (Exception ex) when (ConnectorFaults.IsMalformedRecord(ex))
        {
            logger?.LogWarning(ex, "Skipped malformed LibreLinkUp measurement at {Timestamp}", measurement.FactoryTimestamp);
            return null;
        }
    }
}
