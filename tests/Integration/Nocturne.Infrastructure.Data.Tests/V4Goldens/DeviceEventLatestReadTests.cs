using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// The latest device event of a type reads the rows every other device event read serves: the
/// primary of a deduplicated group, and no deleted row.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class DeviceEventLatestReadTests
{
    private readonly V4GoldenFixture _fx;

    public DeviceEventLatestReadTests(V4GoldenFixture fx) => _fx = fx;

    private static readonly DateTime T0 = new(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Latest_IsThePrimaryNotTheNewerCopyDeduplicationHid()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceEventRepository>();
        var device = await scope.ServiceProvider.GetRequiredService<IPatientDeviceRepository>().CreateAsync(new PatientDevice
        {
            DeviceCategory = DeviceCategory.CGM,
            Manufacturer = "Dexcom",
            Model = "G7",
            CatalogId = "dexcom-g7",
            IsCurrent = true,
        }, WriteOrigin.Live, CancellationToken.None);
        var primary = await repo.CreateAsync(new DeviceEvent
        {
            Timestamp = T0,
            EventType = DeviceEventType.SensorStart,
            PatientDeviceId = device.Id,
            DataSource = DataSources.SensorSessionInference,
            SyncIdentifier = "inferred",
        }, WriteOrigin.Live, CancellationToken.None);
        await repo.CreateAsync(new DeviceEvent
        {
            Timestamp = T0.AddSeconds(10),
            EventType = DeviceEventType.SensorStart,
            DataSource = DataSources.XDrip,
            LegacyId = "recorded",
        }, WriteOrigin.Live, CancellationToken.None);

        var visible = await repo.GetAsync(T0.AddMinutes(-1), T0.AddMinutes(1), null, null, ct: CancellationToken.None);
        visible.Should().ContainSingle().Which.Id.Should().Be(primary.Id, "the test needs the newer row hidden");

        var latest = await repo.GetLatestByEventTypeAsync(DeviceEventType.SensorStart, asOf: null, CancellationToken.None);

        latest!.Id.Should().Be(primary.Id);
    }

    [Fact]
    public async Task Latest_SkipsADeletedRowNewerThanTheVisibleOne()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceEventRepository>();
        var kept = await repo.CreateAsync(new DeviceEvent
        {
            Timestamp = T0, EventType = DeviceEventType.InsulinChange, DataSource = DataSources.XDrip, LegacyId = "kept",
        }, WriteOrigin.Live, CancellationToken.None);
        var deleted = await repo.CreateAsync(new DeviceEvent
        {
            Timestamp = T0.AddHours(1), EventType = DeviceEventType.InsulinChange, DataSource = DataSources.XDrip, LegacyId = "deleted",
        }, WriteOrigin.Live, CancellationToken.None);
        await repo.DeleteAsync(deleted.Id, WriteOrigin.Live, CancellationToken.None);

        var latest = await repo.GetLatestByEventTypeAsync(DeviceEventType.InsulinChange, asOf: null, CancellationToken.None);

        latest!.Id.Should().Be(kept.Id);
    }
}
