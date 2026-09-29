using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.SetupHub;
using Nocturne.API.Services.SetupHub.Items;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.SetupHub;

public class SetupHubServiceTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OtherTenantId = Guid.CreateVersion7();

    private readonly string _database = $"setup_hub_{Guid.NewGuid()}";
    private readonly NocturneDbContext _db;

    public SetupHubServiceTests()
    {
        _db = ContextFor(TenantId);
        _db.Tenants.AddRange(
            new TenantEntity { Id = TenantId, Slug = "own", DisplayName = "Own" },
            new TenantEntity { Id = OtherTenantId, Slug = "other", DisplayName = "Other" });
        _db.SaveChanges();
    }

    private NocturneDbContext ContextFor(Guid tenantId)
    {
        var db = TestDbContextFactory.CreateInMemoryContext(_database);
        db.TenantId = tenantId;
        return db;
    }

    private static SetupHubService ServiceOver(NocturneDbContext db) =>
        new(db, new ISetupHubItem[]
        {
            // Registered out of order on purpose: the key decides the hub order.
            new AboutItem(db), new SharingItem(db), new TherapyItem(db),
            DevicesItemOver(db), new AlertsItem(db), new ConnectDataItem(db),
        });

    private SetupHubService Service => ServiceOver(_db);

    internal static DevicesItem DevicesItemOver(NocturneDbContext db)
    {
        var factory = new TestTenantDbContextFactory(db);
        return new DevicesItem(
            db,
            new PatientDeviceRepository(factory, NullLogger<PatientDeviceRepository>.Instance),
            new PatientInsulinRepository(factory, NullLogger<PatientInsulinRepository>.Instance));
    }

    private static SensorGlucoseEntity Reading(Guid tenantId) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenantId, Timestamp = DateTime.UtcNow, Mgdl = 110,
    };

    /// <summary>What makes the Devices item work: a CGM on record and the insulin question answered.</summary>
    private void AddCgmAndInsulin()
    {
        _db.PatientDevices.Add(new PatientDeviceEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DeviceCategory = "CGM", Manufacturer = "Dexcom", Model = "G7", IsCurrent = true,
        });
        _db.PatientInsulins.Add(new PatientInsulinEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, InsulinCategory = "RapidActing", Name = "Humalog", IsCurrent = true,
        });
    }

    private void Enrol()
    {
        _db.Tenants.Single(t => t.Id == TenantId).SetupHubEnrolledAt = DateTime.UtcNow;
        _db.SaveChanges();
    }

    [Fact]
    public async Task AFreshTenant_IsOfferedEveryItemInHubOrder_AllOpen()
    {
        var hub = await Service.GetAsync(CancellationToken.None);

        hub.Items.Select(i => i.Key).Should().Equal(Enum.GetValues<SetupHubItemKey>());
        hub.Items.Should().OnlyContain(i => i.State == SetupHubItemState.Open);
        hub.ResolvedCount.Should().Be(0);
        hub.OpenCount.Should().Be(6);
        hub.TotalCount.Should().Be(6);
    }

    [Fact]
    public async Task ConnectData_IsNotOfferedToATenantThatSavedASource()
    {
        _db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, ConnectorName = "dexcom",
        });
        await _db.SaveChangesAsync();

        var hub = await Service.GetAsync(CancellationToken.None);

        hub.Items.Should().NotContain(i => i.Key == SetupHubItemKey.ConnectData);
        hub.TotalCount.Should().Be(5);
    }

    [Fact]
    public async Task ConnectData_OnceListed_StaysListedUntilTheFirstReadingMakesItDone()
    {
        await Service.GetAsync(CancellationToken.None);

        _db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, ConnectorName = "dexcom",
        });
        await _db.SaveChangesAsync();

        var saved = await Service.GetAsync(CancellationToken.None);
        saved.Items.Should().ContainSingle(i => i.Key == SetupHubItemKey.ConnectData)
            .Which.State.Should().Be(SetupHubItemState.Open, "a saved source is not a working one");

        _db.SensorGlucose.Add(Reading(TenantId));
        await _db.SaveChangesAsync();

        var receiving = await Service.GetAsync(CancellationToken.None);
        receiving.Items.Single(i => i.Key == SetupHubItemKey.ConnectData)
            .State.Should().Be(SetupHubItemState.Done);
    }

    [Fact]
    public async Task Done_NeverReverts_WhenWhatMadeItDoneGoesAway()
    {
        await Service.GetAsync(CancellationToken.None);
        var reading = Reading(TenantId);
        _db.SensorGlucose.Add(reading);
        await _db.SaveChangesAsync();
        await Service.GetAsync(CancellationToken.None);

        _db.SensorGlucose.Remove(reading);
        await _db.SaveChangesAsync();

        var hub = await Service.GetAsync(CancellationToken.None);
        hub.Items.Single(i => i.Key == SetupHubItemKey.ConnectData)
            .State.Should().Be(SetupHubItemState.Done);
    }

    [Fact]
    public async Task AnExistingTenant_HasItsItemsResolvedFromItsCurrentData()
    {
        _db.SensorGlucose.Add(Reading(TenantId));
        _db.AlertRules.Add(new AlertRuleEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Name = "Low" });
        AddCgmAndInsulin();
        _db.TherapySettings.Add(new TherapySettingsEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, ProfileName = "Default", Timestamp = DateTime.UtcNow,
        });
        _db.PatientRecords.Add(new PatientRecordEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, DiabetesType = "Type1" });
        _db.Tenants.Single(t => t.Id == TenantId).ShareToken = "digest";
        await _db.SaveChangesAsync();

        var hub = await Service.GetAsync(CancellationToken.None);

        hub.Items.Should().NotContain(i => i.Key == SetupHubItemKey.ConnectData, "the tenant already has data");
        hub.Items.Should().HaveCount(5).And.OnlyContain(i => i.State == SetupHubItemState.Done);
        hub.ResolvedCount.Should().Be(5);
    }

    [Fact]
    public async Task Alerts_IgnoresRulesATrackerManagesAndDisabledRules()
    {
        _db.AlertRules.AddRange(
            new AlertRuleEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Name = "Sensor", ManagedBy = "tracker" },
            new AlertRuleEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Name = "Off", IsEnabled = false });
        await _db.SaveChangesAsync();

        var hub = await Service.GetAsync(CancellationToken.None);

        hub.Items.Single(i => i.Key == SetupHubItemKey.Alerts).State.Should().Be(SetupHubItemState.Open);
    }

    private MemberInviteEntity Invite(DateTime expiresAt, DateTime? revokedAt = null, int useCount = 0) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = TenantId, CreatedBySubjectId = Guid.CreateVersion7(),
        TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = expiresAt, RevokedAt = revokedAt, UseCount = useCount,
    };

    private async Task<SetupHubItemState> SharingStateAsync() =>
        (await Service.GetAsync(CancellationToken.None)).Items.Single(i => i.Key == SetupHubItemKey.Sharing).State;

    [Fact]
    public async Task Sharing_IgnoresARevokedOrLapsedInviteNobodyUsed()
    {
        _db.MemberInvites.AddRange(
            Invite(DateTime.UtcNow.AddDays(7), revokedAt: DateTime.UtcNow),
            Invite(DateTime.UtcNow.AddDays(-1)));
        await _db.SaveChangesAsync();

        (await SharingStateAsync()).Should().Be(SetupHubItemState.Open);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sharing_IsDoneByAnOpenInvite_OrOneSomeoneAccepted(bool accepted)
    {
        _db.MemberInvites.Add(accepted
            ? Invite(DateTime.UtcNow.AddDays(-1), useCount: 1)
            : Invite(DateTime.UtcNow.AddDays(7)));
        await _db.SaveChangesAsync();

        (await SharingStateAsync()).Should().Be(SetupHubItemState.Done);
    }

    [Fact]
    public async Task NotForMe_ResolvesTheItem_AndReopeningPutsItBack()
    {
        var set = await Service.SetStateAsync(SetupHubItemKey.Alerts, SetupHubItemState.NotForMe, CancellationToken.None);
        set.Items.Single(i => i.Key == SetupHubItemKey.Alerts).State.Should().Be(SetupHubItemState.NotForMe);
        set.ResolvedCount.Should().Be(1);
        set.OpenCount.Should().Be(5);

        var reread = await Service.GetAsync(CancellationToken.None);
        reread.Items.Single(i => i.Key == SetupHubItemKey.Alerts).State
            .Should().Be(SetupHubItemState.NotForMe, "revisiting the hub never clears a choice");

        var reopened = await Service.SetStateAsync(SetupHubItemKey.Alerts, SetupHubItemState.Open, CancellationToken.None);
        reopened.ResolvedCount.Should().Be(0);
    }

    [Fact]
    public async Task NotForMe_GivesWayToDone_WhenTheThingStartsWorking()
    {
        await Service.SetStateAsync(SetupHubItemKey.Devices, SetupHubItemState.NotForMe, CancellationToken.None);
        AddCgmAndInsulin();
        await _db.SaveChangesAsync();

        var hub = await Service.GetAsync(CancellationToken.None);

        hub.Items.Single(i => i.Key == SetupHubItemKey.Devices).State.Should().Be(SetupHubItemState.Done);
    }

    [Fact]
    public async Task SetState_RefusesDone_ADoneItem_AndAnUnlistedItem()
    {
        _db.SensorGlucose.Add(Reading(TenantId));
        AddCgmAndInsulin();
        await _db.SaveChangesAsync();

        await Service.Invoking(s => s.SetStateAsync(SetupHubItemKey.Alerts, SetupHubItemState.Done, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        await Service.Invoking(s => s.SetStateAsync(SetupHubItemKey.Devices, SetupHubItemState.NotForMe, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        await Service.Invoking(s => s.SetStateAsync(SetupHubItemKey.ConnectData, SetupHubItemState.NotForMe, CancellationToken.None))
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task AnotherTenant_NeitherSeesThisHub_NorResolvesItWithItsData()
    {
        await Service.SetStateAsync(SetupHubItemKey.Alerts, SetupHubItemState.NotForMe, CancellationToken.None);
        _db.SensorGlucose.Add(Reading(OtherTenantId));
        await _db.SaveChangesAsync();

        await using var other = ContextFor(OtherTenantId);
        var theirs = await ServiceOver(other).GetAsync(CancellationToken.None);
        theirs.Items.Should().NotContain(i => i.State == SetupHubItemState.NotForMe);

        var ours = await Service.GetAsync(CancellationToken.None);
        ours.Items.Single(i => i.Key == SetupHubItemKey.ConnectData).State
            .Should().Be(SetupHubItemState.Open, "another tenant's reading is not ours");
        _db.SetupHubItems.Should().OnlyContain(r => r.TenantId == TenantId);
    }

    [Fact]
    public async Task Strip_IsOfferedOnlyToAnEnrolledTenantWithSomethingOpen()
    {
        (await Service.GetAsync(CancellationToken.None)).ShowStrip.Should().BeFalse("the tenant onboarded before the hub");

        Enrol();
        (await Service.GetAsync(CancellationToken.None)).ShowStrip.Should().BeTrue();

        foreach (var key in Enum.GetValues<SetupHubItemKey>())
            await Service.SetStateAsync(key, SetupHubItemState.NotForMe, CancellationToken.None);
        (await Service.GetAsync(CancellationToken.None)).ShowStrip.Should().BeFalse("everything is resolved");
    }

    [Fact]
    public async Task DismissingTheStrip_HidesItUntilTheHubChanges()
    {
        Enrol();
        var before = await Service.GetAsync(CancellationToken.None);

        var dismissed = await Service.DismissStripAsync(before.Revision, CancellationToken.None);
        dismissed.ShowStrip.Should().BeFalse();
        (await Service.GetAsync(CancellationToken.None)).ShowStrip.Should().BeFalse();

        var changed = await Service.SetStateAsync(SetupHubItemKey.About, SetupHubItemState.NotForMe, CancellationToken.None);
        changed.Revision.Should().NotBe(before.Revision);
        changed.ShowStrip.Should().BeTrue();
    }
}
