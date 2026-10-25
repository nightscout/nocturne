using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Goldens pinning which sensor glucose writes the chokepoint relays as the Home Assistant hub's
/// <c>glucose_reading</c>: live creates on the canonical stream, never backfill, never a losing CGM.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class GlucoseReadingRelayGoldenTests(V4GoldenFixture fx)
{
    private static readonly DateTime T0 = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task LiveCreate_RelaysTheCreatedReadings()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        fx.GlucoseReadingCapture.Clear();

        await repo.BulkCreateAsync(
            new[]
            {
                new SensorGlucose { Timestamp = T0, Mgdl = 120, DataSource = "dexcom", LegacyId = "gr-a" },
                new SensorGlucose { Timestamp = T0.AddMinutes(5), Mgdl = 126, DataSource = "dexcom", LegacyId = "gr-b" },
            }, WriteOrigin.Live, CancellationToken.None);

        var relay = fx.GlucoseReadingCapture.Snapshot().Should().ContainSingle().Which;
        relay.Select(r => r.LegacyId).Should().BeEquivalentTo("gr-a", "gr-b");
        relay.Should().OnlyContain(r => r.Id != Guid.Empty, "the relay carries the stored records");
    }

    [Fact]
    public async Task LiveSingleCreate_RelaysTheReading()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        fx.GlucoseReadingCapture.Clear();

        var created = await repo.CreateAsync(
            new SensorGlucose { Timestamp = T0, Mgdl = 118, DataSource = "xdrip", SyncIdentifier = "gr-single" },
            WriteOrigin.Live, CancellationToken.None);

        fx.GlucoseReadingCapture.Snapshot().Should().ContainSingle()
            .Which.Should().ContainSingle().Which.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task LiveCreateOlderThanTheLastReading_RelaysNothing()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        await repo.BulkCreateAsync(
            new[] { new SensorGlucose { Timestamp = T0.AddMinutes(10), Mgdl = 140, DataSource = "dexcom", LegacyId = "gr-late-new" } },
            WriteOrigin.Live, CancellationToken.None);
        fx.GlucoseReadingCapture.Clear();

        await repo.BulkCreateAsync(
            new[]
            {
                new SensorGlucose { Timestamp = T0.AddMinutes(5), Mgdl = 135, DataSource = "dexcom", LegacyId = "gr-late-old" },
                new SensorGlucose { Timestamp = T0.AddMinutes(15), Mgdl = 142, DataSource = "dexcom", LegacyId = "gr-late-newer" },
            },
            WriteOrigin.Live, CancellationToken.None);

        fx.GlucoseReadingCapture.Snapshot().Should().ContainSingle()
            .Which.Should().ContainSingle().Which.LegacyId.Should().Be("gr-late-newer");
    }

    [Fact]
    public async Task LiveRestore_RelaysNothing()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var created = await repo.CreateAsync(
            new SensorGlucose { Timestamp = T0, Mgdl = 99, DataSource = "xdrip", SyncIdentifier = "gr-restore" },
            WriteOrigin.Live, CancellationToken.None);
        await repo.DeleteAsync(created.Id, WriteOrigin.Live, CancellationToken.None);
        fx.GlucoseReadingCapture.Clear();

        await repo.RestoreAsync(created.Id, WriteOrigin.Live, CancellationToken.None);

        fx.GlucoseReadingCapture.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task BackfillCreate_RelaysNothing()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        fx.GlucoseReadingCapture.Clear();

        await repo.BulkCreateAsync(
            new[] { new SensorGlucose { Timestamp = T0, Mgdl = 130, DataSource = "dexcom", LegacyId = "gr-bf" } },
            WriteOrigin.Backfill, CancellationToken.None);

        fx.GlucoseReadingCapture.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task LiveMultiStreamCreate_RelaysOnlyTheCanonicalStream()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var deviceRepo = scope.ServiceProvider.GetRequiredService<IPatientDeviceRepository>();

        var primary = await deviceRepo.CreateAsync(
            new PatientDevice { DeviceCategory = DeviceCategory.CGM, Manufacturer = "Dexcom", Model = "G7", Rank = 1 },
            WriteOrigin.Live, CancellationToken.None);
        var secondary = await deviceRepo.CreateAsync(
            new PatientDevice { DeviceCategory = DeviceCategory.CGM, Manufacturer = "Abbott", Model = "Libre 3", Rank = 2 },
            WriteOrigin.Live, CancellationToken.None);
        fx.GlucoseReadingCapture.Clear();

        await repo.BulkCreateAsync(
            new[]
            {
                new SensorGlucose { Timestamp = T0, Mgdl = 120, DataSource = "dexcom-connector", LegacyId = "gr-mc-p1", PatientDeviceId = primary.Id },
                new SensorGlucose { Timestamp = T0.AddMinutes(1), Mgdl = 145, DataSource = "libre-connector", LegacyId = "gr-mc-s1", PatientDeviceId = secondary.Id },
            },
            WriteOrigin.Live, CancellationToken.None);

        var relay = fx.GlucoseReadingCapture.Snapshot().Should().ContainSingle().Which;
        relay.Should().ContainSingle().Which.LegacyId.Should().Be("gr-mc-p1");
    }
}
