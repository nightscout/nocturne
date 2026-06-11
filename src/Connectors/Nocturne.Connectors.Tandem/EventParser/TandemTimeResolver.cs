using Nocturne.Connectors.Tandem.Configurations;

namespace Nocturne.Connectors.Tandem.EventParser;

/// <summary>
/// Converts raw Tandem pump timestamps (seconds since 2008-01-01, expressed in the user's local
/// wall-clock with no timezone attached) into UTC. Tandem event timestamps carry no offset — the
/// pump records local wall-clock — so, like <c>tconnectsync</c>'s <c>TIMEZONE_NAME</c> handling, the
/// connector applies the account's configured offset to recover UTC.
/// </summary>
public sealed class TandemTimeResolver(double timezoneOffsetHours)
{
    /// <summary>The configured UTC offset in whole minutes, for stamping on published records.</summary>
    public int OffsetMinutes { get; } = (int)Math.Round(timezoneOffsetHours * 60);

    private readonly TimeSpan _offset = TimeSpan.FromHours(timezoneOffsetHours);

    /// <summary>Converts a raw Tandem timestamp (seconds since the Tandem epoch) to UTC.</summary>
    public DateTime ToUtc(long rawSeconds)
    {
        // The raw value is local wall-clock seconds. Reading it as if UTC gives the wall-clock
        // instant; subtracting the configured offset recovers the true UTC instant.
        var wallClock = DateTimeOffset
            .FromUnixTimeSeconds(TandemConstants.TandemEpochUnixSeconds + rawSeconds)
            .UtcDateTime;
        return wallClock - _offset;
    }
}
