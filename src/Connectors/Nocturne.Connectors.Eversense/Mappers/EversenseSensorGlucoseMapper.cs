using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Eversense.Configurations;
using Nocturne.Connectors.Eversense.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.Connectors.Eversense.Mappers;

public class EversenseSensorGlucoseMapper(ILogger logger)
{
    /// <summary>
    /// An Eversense trend value as a GlucoseDirection. The wire value is the ordinal of the
    /// Eversense 365 app's own ARROW_TYPE enum, which runs falling-to-rising — the inverse of
    /// LoopKit's GlucoseTrend, so this table reads as reversed if checked against LoopKit. 0 is
    /// STALE, and 1 to 5 run from the single-arrow FALLING_FAST to RISING_FAST in every build.
    /// The double arrows depend on the build that uploaded the value: the US app (8.3.0) lists
    /// DOUBLE_ARROW_RISING_FAST before DOUBLE_ARROW_FALLING_FAST, and the outside-US app (80.0.1)
    /// the other way round, so 6 and 7 follow the account's <paramref name="server"/> region.
    /// </summary>
    private static GlucoseDirection Direction(int trend, string server)
    {
        var us = !string.Equals(server, "OUS", StringComparison.OrdinalIgnoreCase);
        return trend switch
        {
            0 => GlucoseDirection.NotComputable,
            1 => GlucoseDirection.SingleDown,
            2 => GlucoseDirection.FortyFiveDown,
            3 => GlucoseDirection.Flat,
            4 => GlucoseDirection.FortyFiveUp,
            5 => GlucoseDirection.SingleUp,
            6 => us ? GlucoseDirection.DoubleUp : GlucoseDirection.DoubleDown,
            7 => us ? GlucoseDirection.DoubleDown : GlucoseDirection.DoubleUp,
            _ => GlucoseDirection.None,
        };
    }

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// The patient's latest reading, or null when there is none: a patient whose app has not
    /// uploaded yet is listed with <c>CurrentGlucose</c> 0 and <c>CGTime</c> 0001-01-01. The value is
    /// in mg/dL whatever the patient's display unit.
    /// </summary>
    public SensorGlucose? Map(EversensePatientDatum patient, string server, DateTime now)
    {
        try
        {
            if (!IsReading(patient.CurrentGlucose))
                return null;

            if (!UploaderTimestamp.TryParse(patient.CgTime, out var timestamp))
            {
                _logger.LogWarning("Could not parse Eversense timestamp: {Timestamp}", patient.CgTime);
                return null;
            }

            var direction = Direction(patient.GlucoseTrend, server);
            return Build(timestamp, patient.CurrentGlucose, direction, now);
        }
        catch (Exception ex) when (ConnectorFaults.IsMalformedRecord(ex))
        {
            _logger.LogWarning(ex, "Skipped malformed Eversense reading at {Timestamp}", patient.CgTime);
            return null;
        }
    }

    /// <summary>
    /// Sensor glucose rows of a history feed. The feed carries no trend, so
    /// <see cref="SensorGlucose.Direction"/> is left unset.
    /// </summary>
    public IReadOnlyList<SensorGlucose> MapHistory(IEnumerable<EversenseUserEvent> events, DateTime now)
    {
        var records = new List<SensorGlucose>();
        foreach (var e in events)
        {
            if (e.EventTypeId != EversenseConstants.SensorGlucoseEventType || !IsReading(e.Value))
                continue;

            if (!UploaderTimestamp.TryParse(e.EventDate, out var timestamp))
            {
                _logger.LogWarning("Could not parse Eversense event date: {EventDate}", e.EventDate);
                continue;
            }

            records.Add(Build(timestamp, e.Value, direction: null, now));
        }

        return records;
    }

    private static bool IsReading(double value) => value > 0 && value != EversenseConstants.NoGlucose;

    private static SensorGlucose Build(DateTimeOffset timestamp, double mgdl, GlucoseDirection? direction, DateTime now)
    {
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
}
