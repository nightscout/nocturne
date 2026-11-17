using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

[Trait("Category", "Unit")]
public class DeviceCatalogTests
{
    [Fact]
    public void GetAll_ShouldReturnAllEntries()
    {
        var entries = DeviceCatalog.GetAll();
        entries.Should().NotBeEmpty();
    }

    [Fact]
    public void GetAll_ShouldContainBothCgmsAndPumps()
    {
        var entries = DeviceCatalog.GetAll();
        entries.Should().Contain(e => e.Category == DeviceCategory.CGM);
        entries.Should().Contain(e => e.Category == DeviceCategory.InsulinPump);
    }

    [Fact]
    public void GetById_ShouldReturnMatchingEntry()
    {
        var entry = DeviceCatalog.GetById("dexcom-g7");
        entry.Should().NotBeNull();
        entry!.Name.Should().Be("Dexcom G7");
        entry.Category.Should().Be(DeviceCategory.CGM);
    }

    [Fact]
    public void GetById_ShouldReturnNullForUnknownId()
    {
        var entry = DeviceCatalog.GetById("nonexistent");
        entry.Should().BeNull();
    }

    [Fact]
    public void GetByCategory_ShouldFilterCorrectly()
    {
        var cgms = DeviceCatalog.GetByCategory(DeviceCategory.CGM);
        cgms.Should().OnlyContain(e => e.Category == DeviceCategory.CGM);
    }

    [Fact]
    public void GetByCategory_ForUnsupportedCategory_ShouldReturnEmpty()
    {
        var meters = DeviceCatalog.GetByCategory(DeviceCategory.GlucoseMeter);
        meters.Should().BeEmpty();
    }

    [Fact]
    public void CgmEntries_ShouldHaveCgmProperties()
    {
        var cgms = DeviceCatalog.GetByCategory(DeviceCategory.CGM);
        cgms.Where(e => e.Id != "custom-cgm")
            .Should().OnlyContain(e => e.Cgm != null);
    }

    [Fact]
    public void PumpEntries_ShouldNotHaveCgmProperties()
    {
        var pumps = DeviceCatalog.GetByCategory(DeviceCategory.InsulinPump);
        pumps.Should().OnlyContain(e => e.Cgm == null);
    }

    [Fact]
    public void CgmWithSeparateTransmitter_ShouldHaveTransmitterDuration()
    {
        var g6 = DeviceCatalog.GetById("dexcom-g6");
        g6.Should().NotBeNull();
        g6!.Cgm!.HasSeparateTransmitter.Should().BeTrue();
        g6.Cgm.TransmitterDurationDays.Should().NotBeNull();
    }

    [Fact]
    public void CgmWithoutSeparateTransmitter_ShouldNotHaveTransmitterDuration()
    {
        var g7 = DeviceCatalog.GetById("dexcom-g7");
        g7.Should().NotBeNull();
        g7!.Cgm!.HasSeparateTransmitter.Should().BeFalse();
        g7.Cgm.TransmitterDurationDays.Should().BeNull();
    }

    [Theory]
    [InlineData("dexcom-g7", 10, 30, 5)]
    [InlineData("dexcom-g7-15-day", 15, 60, 5)]
    [InlineData("libre-3", 14, 60, 1)]
    [InlineData("libre-3-plus", 15, 60, 1)]
    public void SensorWearAndWarmup_MatchLabelling(string id, int days, int warmupMinutes, int intervalMinutes)
    {
        var cgm = DeviceCatalog.GetById(id)!.Cgm!;
        cgm.SensorDurationDays.Should().Be(days);
        cgm.WarmupMinutes.Should().Be(warmupMinutes);
        cgm.UpdateIntervalMinutes.Should().Be(intervalMinutes);
        cgm.HasSeparateTransmitter.Should().BeFalse();
    }

    [Fact]
    public void OnlyTheDexcomG7Sensors_CarryAGracePeriod()
    {
        DeviceCatalog.GetByCategory(DeviceCategory.CGM)
            .Where(e => e.Cgm?.GraceHours is not null)
            .Select(e => (e.Id, e.Cgm!.GraceHours))
            .Should().BeEquivalentTo([("dexcom-g7", (int?)12), ("dexcom-g7-15-day", (int?)12), ("dexcom-one-plus", (int?)12)]);
    }

    [Fact]
    public void AllEntries_ShouldHaveUniqueIds()
    {
        var entries = DeviceCatalog.GetAll();
        entries.Select(e => e.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryCataloguedPump_HasADisposableSite()
    {
        DeviceCatalog.GetByCategory(DeviceCategory.InsulinPump)
            .Where(e => e.Pump is not null)
            .Should().OnlyContain(e => e.Pump!.Components.Any(c =>
                c.Lifecycle == ComponentLifecycle.Disposable && c.Provides.Contains(PumpCapability.Site)));
    }

    [Fact]
    public void EveryDisposable_HasALifespanAndAnEventThatReplacesIt()
    {
        DeviceCatalog.GetAll()
            .SelectMany(e => e.Pump?.Components ?? [])
            .Where(c => c.Lifecycle == ComponentLifecycle.Disposable)
            .Should().OnlyContain(c => c.LifespanHours > 0 && c.ResetBy.Count > 0);
    }

    [Fact]
    public void ComponentKeys_AreUniqueWithinEachPump()
    {
        DeviceCatalog.GetAll()
            .Where(e => e.Pump is not null)
            .Should().OnlyContain(e => e.Pump!.Components.Select(c => c.Key).Distinct().Count() == e.Pump.Components.Count);
    }

    [Fact]
    public void APartThatProvidesPower_SaysHow()
    {
        DeviceCatalog.GetAll()
            .SelectMany(e => e.Pump?.Components ?? [])
            .Where(c => c.Provides.Contains(PumpCapability.Power))
            .Should().OnlyContain(c => c.Power != null);
    }

    [Theory]
    [InlineData("omnipod-5", true)]
    [InlineData("medtrum-nano", true)]
    [InlineData("beta-bionics-mint", true)]
    [InlineData("equil", false)]
    [InlineData("accu-chek-solo", false)]
    [InlineData("tandem-tslim-x2", false)]
    public void IsPatch_MeansOneDisposableIsTheSiteAndTheReservoir(string id, bool isPatch)
    {
        DeviceCatalog.GetById(id)!.Pump!.IsPatch.Should().Be(isPatch);
    }
}
