using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Glooko.Mappers;

/// <summary>
///     Maps the two SSV2 app-logged exercise feeds to <see cref="Activity"/> records:
///     <c>exercises</c> (camelCase, duration in <b>seconds</b>, numeric intensity) and
///     <c>cgm/exercise_events</c> (snake_case, duration in <b>minutes</b>, string intensity). Both are
///     normalized to a single Activity shape: duration in <b>minutes</b> (what <c>Activity.Duration</c>
///     expects) and intensity as a string. Records are keyed on their stable Glooko guid (raw-timestamp
///     hash fallback) via <see cref="Activity.Id"/> — the dedup key carried through to the StateSpan's
///     OriginalId — so re-correction upserts in place; soft-delete aware.
/// </summary>
public class GlookoActivityMapper(string connectorSource, GlookoTimeMapper timeMapper, ILogger logger)
{
    private readonly string _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
    private readonly GlookoTimeMapper _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Maps the SSV2 <c>exercises</c> feed (camelCase). <c>duration</c> is in seconds → normalized to
    /// minutes; <c>intensity</c> is numeric → stringified.
    /// </summary>
    public List<Activity> MapSsv2Exercises(IReadOnlyList<GlookoSsv2Exercise> exercises)
    {
        var results = new List<Activity>();

        foreach (var ex in exercises)
        {
            try
            {
                if (ex.SoftDeleted) continue;

                var rawTimestamp = _timeMapper.GetRawGlookoDate(ex.Timestamp ?? string.Empty, null);
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);

                // Source duration is seconds; Activity.Duration is minutes.
                var durationMinutes = ex.Duration > 0 ? ex.Duration / 60.0 : (double?)null;
                var intensity = ex.Intensity?.ToString(CultureInfo.InvariantCulture);

                var legacyId = !string.IsNullOrEmpty(ex.Guid)
                    ? $"glooko_exercise_{ex.Guid}"
                    : GenerateLegacyId("ssv2_exercise", rawTimestamp, $"name:{ex.Name}_dur:{ex.Duration}");

                results.Add(BuildActivity(correctedTimestamp, legacyId, ex.Name, durationMinutes, intensity));
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "[{ConnectorSource}] Error mapping SSV2 exercise", _connectorSource);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} activities from SSV2 exercises feed", _connectorSource, results.Count);

        return results;
    }

    /// <summary>
    /// Maps the SSV2 <c>cgm/exercise_events</c> feed (snake_case). <c>duration</c> is already in minutes;
    /// <c>intensity</c> is a string ("light"/"moderate"/"vigorous"). Uses <c>display_time</c> (falling
    /// back to <c>event_time</c>). This feed carries no activity name.
    /// </summary>
    public List<Activity> MapSsv2ExerciseEvents(IReadOnlyList<GlookoSsv2ExerciseEvent> exerciseEvents)
    {
        var results = new List<Activity>();

        foreach (var ev in exerciseEvents)
        {
            try
            {
                if (ev.SoftDeleted) continue;

                // display_time wins over event_time: GetRawGlookoDate prefers its second arg when present.
                var rawTimestamp = _timeMapper.GetRawGlookoDate(ev.EventTime ?? string.Empty, ev.DisplayTime);
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);

                // Source duration is already minutes — no conversion.
                var durationMinutes = ev.Duration > 0 ? ev.Duration : (double?)null;

                var legacyId = !string.IsNullOrEmpty(ev.Guid)
                    ? $"glooko_exercise_event_{ev.Guid}"
                    : GenerateLegacyId("ssv2_exercise_event", rawTimestamp, $"dur:{ev.Duration}_int:{ev.Intensity}");

                results.Add(BuildActivity(correctedTimestamp, legacyId, name: null, durationMinutes, ev.Intensity));
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "[{ConnectorSource}] Error mapping SSV2 exercise event", _connectorSource);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} activities from SSV2 exercise_events feed", _connectorSource, results.Count);

        return results;
    }

    private Activity BuildActivity(
        DateTime timestamp, string legacyId, string? name, double? durationMinutes, string? intensity) =>
        new()
        {
            // Id is the stable dedup key: it flows through to the StateSpan's OriginalId on publish.
            Id = legacyId,
            Mills = new DateTimeOffset(timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            // "exercise" maps to StateSpanCategory.Exercise; the specific activity name is kept in Name.
            Type = "exercise",
            Name = name,
            Duration = durationMinutes,
            Intensity = intensity,
            EnteredBy = _connectorSource,
        };

    private static string GenerateLegacyId(string eventType, DateTime timestamp, string? additionalData = null)
    {
        var dataToHash = $"glooko_{eventType}_{timestamp.Ticks}_{additionalData ?? string.Empty}";
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(dataToHash));
        return $"glooko_{Convert.ToHexString(hashBytes).ToLowerInvariant()}";
    }
}
