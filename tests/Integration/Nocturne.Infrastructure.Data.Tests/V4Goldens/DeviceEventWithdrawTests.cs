using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// The reads and the delete sensor session inference relies on, over deduplicated rows: an inferred
/// start and a recorded one linked into one group, and a stamped reading hidden behind an
/// unstamped primary.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class DeviceEventWithdrawTests
{
    private readonly V4GoldenFixture _fx;

    public DeviceEventWithdrawTests(V4GoldenFixture fx) => _fx = fx;

    private static readonly DateTime T0 = new(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);

    private static async Task<PatientDevice> RegisterCgmAsync(IServiceScope scope) =>
        await scope.ServiceProvider.GetRequiredService<IPatientDeviceRepository>().CreateAsync(new PatientDevice
        {
            DeviceCategory = DeviceCategory.CGM,
            Manufacturer = "Dexcom",
            Model = "G7",
            CatalogId = "dexcom-g7",
            IsCurrent = true,
        }, WriteOrigin.Live, CancellationToken.None);

    /// <summary>The inferred start is written first, so it is the group's primary and hides the recorded one.</summary>
    private static async Task<(DeviceEvent Inferred, DeviceEvent Recorded)> LinkedStartsAsync(
        IDeviceEventRepository repo, Guid patientDeviceId)
    {
        var inferred = await repo.CreateAsync(new DeviceEvent
        {
            Timestamp = T0,
            EventType = DeviceEventType.SensorStart,
            PatientDeviceId = patientDeviceId,
            DataSource = DataSources.SensorSessionInference,
            SyncIdentifier = "inferred",
        }, WriteOrigin.Live, CancellationToken.None);
        var recorded = await repo.CreateAsync(new DeviceEvent
        {
            Timestamp = T0.AddSeconds(10),
            EventType = DeviceEventType.SensorStart,
            DataSource = DataSources.XDrip,
            LegacyId = "recorded",
        }, WriteOrigin.Live, CancellationToken.None);
        return (inferred, recorded);
    }

    private async Task AssertOneGroupAsync(Guid tenant, string recordType)
    {
        var links = await _fx.QueryAsync(tenant, ctx =>
            ctx.LinkedRecords.AsNoTracking().Where(lr => lr.RecordType == recordType).ToListAsync());
        links.Select(l => l.CanonicalId).Distinct().Should().ContainSingle("the test needs the two linked");
    }

    [Fact]
    public async Task Withdrawing_AnInferredStart_LeavesTheRecordedCopyDeduplicationLinkedToIt()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceEventRepository>();
        var device = await RegisterCgmAsync(scope);
        var (_, recorded) = await LinkedStartsAsync(repo, device.Id);
        await AssertOneGroupAsync(tenant, "deviceevent");

        var withdrawn = await repo.WithdrawBySyncIdentifierAsync(
            DataSources.SensorSessionInference, "inferred", WriteOrigin.Live, CancellationToken.None);

        withdrawn.Should().Be(1);
        var visible = await repo.GetAsync(T0.AddMinutes(-1), T0.AddMinutes(1), null, null, ct: CancellationToken.None);
        visible.Should().ContainSingle().Which.Id.Should().Be(recorded.Id);
        (await repo.GetUserDeletedAsync(T0.AddMinutes(-1), T0.AddMinutes(1), [DeviceEventType.SensorStart], CancellationToken.None))
            .Should().BeEmpty("a system withdrawal must not dismiss the gap");
    }

    [Fact]
    public async Task AllCopies_IncludesTheRecordedStartHiddenBehindTheInferredPrimary()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceEventRepository>();
        var device = await RegisterCgmAsync(scope);
        var (inferred, recorded) = await LinkedStartsAsync(repo, device.Id);
        await AssertOneGroupAsync(tenant, "deviceevent");
        await repo.CreateAsync(new DeviceEvent
        {
            Timestamp = T0.AddSeconds(20), EventType = DeviceEventType.SiteChange, DataSource = DataSources.XDrip, LegacyId = "site",
        }, WriteOrigin.Live, CancellationToken.None);

        var copies = await repo.GetAllCopiesAsync(
            T0.AddMinutes(-1), T0.AddMinutes(1), [DeviceEventType.SensorStart, DeviceEventType.SensorChange], CancellationToken.None);

        copies.Select(e => e.Id).Should().Equal(inferred.Id, recorded.Id);
    }

    [Fact]
    public async Task UserDeleted_IsTheStartsTheUserDeletedOfTheGivenTypes()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceEventRepository>();
        var device = await RegisterCgmAsync(scope);
        var (inferred, _) = await LinkedStartsAsync(repo, device.Id);

        var site = await repo.CreateAsync(new DeviceEvent
        {
            Timestamp = T0.AddSeconds(20), EventType = DeviceEventType.SiteChange, DataSource = DataSources.XDrip, LegacyId = "site",
        }, WriteOrigin.Live, CancellationToken.None);

        scope.ServiceProvider.GetRequiredService<TestAuditContext>().AttributeToUser(Guid.NewGuid());
        await repo.DeleteAsync(inferred.Id, WriteOrigin.Live, CancellationToken.None);
        await repo.DeleteAsync(site.Id, WriteOrigin.Live, CancellationToken.None);

        var deleted = await repo.GetUserDeletedAsync(
            T0.AddMinutes(-1), T0.AddMinutes(1), [DeviceEventType.SensorStart], CancellationToken.None);

        deleted.Select(e => (e.Timestamp, e.DataSource, e.PatientDeviceId)).Should().Contain(
            (T0, DataSources.SensorSessionInference, device.Id));
        deleted.Should().NotContain(e => e.EventType == DeviceEventType.SiteChange);
    }

    [Fact]
    public async Task AttributedReadingTimes_IncludeAStampedReadingHiddenBehindAnUnstampedPrimary()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var device = await RegisterCgmAsync(scope);

        await repo.CreateAsync(new SensorGlucose
        {
            Timestamp = T0, Mgdl = 120, DataSource = DataSources.XDrip, LegacyId = "unstamped",
        }, WriteOrigin.Live, CancellationToken.None);
        await repo.CreateAsync(new SensorGlucose
        {
            Timestamp = T0.AddSeconds(10), Mgdl = 120, UtcOffset = 600, PatientDeviceId = device.Id,
            DataSource = DataSources.DexcomConnector, LegacyId = "stamped",
        }, WriteOrigin.Live, CancellationToken.None);
        await AssertOneGroupAsync(tenant, "sensorglucose");
        (await repo.GetAsync(T0.AddMinutes(-1), T0.AddMinutes(1), null, null, ct: CancellationToken.None, patientDeviceId: device.Id))
            .Should().BeEmpty("the stamped copy is hidden from ordinary reads");

        var times = await repo.GetAttributedReadingTimesAsync(device.Id, T0.AddMinutes(-1), T0.AddMinutes(1), CancellationToken.None);

        times.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Timestamp = T0.AddSeconds(10), UtcOffset = (int?)600 });
    }
}
