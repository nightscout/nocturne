using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Clinical.Tests.Treatments;

public class DeviceReadingsTests
{
    private const long Minute = 60 * 1000;
    private const long Now = 1_750_000_200_000;

    private static DateTime At(long minutesFromNow) =>
        DateTimeOffset.FromUnixTimeMilliseconds(Now + minutesFromNow * Minute).UtcDateTime;

    private static DeviceReadings Of(ApsIobCobPoint[]? aps = null, PumpIobPoint[]? pump = null) =>
        DeviceReadings.Of(aps, pump);

    [Fact]
    public void APastInstant_DoesNotTakeALaterSnapshot_ButALiveOneDoes()
    {
        var readings = Of(aps: [new ApsIobCobPoint(At(2), 1.5, 10)]);

        Assert.Null(readings.At(Now, live: false).Iob);
        Assert.Null(readings.At(Now, live: false).Cob);
        Assert.Equal(1.5, readings.At(Now, live: true).Iob?.Iob);
        Assert.Equal(10, readings.At(Now, live: true).Cob?.Cob);
    }

    [Fact]
    public void ALiveInstant_TakesNothingStampedMoreThanTheSkewAllowanceLater()
    {
        var readings = Of(aps: [new ApsIobCobPoint(At(6), 1.5, 10)]);

        Assert.Null(readings.At(Now, live: true).Iob);
    }

    [Fact]
    public void ASnapshot_CountsForThirtyMinutes()
    {
        Assert.Equal(1.5, Of(aps: [new ApsIobCobPoint(At(-30), 1.5, null)]).At(Now, live: false).Iob?.Iob);
        Assert.Null(Of(aps: [new ApsIobCobPoint(At(-31), 1.5, null)]).At(Now, live: false).Iob);
    }

    [Fact]
    public void TheApsSnapshot_HoldsBeforeTheNewerPumpSnapshot()
    {
        var device = Of(aps: [new ApsIobCobPoint(At(-4), 1.5, null)], pump: [new PumpIobPoint(At(-1), 0.7, null)])
            .At(Now, live: false);

        Assert.Equal(DeviceSource.Aps, device.Iob?.Source);
        Assert.Equal(1.5, device.Iob?.Iob);
    }

    [Fact]
    public void EachComponent_TakesTheFirstSourceThatReportsIt()
    {
        var device = Of(aps: [new ApsIobCobPoint(At(-2), null, 12)], pump: [new PumpIobPoint(At(-3), 0.7, null)])
            .At(Now, live: false);

        Assert.Equal(DeviceSource.Pump, device.Iob?.Source);
        Assert.Equal(0.7, device.Iob?.Iob);
        Assert.Equal(DeviceSource.Aps, device.Cob?.Source);
        Assert.Equal(12, device.Cob?.Cob);
    }

    [Fact]
    public void OnlyTheNewestSnapshotOfASource_Counts()
    {
        var device = Of(aps: [new ApsIobCobPoint(At(-10), 1.5, 8), new ApsIobCobPoint(At(-2), null, 12)])
            .At(Now, live: false);

        Assert.Null(device.Iob);
        Assert.Equal(12, device.Cob?.Cob);
    }

    [Fact]
    public void APumpReportingOnlyBolusIob_CountsItAsItsTotal()
    {
        var device = Of(pump: [new PumpIobPoint(At(-2), null, 0.4)]).At(Now, live: false);

        Assert.Equal(0.4, device.Iob?.Iob);
        Assert.Null(device.Cob);
    }
}
