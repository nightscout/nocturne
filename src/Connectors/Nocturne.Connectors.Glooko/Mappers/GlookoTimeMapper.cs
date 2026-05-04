using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Configurations;

namespace Nocturne.Connectors.Glooko.Mappers;

public class GlookoTimeMapper
{
    private readonly GlookoConnectorConfiguration _config;
    private readonly ILogger _logger;

    public GlookoTimeMapper(GlookoConnectorConfiguration config, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public DateTime GetCorrectedGlookoTime(DateTime rawDate)
    {
        var offsetHours = _config.TimezoneOffset;
        var corrected = rawDate.AddHours(-offsetHours);
        _logger.LogDebug(
            "GetCorrectedGlookoTime: Raw={Raw}, ConfigOffset={ConfigOffset}, Result={Result}",
            rawDate,
            _config.TimezoneOffset,
            corrected
        );
        return corrected;
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