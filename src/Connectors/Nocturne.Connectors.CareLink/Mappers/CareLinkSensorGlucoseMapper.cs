using Microsoft.Extensions.Logging;
using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Connectors.CareLink.Models;
using Nocturne.Connectors.CareLink.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.CareLink.Mappers;

public class CareLinkSensorGlucoseMapper(ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public List<SensorGlucose> Map(CareLinkData data)
    {
        if (data.Sgs is not { Count: > 0 })
            return [];

        var pumpOffsetMs = CareLinkTimestampParser.CalculatePumpOffsetMs(
            data.MedicalDeviceTime ?? "",
            data.CurrentServerTime);

        var isMmol = data.EffectiveBgUnits?.Contains("mmol", StringComparison.OrdinalIgnoreCase) == true;
        var deviceName = $"CareLink {data.MedicalDeviceFamily ?? "Unknown"}";
        var now = DateTime.UtcNow;

        var validSgs = data.Sgs
            .Where(sg => sg is { Kind: "SG", Sg: > 0 } && !string.IsNullOrEmpty(sg.Datetime))
            .OrderByDescending(sg => sg.Datetime)
            .ToList();

        var results = new List<SensorGlucose>(validSgs.Count);
        var trendDirection = CareLinkTrendMapper.Map(data.LastSGTrend);

        for (var i = 0; i < validSgs.Count; i++)
        {
            var sg = validSgs[i];
            var timestamp = CareLinkTimestampParser.ParseSgTimestamp(sg.Datetime, pumpOffsetMs);
            if (timestamp == null)
            {
                _logger.LogWarning("Could not parse CareLink SG timestamp: {Timestamp}", sg.Datetime);
                continue;
            }

            var mgdl = isMmol ? sg.Sg * CareLinkConstants.MmolToMgdlFactor : sg.Sg;

            results.Add(new SensorGlucose
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp.Value,
                LegacyId = $"carelink_{new DateTimeOffset(timestamp.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()}",
                Device = deviceName,
                DataSource = DataSources.CareLinkConnector,
                Mgdl = mgdl,
                Direction = i == 0 ? trendDirection : GlucoseDirection.None,
                CreatedAt = now,
                ModifiedAt = now,
            });
        }

        return results;
    }
}
