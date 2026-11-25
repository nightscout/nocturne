using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.Connectors.Tidepool.Mappers;

public class TidepoolSensorGlucoseMapper(ILogger logger, string connectorSource)
{
    private static readonly ConnectorLegacyIds LegacyIds = ConnectorLegacyIds.For<TidepoolConnectorConfiguration>();

    private readonly string _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public IEnumerable<SensorGlucose> MapBgValues(TidepoolBgValue[]? bgValues)
    {
        if (bgValues == null || bgValues.Length == 0) return [];

        return ConnectorFaults.NonNullRecords(bgValues, _logger, _connectorSource, "Tidepool BG value")
            .Where(bg => bg.Value > 0 && bg.Time.HasValue)
            .Select(bg => ConvertBgValue(bg))
            .Where(sg => sg != null)
            .Cast<SensorGlucose>()
            .OrderBy(sg => sg.Mills)
            .ToList();
    }

    private SensorGlucose? ConvertBgValue(TidepoolBgValue bgValue)
    {
        try
        {
            var mgdl = ConvertToMgdl(bgValue.Value, bgValue.Units);
            if (mgdl <= 0) return null;

            var timestamp = bgValue.Time!.Value.ToUniversalTime();

            return new SensorGlucose
            {
                Timestamp = timestamp,
                LegacyId = LegacyIds.Of(bgValue.Id),
                Device = _connectorSource,
                Mgdl = mgdl,
            };
        }
        catch (Exception ex) when (ConnectorFaults.IsMalformedRecord(ex))
        {
            _logger.LogWarning(ex, "Skipped malformed Tidepool BG value {Id}", bgValue.Id);
            return null;
        }
    }

    internal static double ConvertToMgdl(double value, string? units) =>
        GlucoseUnits.ToMgdl(value, GlucoseUnits.Parse(units) ?? GlucoseUnit.MgDl);
}
