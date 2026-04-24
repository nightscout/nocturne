using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Eversense.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Eversense.Mappers;

public class EversenseSensorGlucoseMapper(ILogger logger)
{
    /// <summary>
    /// Eversense trend values mapped to GlucoseDirection.
    /// Mapping verified via LoopKit GlucoseTrend enum and the EversenseNowClient trendmap.
    /// </summary>
    private static readonly Dictionary<int, GlucoseDirection> TrendDirections = new()
    {
        { 0, GlucoseDirection.None },
        { 1, GlucoseDirection.SingleDown },
        { 2, GlucoseDirection.FortyFiveDown },
        { 3, GlucoseDirection.Flat },
        { 4, GlucoseDirection.FortyFiveUp },
        { 5, GlucoseDirection.SingleUp },
        { 6, GlucoseDirection.DoubleDown },
        { 7, GlucoseDirection.DoubleUp },
    };

    private const double MmolToMgdlFactor = 18.0182;

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public SensorGlucose? Map(EversensePatientDatum patient)
    {
        try
        {
            if (!DateTimeOffset.TryParse(patient.CgTime, out var timestamp))
            {
                _logger.LogWarning("Could not parse Eversense timestamp: {Timestamp}", patient.CgTime);
                return null;
            }

            var mgdl = patient.Units == 1
                ? patient.CurrentGlucose * MmolToMgdlFactor
                : (double)patient.CurrentGlucose;

            var direction = TrendDirections.GetValueOrDefault(patient.GlucoseTrend, GlucoseDirection.None);
            var now = DateTime.UtcNow;

            return new SensorGlucose
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp.UtcDateTime,
                LegacyId = $"eversense_{timestamp.ToUnixTimeMilliseconds()}",
                Device = DataSources.EversenseConnector,
                DataSource = DataSources.EversenseConnector,
                Mgdl = mgdl,
                Direction = direction,
                CreatedAt = now,
                ModifiedAt = now
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error converting Eversense patient datum");
            return null;
        }
    }
}
