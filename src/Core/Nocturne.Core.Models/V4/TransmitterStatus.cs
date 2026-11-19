using System.Globalization;
using System.Text.Json;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// What a CGM transmitter last reported about its battery. xDrip+ uploads it for a Dexcom
/// transmitter ("Upload Dexcom battery") as an uploader status of type <see cref="UploaderType"/>,
/// and only when voltage A changes, so the latest one is current however old it is.
/// </summary>
/// <remarks>
/// Voltages and resistance are the numbers the transmitter reports and xDrip+ shows, unscaled
/// (a healthy G6 reads about 300 on voltage A); the uploader states no unit for them.
/// </remarks>
public sealed record TransmitterStatus
{
    /// <summary><see cref="UploaderSnapshot.Type"/> of an uploader status that describes a transmitter.</summary>
    public const string UploaderType = "DEXCOM_TRANSMITTER";

    /// <summary>When the status was uploaded.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>The uploader's name for the transmitter, e.g. "G6 Transmitter".</summary>
    public string? Device { get; init; }

    public decimal? VoltageA { get; init; }

    public decimal? VoltageB { get; init; }

    /// <summary>Reported only by transmitter firmware that measures it.</summary>
    public decimal? Resistance { get; init; }

    /// <summary>Days since the transmitter was activated.</summary>
    public decimal? Days { get; init; }

    /// <summary>The uploader's own verdict: <c>OK</c>, <c>LOW</c>, <c>BRICKED</c> or <c>UNKNOWN</c>.</summary>
    public string? Status { get; init; }

    public double? Temperature { get; init; }

    /// <summary>
    /// Reads a transmitter status off the uploader snapshot it was stored as, or
    /// <see langword="null"/> when <paramref name="snapshot"/> is not one.
    /// </summary>
    public static TransmitterStatus? From(UploaderSnapshot? snapshot)
    {
        if (snapshot is not { Type: UploaderType })
            return null;

        var reported = snapshot.AdditionalProperties;
        return new TransmitterStatus
        {
            Timestamp = snapshot.Timestamp,
            Device = snapshot.Device,
            VoltageA = Number(reported, "voltagea"),
            VoltageB = Number(reported, "voltageb"),
            Resistance = Number(reported, "resistance"),
            Days = Number(reported, "days"),
            Status = reported?.GetValueOrDefault("status") switch
            {
                JsonElement { ValueKind: JsonValueKind.String } text => text.GetString(),
                string text => text,
                _ => null,
            },
            Temperature = snapshot.Temperature,
        };
    }

    private static decimal? Number(Dictionary<string, object?>? reported, string key) =>
        reported?.GetValueOrDefault(key) switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetDecimal(out var value) => value,
            JsonElement { ValueKind: JsonValueKind.String } text
                when decimal.TryParse(text.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            decimal value => value,
            int value => value,
            long value => value,
            _ => null,
        };
}
