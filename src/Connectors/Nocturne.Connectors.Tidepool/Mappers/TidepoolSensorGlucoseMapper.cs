using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Tidepool.Mappers;

public class TidepoolSensorGlucoseMapper(ILogger logger, string connectorSource)
{
    private readonly string _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public IEnumerable<SensorGlucose> MapBgValues(TidepoolBgValue[]? bgValues)
    {
        if (bgValues == null || bgValues.Length == 0) return [];

        return bgValues
            .Where(bg => bg.Value > 0 && bg.Time.HasValue)
            .Select(ConvertBgValue)
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
            var now = DateTime.UtcNow;

            return new SensorGlucose
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp,
                LegacyId = $"tidepool_{bgValue.Id}",
                Device = _connectorSource,
                DataSource = _connectorSource,
                Mgdl = mgdl,
                CreatedAt = now,
                ModifiedAt = now
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error converting Tidepool BG value: {Id}", bgValue.Id);
            return null;
        }
    }

    internal static double ConvertToMgdl(double value, string units)
    {
        if (string.IsNullOrEmpty(units)) return value;
        return units.ToLowerInvariant() switch
        {
            "mmol/l" or "mmol" => value * GlucoseConstants.MgdlPerMmol,
            "mg/dl" => value,
            _ => value
        };
    }
}
