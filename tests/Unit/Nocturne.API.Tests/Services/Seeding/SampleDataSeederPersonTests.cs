using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.API.Services.Seeding;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.V4;
using Nocturne.Services.Demo.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>What seeding a demo person writes besides their glucose: the therapy of their row, and a seed that stopped part-way.</summary>
public class SampleDataSeederPersonTests : IDisposable
{
    private static readonly TenantContext Tenant = new(SampleDataSeederHarness.TenantId, "demo-person", "Person", true, true);

    private readonly SampleDataSeederHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private Task<SampleDataSeedResult> SeedAsync(string key, IDemoDataPurge? purge = null) =>
        _harness.CreateSeeder(purge).SeedAsync(
            Tenant, days: 3, ownerSubjectId: null, DataSources.DemoService, includeGlucose: true, DemoArchetypes.Find(key));

    [Theory]
    [InlineData("maddie")]
    [InlineData("rory")]
    public async Task A_person_on_injections_has_no_pump_mode_no_pump_and_no_pump_changes(string key)
    {
        await SeedAsync(key);

        await using var db = _harness.Db.CreateContext();
        (await db.StateSpans.CountAsync(s => s.Category == nameof(StateSpanCategory.PumpMode))).Should().Be(0);
        (await db.PatientDevices.Select(d => d.DeviceCategory).ToListAsync()).Should()
            .NotContain(nameof(DeviceCategory.InsulinPump)).And.Contain(nameof(DeviceCategory.CGM));
        var archetype = DemoArchetypes.Find(key)!;
        DemoDeviceLifecycle.GenerateSchedule(new DateTime(2026, 3, 14, 20, 0, 0), 30, DemoTestClock.Host, archetype)
            .Select(change => change.EventType).Distinct().Should().Equal(TreatmentTypes.SensorStart);
        DemoDeviceLifecycle.TrackerSpecsFor(archetype).Select(spec => spec.TriggerEventType).Should().Equal(TreatmentTypes.SensorStart);
    }

    [Fact]
    public async Task A_person_with_a_pump_and_no_loop_has_a_manual_pump_mode_and_a_pump_without_an_aid_algorithm()
    {
        await SeedAsync("paula");

        await using var db = _harness.Db.CreateContext();
        (await db.StateSpans.Where(s => s.Category == nameof(StateSpanCategory.PumpMode)).Select(s => s.State).Distinct().ToListAsync())
            .Should().Equal(nameof(PumpModeState.Manual));
        var pump = await db.PatientDevices.SingleAsync(d => d.DeviceCategory == nameof(DeviceCategory.InsulinPump));
        pump.AidAlgorithm.Should().BeNull();
        DemoDeviceLifecycle.TrackerSpecsFor(DemoArchetypes.Find("paula")).Select(s => s.TriggerEventType)
            .Should().Contain([TreatmentTypes.SiteChange, TreatmentTypes.InsulinChange, TreatmentTypes.PumpBatteryChange]);
    }

    [Fact]
    public async Task A_person_on_a_loop_has_an_automatic_pump_mode_and_a_pump_with_the_aid_algorithm()
    {
        await SeedAsync("liam");

        await using var db = _harness.Db.CreateContext();
        (await db.StateSpans.Where(s => s.Category == nameof(StateSpanCategory.PumpMode)).Select(s => s.State).ToListAsync())
            .Should().Contain(nameof(PumpModeState.Automatic));
        (await db.PatientDevices.SingleAsync(d => d.DeviceCategory == nameof(DeviceCategory.InsulinPump)))
            .AidAlgorithm.Should().Be(nameof(AidAlgorithm.Trio));
    }

    [Fact]
    public async Task A_seed_that_finds_no_timeline_removes_the_records_of_the_demo_service_first()
    {
        var purge = new Mock<IDemoDataPurge>();
        purge.Setup(p => p.HoldsDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        purge.Setup(p => p.PurgeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new DemoPurgeCounts(0, 0, 0));

        var seeded = await SeedAsync("sam", purge.Object);

        purge.Verify(p => p.PurgeAsync(It.IsAny<CancellationToken>()), Times.Once);
        seeded.Entries.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_tenant_that_holds_a_timeline_is_left_as_it_is_and_nothing_is_removed()
    {
        await SeedAsync("sam");
        var purge = new Mock<IDemoDataPurge>();
        purge.Setup(p => p.HoldsDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var again = await SeedAsync("sam", purge.Object);

        again.Should().Be(SampleDataSeedResult.None);
        purge.Verify(p => p.PurgeAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
