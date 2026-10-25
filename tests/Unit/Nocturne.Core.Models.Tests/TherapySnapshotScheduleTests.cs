using Xunit;
using System.Text;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models.Tests;

public class TherapySnapshotScheduleTests
{
    private const long Epoch = 1_700_000_000_000L;
    private static readonly long MidnightUtc = Epoch - Epoch % 86_400_000L;

    private static long At(int hour) => MidnightUtc + hour * 3_600_000L;

    private static List<ScheduleEntry> TimeOnly(double night, double morning, double evening) =>
    [
        new() { Time = "22:00", Value = evening },
        new() { Time = "06:00", Value = morning },
        new() { Time = "00:00", Value = night },
    ];

    private static List<ScheduleEntry> WithSeconds(double night, double morning, double evening) =>
    [
        new() { Time = "22:00", Value = evening, TimeAsSeconds = 22 * 3600 },
        new() { Time = "06:00", Value = morning, TimeAsSeconds = 6 * 3600 },
        new() { Time = "00:00", Value = night, TimeAsSeconds = 0 },
    ];

    private static TherapySnapshot Snapshot(List<ScheduleEntry> entries) =>
        new(3, 75, 30, null, null, 0, entries, entries, entries);

    private static string Fingerprint(TherapySnapshot snapshot)
    {
        var sb = new StringBuilder();
        snapshot.AppendFingerprint(sb);
        return sb.ToString();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(22)]
    [InlineData(23)]
    public void EntriesCarryingOnlyTime_ResolveEveryScheduleAsTheSameEntriesWithTimeAsSeconds(int hour)
    {
        var timeOnly = Snapshot(TimeOnly(1.0, 0.8, 1.2));
        var withSeconds = Snapshot(WithSeconds(1.0, 0.8, 1.2));
        var at = At(hour);

        Assert.Equal(withSeconds.BasalRateAt(at), timeOnly.BasalRateAt(at));
        Assert.Equal(withSeconds.SensitivityAt(at), timeOnly.SensitivityAt(at));
        Assert.Equal(withSeconds.CarbRatioAt(at), timeOnly.CarbRatioAt(at));
    }

    [Fact]
    public void EntryWithOnlyTime_IsInForceFromItsTime()
    {
        var snapshot = Snapshot(TimeOnly(1.0, 0.8, 1.2));

        Assert.Equal(0.8, snapshot.BasalRateAt(At(12)));
        Assert.Equal(1.0, snapshot.BasalRateAt(At(3)));
    }

    [Fact]
    public void SnapshotsDifferingOnlyInTimeVersusTimeAsSeconds_FingerprintIdentically()
    {
        Assert.Equal(
            Fingerprint(Snapshot(WithSeconds(1.0, 0.8, 1.2))),
            Fingerprint(Snapshot(TimeOnly(1.0, 0.8, 1.2))));
    }

    [Fact]
    public void SnapshotsWithEntriesAtDifferentTimes_FingerprintDifferently()
    {
        var later = TimeOnly(1.0, 0.8, 1.2);
        later[1].Time = "07:00";

        Assert.NotEqual(Fingerprint(Snapshot(TimeOnly(1.0, 0.8, 1.2))), Fingerprint(Snapshot(later)));
    }
}
