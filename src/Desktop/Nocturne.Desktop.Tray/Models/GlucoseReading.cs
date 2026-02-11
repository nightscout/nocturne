namespace Nocturne.Desktop.Tray.Models;

/// <summary>
/// Represents a single glucose reading from the Nocturne API.
/// </summary>
public sealed record GlucoseReading
{
    public double Sgv { get; init; }
    public double Mgdl { get; init; }
    public double? Mmol { get; init; }
    public string? Direction { get; init; }
    public int? Trend { get; init; }
    public double? TrendRate { get; init; }
    public double? Delta { get; init; }
    public long Mills { get; init; }
    public string? DateString { get; init; }
    public string? Device { get; init; }

    public DateTimeOffset Timestamp =>
        DateTimeOffset.FromUnixTimeMilliseconds(Mills);

    public TimeSpan Age =>
        DateTimeOffset.UtcNow - Timestamp;
}
