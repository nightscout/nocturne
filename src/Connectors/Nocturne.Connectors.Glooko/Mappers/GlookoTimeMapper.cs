using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Core.Models.Timezones;

namespace Nocturne.Connectors.Glooko.Mappers;

public class GlookoTimeMapper
{
    private readonly GlookoConnectorConfiguration _config;
    private readonly ILogger _logger;
    private TimezoneTimeline? _timeline;

    public GlookoTimeMapper(GlookoConnectorConfiguration config, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     Supplies the tenant's timezone timeline for this sync. When set, fake-UTC timestamps are
    ///     converted per the zone in effect at each instant (honouring DST and travel/relocation).
    ///     When unset, the legacy single <see cref="GlookoConnectorConfiguration.TimezoneOffset"/> applies.
    /// </summary>
    public void UseTimeline(TimezoneTimeline timeline) => _timeline = timeline;

    public DateTime GetCorrectedGlookoTime(DateTime rawDate)
    {
        // Glooko stores fake UTC (local wall-clock stamped as UTC). The timeline interprets that
        // wall-clock in the zone in effect at the time; without one, fall back to the static offset.
        if (_timeline is { } timeline)
            return timeline.ToUtc(rawDate);

        return DateTime.SpecifyKind(rawDate.AddHours(-_config.TimezoneOffset), DateTimeKind.Utc);
    }

    /// <summary>
    ///     Converts a real UTC timestamp back to Glooko's fake-UTC format
    ///     (local time with Z suffix) for use in API request parameters.
    ///     This is the reverse of <see cref="GetCorrectedGlookoTime(DateTime)"/>.
    /// </summary>
    public DateTime ToGlookoTime(DateTime utcTime)
    {
        return utcTime.AddHours(_config.TimezoneOffset);
    }

    public DateTime GetCorrectedGlookoTime(long unixSeconds)
    {
        var rawUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        return GetCorrectedGlookoTime(rawUtc);
    }

    public DateTime GetRawGlookoDate(string timestamp, string? pumpTimestamp)
    {
        var dateString = !string.IsNullOrWhiteSpace(pumpTimestamp) ? pumpTimestamp : timestamp;

        if (string.IsNullOrWhiteSpace(dateString))
        {
            _logger.LogWarning("Received empty timestamp and pumpTimestamp from Glooko");
            throw new ArgumentException("Both timestamp and pumpTimestamp are empty or whitespace");
        }

        if (!DateTime.TryParse(
                dateString,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedDate))
        {
            _logger.LogWarning("Failed to parse Glooko date string: '{DateString}'", dateString);
            throw new FormatException($"Unable to parse date string: {dateString}");
        }

        return parsedDate;
    }
}