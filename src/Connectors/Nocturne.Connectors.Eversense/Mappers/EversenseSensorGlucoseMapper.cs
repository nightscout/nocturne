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
    /// Eversense trend values mapped to GlucoseDirection. The wire value is the ordinal of the
    /// Eversense app's own ARROW_TYPE enum, which runs falling-to-rising — the inverse of LoopKit's
    /// GlucoseTrend, so this table reads as reversed if checked against LoopKit. In the Eversense 365
    /// app that enum has eight members: 0 is STALE, 1 and 5 are the single-arrow FALLING_FAST and
    /// RISING_FAST, and 6 and 7 are DOUBLE_ARROW_FALLING_FAST and DOUBLE_ARROW_RISING_FAST.
    /// </summary>
    private static readonly Dictionary<int, GlucoseDirection> TrendDirections = new()
    {
        { 0, GlucoseDirection.NotComputable },
        { 1, GlucoseDirection.SingleDown },
        { 2, GlucoseDirection.FortyFiveDown },
        { 3, GlucoseDirection.Flat },
        { 4, GlucoseDirection.FortyFiveUp },
        { 5, GlucoseDirection.SingleUp },
        { 6, GlucoseDirection.DoubleDown },
        { 7, GlucoseDirection.DoubleUp },
    };

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// The patient's latest reading, or null when there is none: a patient whose app has not
    /// uploaded yet is listed with <c>CurrentGlucose</c> 0 and <c>CGTime</c> 0001-01-01.
    /// </summary>
    public SensorGlucose? Map(EversensePatientDatum patient)
    {
        try
        {
            if (patient.CurrentGlucose <= 0)
                return null;

            if (!UploaderTimestamp.TryParse(patient.CgTime, out var timestamp))
            {
                _logger.LogWarning("Could not parse Eversense timestamp: {Timestamp}", patient.CgTime);
                return null;
            }

            var mgdl = patient.Units == 1
                ? patient.CurrentGlucose * GlucoseConstants.MgdlPerMmol
                : (double)patient.CurrentGlucose;

            var direction = TrendDirections.GetValueOrDefault(patient.GlucoseTrend, GlucoseDirection.None);
            return Build(timestamp, mgdl, direction);
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
    public IReadOnlyList<SensorGlucose> MapHistory(IEnumerable<EversenseUserEvent> events)
    {
        var records = new List<SensorGlucose>();
        foreach (var e in events)
        {
            if (e.EventTypeId != EversenseConstants.SensorGlucoseEventType || e.Value <= 0)
                continue;

            if (!UploaderTimestamp.TryParse(e.EventDate, out var timestamp))
            {
                _logger.LogWarning("Could not parse Eversense event date: {EventDate}", e.EventDate);
                continue;
            }

            records.Add(Build(timestamp, e.Value, direction: null));
        }

        return records;
    }

    private static SensorGlucose Build(DateTimeOffset timestamp, double mgdl, GlucoseDirection? direction)
    {
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
}
