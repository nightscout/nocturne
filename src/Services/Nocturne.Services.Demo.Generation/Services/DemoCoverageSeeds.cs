using Nocturne.Core.Models;

namespace Nocturne.Services.Demo.Services;

public static class DemoCoverageSeeds
{
    public const string PrimaryCgmSerial = "demo-cgm";
    public const string ComparisonCgmSerial = "demo-cgm-secondary";

    public static Entry ApplyCompressionArtifact(Entry entry, DateTime time)
    {
        if (time.DayOfWeek is not (DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday))
            return entry;

        var glucose = (time.Hour, time.Minute) switch
        {
            (4, 0) => 110,
            (4, 5) => 90,
            (4, 10) => 58,
            (4, 15) => 82,
            (4, 20) => 108,
            _ => (double?)null,
        };

        if (glucose is { } value)
        {
            entry.Mgdl = value;
            entry.Sgv = value;
        }

        return entry;
    }

    public static Entry CreateComparisonReading(Entry source)
    {
        var mills = source.Mills + 120_000;
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;
        var variation = source.Mills / 300_000 % 5 - 2;
        var glucose = Math.Clamp((source.Sgv ?? source.Mgdl) + variation, 40, 400);

        return new Entry
        {
            Type = "sgv",
            Device = ComparisonCgmSerial,
            Mills = mills,
            Date = timestamp,
            DateString = timestamp.ToString("o"),
            Mgdl = glucose,
            Sgv = glucose,
            Direction = source.Direction,
            Delta = source.Delta,
            DataSource = source.DataSource,
            CreatedAt = timestamp.ToString("o"),
            ModifiedAt = timestamp,
        };
    }
}
