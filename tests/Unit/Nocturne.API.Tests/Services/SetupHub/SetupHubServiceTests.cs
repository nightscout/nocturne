using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Core.Constants;
using Nocturne.API.Services.SetupHub;
using Nocturne.API.Services.SetupHub.Items;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
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

    private static SetupHubService ServiceOver(NocturneDbContext db)
    {
        var factory = new TestNocturneDbContextFactory(db);
        return new(factory, new TenantRepository(factory), new TenantMembershipRepository(factory), new ISetupHubItem[]
        {
            // Registered out of order on purpose: the key decides the hub order.
            new AboutItem(factory), new SharingItem(factory, new TenantRepository(factory), new TenantMembershipRepository(factory), new MemberInviteRepository(factory), new OAuthGrantRepository(factory), TimeProvider.System), new TherapyItem(factory),
            DevicesItemOver(db), new AlertsItem(factory, new TenantRepository(factory), new AlertRuleRepository(factory)), new ConnectDataItem(factory, new ConnectorConfigurationRepository(factory)),
        });
    }

    private SetupHubService Service => ServiceOver(_db);

    internal static DevicesItem DevicesItemOver(NocturneDbContext db)
    {
        var factory = new TestNocturneDbContextFactory(db);
        return new DevicesItem(
            factory,
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

    /// <summary>A rule with one test alert, confirmed received or not.</summary>
    private AlertRuleEntity RuleWithTest(bool confirmed, bool enabled = true, string? managedBy = null)
    {
        var rule = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Name = "Low", IsEnabled = enabled, ManagedBy = managedBy,
        };
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, AlertRuleId = rule.Id,
            StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow,
        };
        var channel = new AlertRuleChannelEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, AlertRuleId = rule.Id,
            ChannelType = ChannelType.Webhook, Destination = "https://example.invalid/alerts",
        };
        var instance = new AlertInstanceEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, AlertExcursionId = excursion.Id, Status = "test",
            TriggeredAt = DateTime.UtcNow, IsTest = true, ReceiptConfirmedAt = confirmed ? DateTime.UtcNow : null,
        };
        _db.AlertRules.Add(rule);
        _db.AlertRuleChannels.Add(channel);
        _db.AlertExcursions.Add(excursion);
        _db.AlertInstances.Add(instance);
        _db.AlertDeliveries.Add(new AlertDeliveryEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, AlertInstanceId = instance.Id, AlertRuleChannelId = channel.Id,
            ChannelType = channel.ChannelType, Destination = channel.Destination, Status = "delivered", IsTest = true,
        });
        return rule;
    }

    private void Enrol()
    {
        _db.Tenants.Single(t => t.Id == TenantId).SetupHubEnrolledAt = DateTime.UtcNow;
        _db.SaveChanges();
    }

    [Fact]
    public async Task AFreshTenant_IsOfferedEveryItemInHubOrder_AllOpen()
    {
        var hub = await Service.GetAsync(null, CancellationToken.None);

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

        var hub = await Service.GetAsync(null, CancellationToken.None);

        hub.Items.Should().NotContain(i => i.Key == SetupHubItemKey.ConnectData);
        hub.TotalCount.Should().Be(5);
    }

    [Fact]
    public async Task ConnectData_OnceListed_StaysListedUntilTheFirstReadingMakesItDone()
    {
        await Service.GetAsync(null, CancellationToken.None);

        _db.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, ConnectorName = "dexcom",
        });
        await _db.SaveChangesAsync();

        var saved = await Service.GetAsync(null, CancellationToken.None);
        saved.Items.Should().ContainSingle(i => i.Key == SetupHubItemKey.ConnectData)
            .Which.State.Should().Be(SetupHubItemState.Open, "a saved source is not a working one");

        _db.SensorGlucose.Add(Reading(TenantId));
        await _db.SaveChangesAsync();

        var receiving = await Service.GetAsync(null, CancellationToken.None);
        receiving.Items.Single(i => i.Key == SetupHubItemKey.ConnectData)
            .State.Should().Be(SetupHubItemState.Done);
    }

    [Fact]
    public async Task Done_NeverReverts_WhenWhatMadeItDoneGoesAway()
    {
        await Service.GetAsync(null, CancellationToken.None);
        var reading = Reading(TenantId);
        _db.SensorGlucose.Add(reading);
        await _db.SaveChangesAsync();
        await Service.GetAsync(null, CancellationToken.None);

        _db.SensorGlucose.Remove(reading);
        await _db.SaveChangesAsync();

        var hub = await Service.GetAsync(null, CancellationToken.None);
        hub.Items.Single(i => i.Key == SetupHubItemKey.ConnectData)
            .State.Should().Be(SetupHubItemState.Done);
    }

    [Fact]
    public async Task AnExistingTenant_HasItsItemsResolvedFromItsCurrentData()
    {
        _db.SensorGlucose.Add(Reading(TenantId));
        RuleWithTest(confirmed: true);
        AddCgmAndInsulin();
        _db.TherapySettings.Add(new TherapySettingsEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, ProfileName = "Default", Timestamp = DateTime.UtcNow,
            DataSource = DataSources.ManualEntry,
        });
        _db.PatientRecords.Add(new PatientRecordEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, DiabetesType = "Type1" });
        _db.Tenants.Single(t => t.Id == TenantId).ShareToken = "digest";
        await _db.SaveChangesAsync();

        var hub = await Service.GetAsync(null, CancellationToken.None);

        hub.Items.Should().NotContain(i => i.Key == SetupHubItemKey.ConnectData, "the tenant already has data");
        hub.Items.Should().HaveCount(5).And.OnlyContain(i => i.State == SetupHubItemState.Done);
        hub.ResolvedCount.Should().Be(5);
    }

    [Fact]
    public async Task Alerts_IsNotDoneByARule_ATestNobodyConfirmed_OrAConfirmedTestOfADisabledOrManagedRule()
    {
        _db.AlertRules.Add(new AlertRuleEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Name = "Rule only" });
        RuleWithTest(confirmed: false);
        RuleWithTest(confirmed: true, enabled: false);
        RuleWithTest(confirmed: true, managedBy: "tracker:x");
        await _db.SaveChangesAsync();

        var hub = await Service.GetAsync(null, CancellationToken.None);

        hub.Items.Single(i => i.Key == SetupHubItemKey.Alerts).State.Should().Be(SetupHubItemState.Open);
    }

    private void AddMember(bool system = false)
    {
        var subject = new SubjectEntity { Id = Guid.CreateVersion7(), Name = system ? "Public" : "Person", IsSystemSubject = system };
        _db.Subjects.Add(subject);
        _db.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, SubjectId = subject.Id });
    }

    private void AddGrant(string grantType, DateTime? expiresAt = null, DateTime? revokedAt = null) =>
        _db.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, SubjectId = Guid.CreateVersion7(), GrantType = grantType,
            ExpiresAt = expiresAt, RevokedAt = revokedAt,
        });

    private void AddInvite(DateTime expiresAt, DateTime? revokedAt = null, int useCount = 0) =>
        _db.MemberInvites.Add(new MemberInviteEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, TokenHash = "hash", ExpiresAt = expiresAt,
            RevokedAt = revokedAt, MaxUses = 1, UseCount = useCount,
        });

    private async Task<SetupHubItemState> SharingStateAsync() =>
        (await Service.GetAsync(null, CancellationToken.None)).Items.Single(i => i.Key == SetupHubItemKey.Sharing).State;

    [Fact]
    public async Task Sharing_StaysOpen_ForAnOwnerAloneWithTheirOwnAppsAndTokens()
    {
        AddMember();
        AddMember(system: true);
        AddGrant(OAuthGrantTypes.App);
        AddGrant(OAuthGrantTypes.Direct);
        await _db.SaveChangesAsync();

        (await SharingStateAsync()).Should().Be(SetupHubItemState.Open);
    }

    [Fact]
    public async Task Sharing_StaysOpen_ForInvitesAndGuestLinksThatCanNoLongerLetAnyoneIn()
    {
        var past = DateTime.UtcNow.AddHours(-1);
        var future = DateTime.UtcNow.AddDays(7);
        AddMember();
        AddInvite(future, revokedAt: past);
        AddInvite(past);
        AddGrant(OAuthGrantTypes.Guest, expiresAt: past);
        AddGrant(OAuthGrantTypes.Guest, expiresAt: future, revokedAt: past);
        await _db.SaveChangesAsync();

        (await SharingStateAsync()).Should().Be(SetupHubItemState.Open, "done never reverts, so only a live link may reach it");
    }

    [Fact]
    public async Task Sharing_AnInviteAndAGuestLinkCountUntilTheInstantTheyExpire()
    {
        var expiry = new DateTimeOffset(2026, 10, 29, 3, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(expiry.AddTicks(-1));
        AddMember();
        AddInvite(expiry.UtcDateTime);
        AddGrant(OAuthGrantTypes.Guest, expiresAt: expiry.UtcDateTime);
        await _db.SaveChangesAsync();
        var item = new SharingItem(new TestNocturneDbContextFactory(_db), new TenantRepository(new TestNocturneDbContextFactory(_db)), new TenantMembershipRepository(new TestNocturneDbContextFactory(_db)), new MemberInviteRepository(new TestNocturneDbContextFactory(_db)), new OAuthGrantRepository(new TestNocturneDbContextFactory(_db)), clock);

        (await item.WorksAsync(CancellationToken.None)).Should().BeTrue();

        clock.SetUtcNow(expiry);
        (await item.WorksAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData("invite")]
    [InlineData("accepted invite")]
    [InlineData("guest link")]
    [InlineData("follower")]
    [InlineData("public link")]
    [InlineData("second member")]
    public async Task Sharing_IsDone_OnceAnyoneElseIsLetIn(string how)
    {
        AddMember();
        switch (how)
        {
            case "invite": AddInvite(DateTime.UtcNow.AddDays(7)); break;
            case "accepted invite": AddInvite(DateTime.UtcNow.AddHours(-1), useCount: 1); break;
            case "guest link": AddGrant(OAuthGrantTypes.Guest, expiresAt: DateTime.UtcNow.AddHours(48)); break;
            case "follower": AddGrant(OAuthGrantTypes.Follower); break;
            case "public link": _db.Tenants.Single(t => t.Id == TenantId).ShareToken = "digest"; break;
            case "second member": AddMember(); break;
        }
        await _db.SaveChangesAsync();

        (await SharingStateAsync()).Should().Be(SetupHubItemState.Done);
    }

    [Fact]
    public async Task Sharing_JustMe_ResolvesIt_UntilTheOwnerSharesAfterAll()
    {
        var justMe = await Service.SetStateAsync(null, SetupHubItemKey.Sharing, SetupHubItemState.NotForMe, CancellationToken.None);
        justMe.Items.Single(i => i.Key == SetupHubItemKey.Sharing).State.Should().Be(SetupHubItemState.NotForMe);

        AddGrant(OAuthGrantTypes.Guest);
        await _db.SaveChangesAsync();

        (await SharingStateAsync()).Should().Be(SetupHubItemState.Done);
    }

    private TenantRoleEntity? _ownerRole;

    /// <summary>
    /// A person member, saved on its own so it joins after every earlier one: the save stamps the
    /// join time, and the first person to join is the helper.
    /// </summary>
    private Guid AddPerson(string name, bool owner)
    {
        if (owner && _ownerRole is null)
        {
            _ownerRole = new TenantRoleEntity
            {
                Id = Guid.CreateVersion7(), TenantId = TenantId, Name = "Owner", Slug = RoleSeeds.Owner, Permissions = [Scope.FullAccess],
            };
            _db.TenantRoles.Add(_ownerRole);
        }

        var subject = new SubjectEntity { Id = Guid.CreateVersion7(), Name = name };
        var member = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, SubjectId = subject.Id,
        };
        _db.Subjects.Add(subject);
        _db.TenantMembers.Add(member);
        if (owner)
            _db.TenantMemberRoles.Add(new TenantMemberRoleEntity { Id = Guid.CreateVersion7(), TenantMemberId = member.Id, TenantRoleId = _ownerRole!.Id });
        _db.SaveChanges();
        return subject.Id;
    }

    private void Answer(PatientRelationship relationship, bool handedOver = false)
    {
        var tenant = _db.Tenants.Single(t => t.Id == TenantId);
        tenant.PatientRelationship = relationship;
        tenant.HandedOverAt = handedOver ? DateTime.UtcNow : null;
    }

    [Fact]
    public async Task Sharing_ForAHelper_StaysOpenWithAnInviteTheRecipientHasNotAccepted()
    {
        Answer(PatientRelationship.Helper);
        AddPerson("Helper", owner: true);
        AddInvite(DateTime.UtcNow.AddDays(7));
        AddGrant(OAuthGrantTypes.Guest, expiresAt: DateTime.UtcNow.AddHours(48));
        await _db.SaveChangesAsync();

        (await SharingStateAsync()).Should().Be(SetupHubItemState.Open, "a helper shares by handing over, not by any link");
    }

    [Fact]
    public async Task Sharing_ForAHelper_IsNotDoneByAMemberWhoDoesNotOwnTheTenant()
    {
        Answer(PatientRelationship.Helper);
        AddPerson("Helper", owner: true);
        AddPerson("Carer", owner: false);
        await _db.SaveChangesAsync();

        (await SharingStateAsync()).Should().Be(SetupHubItemState.Open);
    }

    [Fact]
    public async Task Sharing_ForAHelper_IsDoneOnceTheRecipientOwnsTheTenant()
    {
        Answer(PatientRelationship.Helper);
        AddPerson("Helper", owner: true);
        AddPerson("Sam", owner: true);
        await _db.SaveChangesAsync();

        (await SharingStateAsync()).Should().Be(SetupHubItemState.Done);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PatientRelationship.Self)]
    [InlineData(PatientRelationship.Caregiver)]
    public async Task Handover_IsNull_ForATenantNoHelperSetUp(PatientRelationship? relationship)
    {
        if (relationship is { } answered)
            Answer(answered);
        var owner = AddPerson("Owner", owner: true);
        AddPerson("Partner", owner: true);
        await _db.SaveChangesAsync();

        (await Service.GetAsync(owner, CancellationToken.None)).Handover.Should().BeNull();
    }

    [Fact]
    public async Task Handover_BeforeTheRecipientJoins_TellsTheHelperTheyCannotStepDownYet()
    {
        Answer(PatientRelationship.Helper);
        var helper = AddPerson("Alex", owner: true);
        await _db.SaveChangesAsync();

        (await Service.GetAsync(helper, CancellationToken.None)).Handover.Should().Be(
            new SetupHubHandover("Alex", CallerIsHelper: true, RecipientJoined: false, RecipientAnswered: false, CanStepDown: false));
    }

    [Fact]
    public async Task Handover_OnceTheRecipientJoins_NamesTheHelperToTheRecipient_AndLetsOnlyTheHelperStepDown()
    {
        Answer(PatientRelationship.Helper);
        var helper = AddPerson("Alex", owner: true);
        var recipient = AddPerson("Sam", owner: true);
        await _db.SaveChangesAsync();

        (await Service.GetAsync(recipient, CancellationToken.None)).Handover.Should().Be(
            new SetupHubHandover("Alex", CallerIsHelper: false, RecipientJoined: true, RecipientAnswered: false, CanStepDown: false));
        (await Service.GetAsync(helper, CancellationToken.None)).Handover.Should().Be(
            new SetupHubHandover("Alex", CallerIsHelper: true, RecipientJoined: true, RecipientAnswered: false, CanStepDown: true));
    }

    [Fact]
    public async Task Handover_SurvivesTheRecipientsAnswer_SoTheHelperCanStillStepDown()
    {
        Answer(PatientRelationship.Self, handedOver: true);
        var helper = AddPerson("Alex", owner: true);
        var recipient = AddPerson("Sam", owner: true);
        await _db.SaveChangesAsync();

        (await Service.GetAsync(helper, CancellationToken.None)).Handover.Should().Be(
            new SetupHubHandover("Alex", CallerIsHelper: true, RecipientJoined: true, RecipientAnswered: true, CanStepDown: true));
        (await Service.GetAsync(recipient, CancellationToken.None)).Handover!.RecipientAnswered.Should().BeTrue();
    }

    [Fact]
    public async Task Handover_AfterTheHelperSteppedDown_StillNamesThemAsTheHelper()
    {
        Answer(PatientRelationship.Helper);
        AddPerson("Alex", owner: false);
        var recipient = AddPerson("Sam", owner: true);
        await _db.SaveChangesAsync();

        (await Service.GetAsync(recipient, CancellationToken.None)).Handover.Should().Be(
            new SetupHubHandover("Alex", CallerIsHelper: false, RecipientJoined: true, RecipientAnswered: false, CanStepDown: false),
            "the recipient is the only owner left, and is still not the helper");
    }

    [Fact]
    public async Task Alerts_IsDone_OnceATestOfAnEnabledRuleIsConfirmedReceived()
    {
        RuleWithTest(confirmed: true);
        await _db.SaveChangesAsync();

        var hub = await Service.GetAsync(null, CancellationToken.None);

        hub.Items.Single(i => i.Key == SetupHubItemKey.Alerts).State.Should().Be(SetupHubItemState.Done);
    }

    [Fact]
    public async Task NotForMe_ResolvesTheItem_AndReopeningPutsItBack()
    {
        var set = await Service.SetStateAsync(null, SetupHubItemKey.Alerts, SetupHubItemState.NotForMe, CancellationToken.None);
        set.Items.Single(i => i.Key == SetupHubItemKey.Alerts).State.Should().Be(SetupHubItemState.NotForMe);
        set.ResolvedCount.Should().Be(1);
        set.OpenCount.Should().Be(5);

        var reread = await Service.GetAsync(null, CancellationToken.None);
        reread.Items.Single(i => i.Key == SetupHubItemKey.Alerts).State
            .Should().Be(SetupHubItemState.NotForMe, "revisiting the hub never clears a choice");

        var reopened = await Service.SetStateAsync(null, SetupHubItemKey.Alerts, SetupHubItemState.Open, CancellationToken.None);
        reopened.ResolvedCount.Should().Be(0);
    }

    [Fact]
    public async Task NotForMe_GivesWayToDone_WhenTheThingStartsWorking()
    {
        await Service.SetStateAsync(null, SetupHubItemKey.Devices, SetupHubItemState.NotForMe, CancellationToken.None);
        AddCgmAndInsulin();
        await _db.SaveChangesAsync();

        var hub = await Service.GetAsync(null, CancellationToken.None);

        hub.Items.Single(i => i.Key == SetupHubItemKey.Devices).State.Should().Be(SetupHubItemState.Done);
    }

    [Fact]
    public async Task SetState_RefusesDone_ADoneItem_AndAnUnlistedItem()
    {
        _db.SensorGlucose.Add(Reading(TenantId));
        AddCgmAndInsulin();
        await _db.SaveChangesAsync();

        await Service.Invoking(s => s.SetStateAsync(null, SetupHubItemKey.Alerts, SetupHubItemState.Done, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        await Service.Invoking(s => s.SetStateAsync(null, SetupHubItemKey.Devices, SetupHubItemState.NotForMe, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        await Service.Invoking(s => s.SetStateAsync(null, SetupHubItemKey.ConnectData, SetupHubItemState.NotForMe, CancellationToken.None))
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task AnotherTenant_NeitherSeesThisHub_NorResolvesItWithItsData()
    {
        await Service.SetStateAsync(null, SetupHubItemKey.Alerts, SetupHubItemState.NotForMe, CancellationToken.None);
        _db.SensorGlucose.Add(Reading(OtherTenantId));
        await _db.SaveChangesAsync();

        await using var other = ContextFor(OtherTenantId);
        var theirs = await ServiceOver(other).GetAsync(null, CancellationToken.None);
        theirs.Items.Should().NotContain(i => i.State == SetupHubItemState.NotForMe);

        var ours = await Service.GetAsync(null, CancellationToken.None);
        ours.Items.Single(i => i.Key == SetupHubItemKey.ConnectData).State
            .Should().Be(SetupHubItemState.Open, "another tenant's reading is not ours");
        _db.SetupHubItems.Should().OnlyContain(r => r.TenantId == TenantId);
    }

    [Fact]
    public async Task Strip_IsOfferedOnlyToAnEnrolledTenantWithSomethingOpen()
    {
        (await Service.GetAsync(null, CancellationToken.None)).ShowStrip.Should().BeFalse("the tenant onboarded before the hub");

        Enrol();
        (await Service.GetAsync(null, CancellationToken.None)).ShowStrip.Should().BeTrue();

        foreach (var key in Enum.GetValues<SetupHubItemKey>())
            await Service.SetStateAsync(null, key, SetupHubItemState.NotForMe, CancellationToken.None);
        (await Service.GetAsync(null, CancellationToken.None)).ShowStrip.Should().BeFalse("everything is resolved");
    }

    [Fact]
    public async Task DismissingTheStrip_HidesItUntilTheHubChanges()
    {
        Enrol();
        var before = await Service.GetAsync(null, CancellationToken.None);

        var dismissed = await Service.DismissStripAsync(null, before.Revision, CancellationToken.None);
        dismissed.ShowStrip.Should().BeFalse();
        (await Service.GetAsync(null, CancellationToken.None)).ShowStrip.Should().BeFalse();

        var changed = await Service.SetStateAsync(null, SetupHubItemKey.About, SetupHubItemState.NotForMe, CancellationToken.None);
        changed.Revision.Should().NotBe(before.Revision);
        changed.ShowStrip.Should().BeTrue();
    }
}
