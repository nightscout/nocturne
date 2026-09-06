using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Tandem.Mappers;

/// <summary>Shared helpers and constants for the Tandem event-to-model mappers.</summary>
public static class TandemMapHelpers
{
    /// <summary>Data source / device id stamped on every record this connector publishes.</summary>
    public const string Source = DataSources.TConnectSyncConnector;

    /// <summary>Rounds an insulin amount to two decimals, mirroring tconnectsync's insulin_float_round.</summary>
    public static double Round2(double amount) => Math.Round(amount, 2);

    /// <summary>Converts a milliunit amount to whole units (rounded to two decimals).</summary>
    public static double MilliunitsToUnits(double milliunits) => Round2(milliunits / 1000.0);

    /// <summary>Unix milliseconds for a UTC timestamp.</summary>
    public static long ToMills(DateTime utc) =>
        new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
}
