using FluentAssertions;
using Nocturne.Core.Clinical.Treatments;
using Xunit;

namespace Nocturne.Core.Clinical.Tests.Treatments;

public class IobCobSeriesWindowTests
{
    private const long Minute = 60 * 1000;
    private const long Hour = 60 * Minute;
    private const long FirstTick = 1_705_312_800_000;
    private const long LastTick = FirstTick + 23 * Hour + 55 * Minute;

    private static readonly IobCobSeriesWindow Window = new(FirstTick, LastTick, 5 * Minute);

    [Fact]
    public void BolusesAndCarbs_AreReadFromEightHoursBeforeTheFirstTick() =>
        Window.InsulinReadStart.Should().Be(FirstTick - 8 * Hour);

    [Fact]
    public void TempBasals_AreReadFromADayBeforeTheInsulinLookBack() =>
        Window.TempBasalReadStart.Should().Be(FirstTick - 32 * Hour);

    [Fact]
    public void Treatments_AreReadToTheLastTick() =>
        Window.ReadEnd.Should().Be(LastTick);

    [Fact]
    public void Treatments_MayBeReadPastTheLastTickButNeverShortOfIt()
    {
        (Window with { ReadEnd = LastTick + 5 * Minute }).ReadEnd.Should().Be(LastTick + 5 * Minute);
        var shortOfIt = () => Window with { ReadEnd = LastTick - 1 };
        shortOfIt.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AWindowMovedLater_ReadsTreatmentsToItsNewLastTick() =>
        (Window with { LastTick = LastTick + Hour }).ReadEnd.Should().Be(LastTick + Hour);

    [Fact]
    public void DeviceSnapshots_AreReadFromHalfAnHourBeforeTheFirstTick() =>
        Window.DeviceReadStart.Should().Be(FirstTick - 30 * Minute);

    [Fact]
    public void DeviceSnapshots_AreReadToFiveMinutesPastTheLastTick() =>
        Window.DeviceReadEnd.Should().Be(LastTick + 5 * Minute);

    [Fact]
    public void TheTherapyTimelineAndOverrides_StartAtTheInsulinLookBack() =>
        Window.TherapyStart.Should().Be(FirstTick - 8 * Hour);

    [Fact]
    public void TheTherapyTimeline_EndsJustPastTheLastTick() =>
        Window.TimelineEnd.Should().Be(LastTick + 1);

    [Fact]
    public void Overrides_AreReadToTheLastTick() =>
        Window.OverrideEnd.Should().Be(LastTick);
}
