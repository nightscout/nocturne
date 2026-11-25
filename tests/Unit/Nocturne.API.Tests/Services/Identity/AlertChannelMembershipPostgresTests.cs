using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Chat;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// Alert channels against the membership of the member they go to, or of the member who added
/// them, on PostgreSQL under the runtime role and Row Level Security with a fixed clock: a direct
/// message reaches only a live member's linked account, a typed-in channel stops at the end of its
/// adder's access, and the end date and removal move that stop in the same save. A channel that
/// does not send never reaches the chat provider.
/// </summary>
[Trait("Category", "Integration")]
public class AlertChannelMembershipPostgresTests(AlertChannelMembershipPostgresTests.Database database)
    : IClassFixture<AlertChannelMembershipPostgresTests.Database>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("alert_channel_membership");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>Records every dispatch the chat provider makes to the bot service.</summary>
    private sealed class BotService : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    private sealed class Site(Guid tenant, string slug, Guid guest, SuppliedContextFactory contexts, FakeTimeProvider clock, BotService bot)
    {
        public Guid Tenant => tenant;
        public string Slug => slug;
        public Guid Guest => guest;
        public SuppliedContextFactory Contexts => contexts;
        public FakeTimeProvider Clock => clock;
        public BotService Bot => bot;
        public TenantMembershipRepository Members => new(contexts, AccountLinkRoutes.Off);
        public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;
    }

    /// <summary>A tenant with a caretaker, the guest, whose membership ends at <paramref name="accessEndsAt"/>.</summary>
    private async Task<Site> SeedAsync(string slug, DateTime? accessEndsAt)
    {
        var tenant = Guid.NewGuid();
        var fullSlug = $"{slug}-{tenant:N}";
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'alert-channel-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", fullSlug));
            await cmd.ExecuteNonQueryAsync();
        }

        var contexts = new SuppliedContextFactory(() => Context(tenant));
        var members = new TenantMembershipRepository(contexts, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(tenant, CancellationToken.None);
        var caretaker = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
        var guest = await new SubjectRepository(contexts, AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject($"{slug}-guest"), CancellationToken.None);
        (await members.AddMemberAsync(new NewTenantMember(tenant, guest, [caretaker], AccessEndsAt: accessEndsAt) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None))
            .Should().BeTrue();
        return new Site(tenant, fullSlug, guest, contexts, new FakeTimeProvider(Now), new BotService());
    }

    private static string Snowflake() => Random.Shared.NextInt64(100_000_000_000_000_000, 999_999_999_999_999_999).ToString();

    private async Task<string> LinkDiscordAsync(Site site, string? platformUser = null)
    {
        platformUser ??= Snowflake();
        var platform = new SuppliedContextFactory(() => Context(Guid.Empty));
        await new ChatIdentityDirectoryService(
                platform, new TenantMembershipRepository(platform, AccountLinkRoutes.Off), site.Clock,
                Mock.Of<ILogger<ChatIdentityDirectoryService>>())
            .CreateLinkAsync("discord", platformUser, site.Tenant, site.Guest, "home", "Home", CancellationToken.None);
        return platformUser;
    }

    /// <summary>A rule with one channel, as the rule editor saves it for <paramref name="saver"/>.</summary>
    private async Task<Guid> SaveRuleAsync(
        Site site, ChannelType type, string destination, Guid? saver = null, DateTime? saverAccessEndsAt = null,
        string? label = null)
    {
        List<AlertRuleChannelWrite> channels;
        await using (var db = Context(site.Tenant))
        {
            channels = await new AlertRuleChannelWriter(Mock.Of<ISecretEncryptionService>()).ToWritesAsync(
                [new CreateAlertRuleChannelRequest { ChannelType = type, Destination = destination, DestinationLabel = label }],
                saver, saverAccessEndsAt, db, CancellationToken.None);
        }

        return await new AlertRuleRepository(site.Contexts, site.Clock).CreateAsync(
            new NewAlertRule(
                new AlertRuleWrite(
                    "Urgent low", null, AlertConditionType.Threshold, """{"direction":"below","value":55}""",
                    RuleScopeClass.Low, AlertRuleSeverity.Critical, IsEnabled: true, SortOrder: 0,
                    AllowThroughDnd: true, AutoResolveEnabled: false, AutoResolveParams: null, ClientConfiguration: "{}"),
                channels),
            CancellationToken.None);
    }

    private async Task<AlertRuleChannelSnapshot> ChannelAsync(Site site, Guid ruleId) =>
        (await new AlertRuleRepository(site.Contexts, site.Clock).GetChannelsAsync([ruleId], CancellationToken.None))[ruleId]
        .Single();

    /// <summary>Fires the rule once through its channel and returns the delivery row the fire left.</summary>
    private async Task<AlertDeliveryEntity> FireAsync(Site site, Guid ruleId)
    {
        var instanceId = Guid.CreateVersion7();
        await using (var db = Context(site.Tenant))
        {
            var excursion = new AlertExcursionEntity
            {
                Id = Guid.CreateVersion7(), TenantId = site.Tenant, AlertRuleId = ruleId, StartedAt = site.UtcNow,
            };
            db.AlertExcursions.Add(excursion);
            db.AlertInstances.Add(new AlertInstanceEntity
            {
                Id = instanceId, TenantId = site.Tenant, AlertExcursionId = excursion.Id, TriggeredAt = site.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await Delivery(site).DispatchAsync(instanceId, [await ChannelAsync(site, ruleId)], new AlertPayload
        {
            AlertType = AlertConditionType.Threshold,
            RuleName = "Urgent low",
            Severity = AlertRuleSeverity.Critical,
            TenantId = site.Tenant,
            GlucoseValue = 52,
            Trend = null,
            TrendRate = null,
            ReadingTimestamp = site.UtcNow,
            SubjectName = "Synthetic",
            ExcursionId = Guid.NewGuid(),
            InstanceId = instanceId,
            ActiveExcursionCount = 1,
        }, CancellationToken.None);

        await using var read = Context(site.Tenant);
        return await read.AlertDeliveries.AsNoTracking().SingleAsync(d => d.AlertInstanceId == instanceId);
    }

    private static AlertDeliveryService Delivery(Site site)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WEB_URL"] = "http://nocturne-web.test",
                ["INSTANCE_KEY"] = "test-instance-key",
            })
            .Build();
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(a => a.TenantId).Returns(site.Tenant);
        tenantAccessor.Setup(a => a.Context).Returns(new TenantContext(site.Tenant, site.Slug, "Site", true, IsDemo: false));
        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient("ChatBot")).Returns(() => new HttpClient(site.Bot, disposeHandler: false));

        var services = new ServiceCollection()
            .AddSingleton(new ChatBotProvider(http.Object, configuration, tenantAccessor.Object, NullLogger<ChatBotProvider>.Instance))
            .BuildServiceProvider();

        return new AlertDeliveryService(
            site.Contexts,
            site.Members,
            tenantAccessor.Object,
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IEngineAlarmRelay>(),
            services,
            NullLogger<AlertDeliveryService>.Instance,
            site.Clock);
    }

    private async Task<Guid> MemberIdAsync(Site site) =>
        (await site.Members.FindMemberAsync(site.Tenant, site.Guest, CancellationToken.None))!.Id;

    private async Task SetAccessEndAsync(Site site, DateTime? accessEndsAt)
    {
        var memberId = await MemberIdAsync(site);
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(site.Contexts, site.Tenant);
        var members = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);
        var (outcome, _) = await unit.CommitAsync(async ct =>
        {
            var change = await members.SetAccessEndUnlessOwnerAsync(site.Tenant, memberId, accessEndsAt, site.UtcNow, ct);
            if (change == OwnerGuardedChange.Changed && accessEndsAt is { } end)
                await MemberCreations.CapAsync(unit, site.Guest, end, site.UtcNow, ct);
            await unit.Context.SaveChangesAsync(ct);
            return (change, 0);
        });
        outcome.Should().Be(OwnerGuardedChange.Changed);
    }

    private async Task RemoveGuestAsync(Site site)
    {
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(site.Contexts, site.Tenant);
        var members = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);
        var member = (await members.FindMemberAsync(site.Tenant, site.Guest, CancellationToken.None))!;
        var (outcome, _) = await unit.CommitAsync(ct => MembershipRemoval.StageAsync(
            unit, members, new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
            site.Tenant, member, MembershipEndHow.Removed, site.UtcNow, ct));
        outcome.Should().Be(OwnerGuardedChange.Changed);
    }

    [Fact]
    public async Task A_direct_message_to_a_member_who_is_removed_is_not_sent_and_still_names_them()
    {
        var site = await SeedAsync("dm-removed", accessEndsAt: null);
        var account = await LinkDiscordAsync(site);
        var rule = await SaveRuleAsync(site, ChannelType.DiscordDm, account);
        (await FireAsync(site, rule)).Status.Should().Be("pending", "the bot service has the dispatch");
        site.Bot.Bodies.Should().ContainSingle();

        await RemoveGuestAsync(site);
        var delivery = await FireAsync(site, rule);

        site.Bot.Bodies.Should().ContainSingle("the provider is not called for a channel with no recipient");
        delivery.Status.Should().Be("failed");
        delivery.LastError.Should().StartWith("No recipient");
        (await ChannelAsync(site, rule)).RecipientSubjectId.Should().Be(site.Guest, "removal deleted the link, not the channel");
    }

    [Fact]
    public async Task A_direct_message_to_a_member_whose_access_has_ended_is_not_sent_until_the_end_moves_later()
    {
        var site = await SeedAsync("dm-ended", Now.UtcDateTime.AddMinutes(1));
        var rule = await SaveRuleAsync(site, ChannelType.DiscordDm, await LinkDiscordAsync(site));
        await FireAsync(site, rule);
        site.Bot.Bodies.Should().ContainSingle();

        site.Clock.Advance(TimeSpan.FromMinutes(1));
        var ended = await FireAsync(site, rule);
        site.Bot.Bodies.Should().ContainSingle("the member's access ended");
        ended.LastError.Should().StartWith("No recipient");

        await SetAccessEndAsync(site, Now.UtcDateTime.AddDays(7));
        await FireAsync(site, rule);
        site.Bot.Bodies.Should().HaveCount(2, "the channel sends again with no edit once the access is live");
    }

    [Fact]
    public async Task A_direct_message_to_an_account_no_member_has_linked_is_not_sent()
    {
        var site = await SeedAsync("dm-unlinked", accessEndsAt: null);
        var rule = await SaveRuleAsync(site, ChannelType.DiscordDm, Snowflake());

        var delivery = await FireAsync(site, rule);

        site.Bot.Bodies.Should().BeEmpty();
        delivery.Status.Should().Be("failed");
        delivery.LastError.Should().StartWith("No recipient");
    }

    [Fact]
    public async Task A_typed_in_channel_added_by_a_member_with_an_end_date_is_sent_until_that_date()
    {
        var ends = Now.UtcDateTime.AddMinutes(1);
        var site = await SeedAsync("typed-ends", ends);
        var rule = await SaveRuleAsync(site, ChannelType.DiscordChannel, Snowflake(), site.Guest, ends);
        (await ChannelAsync(site, rule)).StopsAt.Should().Be(ends);
        await FireAsync(site, rule);
        site.Bot.Bodies.Should().ContainSingle();

        site.Clock.Advance(TimeSpan.FromMinutes(1));
        var stopped = await FireAsync(site, rule);

        site.Bot.Bodies.Should().ContainSingle("the channel stopped when its adder's access ended");
        stopped.Status.Should().Be("failed");
        stopped.LastError.Should().StartWith("Stopped at");
    }

    [Fact]
    public async Task Shortening_the_end_date_lowers_the_stop_and_extending_or_clearing_it_does_not_raise_it()
    {
        var site = await SeedAsync("typed-shorten", Now.UtcDateTime.AddDays(7));
        var rule = await SaveRuleAsync(site, ChannelType.DiscordChannel, Snowflake(), site.Guest, Now.UtcDateTime.AddDays(7));

        await SetAccessEndAsync(site, Now.UtcDateTime.AddDays(1));
        (await ChannelAsync(site, rule)).StopsAt.Should().Be(Now.UtcDateTime.AddDays(1));

        await SetAccessEndAsync(site, Now.UtcDateTime.AddDays(30));
        (await ChannelAsync(site, rule)).StopsAt.Should().Be(Now.UtcDateTime.AddDays(1));

        await SetAccessEndAsync(site, null);
        (await ChannelAsync(site, rule)).StopsAt.Should().Be(Now.UtcDateTime.AddDays(1));
    }

    [Fact]
    public async Task Giving_a_member_an_end_date_stops_the_channels_they_added_at_that_date()
    {
        var site = await SeedAsync("typed-set", accessEndsAt: null);
        var rule = await SaveRuleAsync(site, ChannelType.DiscordChannel, Snowflake(), site.Guest);
        (await ChannelAsync(site, rule)).StopsAt.Should().BeNull();

        await SetAccessEndAsync(site, Now.UtcDateTime.AddDays(2));

        (await ChannelAsync(site, rule)).StopsAt.Should().Be(Now.UtcDateTime.AddDays(2));
    }

    [Fact]
    public async Task Removing_a_member_with_an_end_date_stops_their_channels_at_once()
    {
        var site = await SeedAsync("typed-remove-ends", Now.UtcDateTime.AddDays(7));
        var rule = await SaveRuleAsync(site, ChannelType.DiscordChannel, Snowflake(), site.Guest, Now.UtcDateTime.AddDays(7));

        await RemoveGuestAsync(site);

        (await ChannelAsync(site, rule)).StopsAt.Should().Be(Now.UtcDateTime);
        (await FireAsync(site, rule)).LastError.Should().StartWith("Stopped at");
        site.Bot.Bodies.Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_a_member_with_no_end_date_leaves_their_channels()
    {
        var site = await SeedAsync("typed-remove-lasting", accessEndsAt: null);
        var rule = await SaveRuleAsync(site, ChannelType.DiscordChannel, Snowflake(), site.Guest);

        await RemoveGuestAsync(site);

        (await ChannelAsync(site, rule)).StopsAt.Should().BeNull();
        await FireAsync(site, rule);
        site.Bot.Bodies.Should().ContainSingle();
    }

    /// <summary>
    /// The notices one reconcile of <paramref name="ruleIds"/> raises for an owner who holds none, by
    /// channel id, with the metadata each carries.
    /// </summary>
    private async Task<Dictionary<string, Dictionary<string, object>?>> RaisedNoticesAsync(Site site, params Guid[] ruleIds)
    {
        var owner = Guid.CreateVersion7().ToString();
        var raised = new Dictionary<string, Dictionary<string, object>?>();
        var notifications = new Mock<Nocturne.Core.Contracts.Notifications.IInAppNotificationService>();
        notifications.Setup(n => n.GetActiveNotificationsAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        notifications.Setup(n => n.CreateNotificationUnlessActiveAsync(
                owner, NotificationSubjectNaming.AlertChannelNotSending, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Nocturne.Core.Models.NotificationCategory?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string sourceId, Nocturne.Core.Models.NotificationCategory? _, string? _,
                string? _, Dictionary<string, object>? metadata, CancellationToken _) => raised[sourceId] = metadata)
            .ReturnsAsync(true);
        var owners = new Mock<Nocturne.Core.Contracts.Identity.ITenantOwnerResolver>();
        owners.Setup(o => o.GetOwnerSubjectIdsAsync(site.Tenant, It.IsAny<CancellationToken>())).ReturnsAsync([owner]);
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(a => a.TenantId).Returns(site.Tenant);
        AlertRuleSnapshot[] enabled =
        [
            .. ruleIds.Select(rule => new AlertRuleSnapshot(
                rule, site.Tenant, "Urgent low", AlertConditionType.Threshold, "{}", AlertRuleSeverity.Critical, "{}", 0, false, null)),
        ];
        await new AlertChannelNotice(
                new AlertRuleRepository(site.Contexts, site.Clock), site.Members,
                new SubjectRepository(site.Contexts, AccountLinkRoutes.Off), site.Contexts, owners.Object,
                notifications.Object, tenantAccessor.Object, new AlertChannelNoticeLog(site.Clock),
                NullLogger<AlertChannelNotice>.Instance)
            .CheckAsync(enabled, site.UtcNow, CancellationToken.None);
        return raised;
    }

    private async Task DeleteGuestAsync(Site site)
    {
        var contexts = new SuppliedContextFactory(() => Context(Guid.Empty));
        var deletion = new SubjectDeletion(
            contexts,
            new SubjectRepository(contexts, AccountLinkRoutes.Off),
            new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
            new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
            Mock.Of<IAuthAuditService>(),
            site.Clock,
            InertTeamStaff.Announcer(),
            InertTeamStaff.Sites(),
            NullLogger<SubjectDeletion>.Instance);
        (await deletion.DeleteAsync(site.Guest, evenLastOwner: false, CancellationToken.None)).Change
            .Should().BeOfType<SubjectChange.Changed>();
    }

    [Fact]
    public async Task After_a_deletion_the_owner_is_told_of_the_persons_channels_without_their_name()
    {
        var site = await SeedAsync("notice-deleted", accessEndsAt: null);
        var toGuest = await SaveRuleAsync(site, ChannelType.InApp, site.Guest.ToString(), label: "Guest's app");
        var added = await SaveRuleAsync(site, ChannelType.DiscordChannel, Snowflake(), site.Guest, label: "Guest's server");
        (await RaisedNoticesAsync(site, toGuest, added)).Should().BeEmpty("both channels send while the guest is a member");

        await DeleteGuestAsync(site);
        var raised = await RaisedNoticesAsync(site, toGuest, added);

        raised.Keys.Should().BeEquivalentTo(
            [(await ChannelAsync(site, toGuest)).Id.ToString(), (await ChannelAsync(site, added)).Id.ToString()],
            "the deletion leaves a channel to the guest with no recipient and stops the channel the guest added");
        foreach (var metadata in raised.Values)
        {
            metadata.Should().NotContainKeys(
                NotificationSubjectNaming.MemberSubjectIdKey, NotificationSubjectNaming.AddedBySubjectIdKey, "destinationLabel");
            metadata!.Values.Select(v => v.ToString()).Should().NotContain(site.Guest.ToString());
        }
    }

    [Fact]
    public async Task After_a_deletion_a_direct_message_saved_before_its_recipient_was_recorded_is_told_without_its_label()
    {
        var site = await SeedAsync("notice-deleted-dm", accessEndsAt: null);
        var rule = await SaveRuleAsync(site, ChannelType.DiscordDm, await LinkDiscordAsync(site), label: "Guest's Discord");
        var channel = (await ChannelAsync(site, rule)).Id;
        await using (var db = Context(site.Tenant))
        {
            await db.AlertRuleChannels.Where(c => c.Id == channel)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RecipientSubjectId, (Guid?)null));
        }

        await DeleteGuestAsync(site);
        var raised = await RaisedNoticesAsync(site, rule);

        raised.Keys.Should().Equal([channel.ToString()], "the deletion took the link the direct message went through");
        raised[channel.ToString()].Should().NotContainKeys(
            NotificationSubjectNaming.MemberSubjectIdKey, NotificationSubjectNaming.AddedBySubjectIdKey, "destinationLabel");
    }

    [Fact]
    public async Task After_a_removal_the_owner_is_told_of_the_channel_to_the_member_by_their_name()
    {
        var site = await SeedAsync("notice-removed", accessEndsAt: null);
        var toGuest = await SaveRuleAsync(site, ChannelType.InApp, site.Guest.ToString(), label: "Guest's app");

        await RemoveGuestAsync(site);
        var raised = await RaisedNoticesAsync(site, toGuest);

        raised[(await ChannelAsync(site, toGuest)).Id.ToString()].Should()
            .Contain(NotificationSubjectNaming.MemberSubjectIdKey, site.Guest.ToString())
            .And.Contain("destinationLabel", "Guest's app");
    }

    /// <summary>Replaces the guest's ended membership with a new one, as an accepted invite does.</summary>
    private async Task RejoinAsync(Site site)
    {
        var caretaker = (await site.Members.FindRoleIdBySlugAsync(site.Tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(site.Contexts, site.Tenant);
        var members = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);
        await unit.CommitAsync(async ct =>
        {
            (await MembershipRemoval.ReplaceEndedAsync(
                unit, members, new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
                site.Tenant, site.Guest, site.UtcNow, ct)).Should().NotBeNull();
            (await members.AddMemberAsync(new NewTenantMember(site.Tenant, site.Guest, [caretaker]) { How = MembershipJoinHow.Direct, By = null }, ct)).Should().BeTrue();
            await unit.Context.SaveChangesAsync(ct);
        });
    }

    [Fact]
    public async Task A_member_who_joins_again_gets_no_direct_message_until_they_link_their_account_again()
    {
        var site = await SeedAsync("dm-rejoin", Now.UtcDateTime.AddMinutes(1));
        var account = await LinkDiscordAsync(site);
        var rule = await SaveRuleAsync(site, ChannelType.DiscordDm, account);
        site.Clock.Advance(TimeSpan.FromMinutes(1));

        await RejoinAsync(site);
        var rejoined = await FireAsync(site, rule);

        site.Bot.Bodies.Should().BeEmpty("joining again went through removal, which deleted the chat link");
        rejoined.LastError.Should().StartWith("No recipient");

        var raised = await RaisedNoticesAsync(site, rule);
        raised.Keys.Should().Equal([(await ChannelAsync(site, rule)).Id.ToString()], "the owner is told the direct message reaches no one");

        await LinkDiscordAsync(site, account);
        await FireAsync(site, rule);
        site.Bot.Bodies.Should().ContainSingle("the channel sends again once the member links the same account");
    }
}
