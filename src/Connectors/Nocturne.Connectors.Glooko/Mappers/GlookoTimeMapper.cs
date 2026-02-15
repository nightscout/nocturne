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

    public DateTime GetCorrectedGlookoTime(long unixSeconds)
    {
        var rawUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        return GetCorrectedGlookoTime(rawUtc);
    }

    public DateTime GetRawGlookoDate(string timestamp, string? pumpTimestamp)
    {
        return DateTime.Parse(
            !string.IsNullOrEmpty(pumpTimestamp) ? pumpTimestamp : timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind
        );
    }
}