using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Twiist.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Twiist.Mappers;

/// <summary>
/// Maps decoded Twiist glucose blob records to V4 SensorGlucose models.
/// </summary>
public class TwiistGlucoseMapper(ILogger logger)
{
    /// <summary>
    /// Maps Twiist glucose direction strings to GlucoseDirection enum values.
    /// Direction strings come from the summary's lastGlucoseTrend field.
    /// </summary>
    private static readonly Dictionary<string, GlucoseDirection> TrendDirections = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Flat", GlucoseDirection.Flat },
        { "SingleUp", GlucoseDirection.SingleUp },
        { "SingleDown", GlucoseDirection.SingleDown },
        { "DoubleUp", GlucoseDirection.DoubleUp },
        { "DoubleDown", GlucoseDirection.DoubleDown },
        { "FortyFiveUp", GlucoseDirection.FortyFiveUp },
        { "FortyFiveDown", GlucoseDirection.FortyFiveDown },
        { "NotComputable", GlucoseDirection.NotComputable },
        { "RateOutOfRange", GlucoseDirection.RateOutOfRange },
        // Twiist also uses arrow Unicode names
        { "FLAT", GlucoseDirection.Flat },
        { "UP", GlucoseDirection.SingleUp },
        { "DOWN", GlucoseDirection.SingleDown },
        { "DOUBLE_UP", GlucoseDirection.DoubleUp },
        { "DOUBLE_DOWN", GlucoseDirection.DoubleDown },
        { "FORTY_FIVE_UP", GlucoseDirection.FortyFiveUp },
        { "FORTY_FIVE_DOWN", GlucoseDirection.FortyFiveDown },
    };

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Maps decoded glucose blob records to SensorGlucose V4 models.
    /// </summary>
    public IEnumerable<SensorGlucose> MapGlucoseRecords(
        IReadOnlyList<GlucoseRecord> records, string? lastTrend = null)
    {
        if (records.Count == 0) return [];

        var now = DateTime.UtcNow;

        return records
            .Where(r => r.Mgdl > 0)
            .Select(r => ConvertRecord(r, now))
            .Where(sg => sg != null)
            .Cast<SensorGlucose>()
            .OrderBy(sg => sg.Mills)
            .ToList();
    }

    private SensorGlucose? ConvertRecord(GlucoseRecord record, DateTime now)
    {
        try
        {
            var epochMs = new DateTimeOffset(record.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

            return new SensorGlucose
            {
                Id = Guid.CreateVersion7(),
                Timestamp = record.Timestamp,
                LegacyId = $"twiist_glucose_{epochMs}",
                Device = DataSources.TwiistConnector,
                DataSource = DataSources.TwiistConnector,
                Mgdl = record.Mgdl,
                Direction = GlucoseDirection.None,
                CreatedAt = now,
                ModifiedAt = now
            };
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Error converting Twiist glucose record at {Timestamp}", record.Timestamp);
            return null;
        }
    }

    /// <summary>
    /// Parses a Twiist trend string into a GlucoseDirection.
    /// </summary>
    public static GlucoseDirection ParseTrend(string? trend)
    {
        if (string.IsNullOrEmpty(trend))
            return GlucoseDirection.None;

        return TrendDirections.GetValueOrDefault(trend, GlucoseDirection.NotComputable);
    }
}
