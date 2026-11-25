using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Models;
using Nocturne.Connectors.Dexcom.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.Connectors.Dexcom.Mappers;

public class DexcomSensorGlucoseMapper(ILogger logger)
{
    private static readonly ConnectorLegacyIds LegacyIds = ConnectorLegacyIds.For<DexcomConnectorConfiguration>();

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public IEnumerable<SensorGlucose> MapBatchData(DexcomEntry[]? batchData)
    {
        if (batchData == null || batchData.Length == 0) return [];

        return ConnectorFaults.NonNullRecords(batchData, _logger, DataSources.DexcomConnector, "Dexcom entry")
            .Where(entry => entry.Value > 0)
            .Select(entry => ConvertEntry(entry))
            .Where(sg => sg != null)
            .Cast<SensorGlucose>()
            .OrderBy(sg => sg.Mills)
            .ToList();
    }

    private SensorGlucose? ConvertEntry(DexcomEntry dexcomEntry)
    {
        try
        {
            if (!DexcomTimestampParser.TryParse(dexcomEntry.Wt, out var timestamp))
            {
                _logger.LogWarning("Could not parse Dexcom timestamp: {Timestamp}", dexcomEntry.Wt);
                return null;
            }

            var direction = dexcomEntry.Trend;
            var mgdl = (double)dexcomEntry.Value;

            return new SensorGlucose
            {
                Timestamp = timestamp,
                LegacyId = LegacyIds.Of(dexcomEntry.Wt),
                Device = DataSources.DexcomConnector,
                Mgdl = mgdl,
                Direction = direction,
            };
        }
        catch (Exception ex) when (ConnectorFaults.IsMalformedRecord(ex))
        {
            _logger.LogWarning(ex, "Skipped malformed Dexcom entry at {Timestamp}", dexcomEntry.Wt);
            return null;
        }
    }
}
