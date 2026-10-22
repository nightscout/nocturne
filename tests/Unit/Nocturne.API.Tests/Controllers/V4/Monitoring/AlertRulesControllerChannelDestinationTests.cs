using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Covers destination validation on <see cref="AlertRulesController"/>. Nothing downstream
/// inspects a channel destination — the bot service hands it straight to the platform adapter —
/// so a destination in the wrong shape saves cleanly and then never delivers.
/// </summary>
[Trait("Category", "Unit")]
public class AlertRulesControllerChannelDestinationTests
{
    private static readonly Guid Tenant = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Subject = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private const string ChannelSnowflake = "1234567890123456789";
    private const string DiscordUserSnowflake = "9876543210987654321";
    private const string SlackMemberId = "U00FAKEUSER1";
    private const string TelegramUserId = "864203571";

    private static (AlertRulesController Controller, NocturneDbContext Db) CreateController(
        IAlertDeliveryService? deliveryService = null, string grantedScope = Scope.AlertsReadWrite)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var ctx = new NocturneDbContext(options) { TenantId = Tenant };

        var controller = new AlertRulesController(
            new TestNocturneDbContextFactory(ctx),
            Mock.Of<IAlertReferenceService>(),
            deliveryService ?? Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            new NoConditionIssues(),
            Mock.Of<ISecretEncryptionService>(),
            new AlertRuleRearm(new AlertRuleEvaluationGate(), new AlertTrackerRepository(ctx.AsFactory())),
            new AlertRuleRetirement(Mock.Of<IExcursionTracker>(), Mock.Of<IExcursionResolutionHandler>()),
            Mock.Of<ILogger<AlertRulesController>>());

        var http = new DefaultHttpContext();
        http.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Subject,
            TenantId = Tenant,
        };
        http.Items[AuthContextKeys.GrantedScopes] = new HashSet<string> { grantedScope };
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        return (controller, ctx);
    }

    private static CreateAlertRuleRequest RuleWith(ChannelType type, string? destination) => new()
    {
        Name = "Low",
        ConditionType = AlertConditionType.Threshold,
        Channels = [new CreateAlertRuleChannelRequest { ChannelType = type, Destination = destination }],
    };

    [Fact]
    public async Task CreateRule_rejects_discord_channel_destination_that_is_a_webhook_url()
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateRule(
            RuleWith(ChannelType.DiscordChannel, "https://discord.com/api/webhooks/123/abc"),
            CancellationToken.None);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("\"message\"").And.Contain("discord_channel");
    }

    [Fact]
    public async Task CreateRule_accepts_discord_channel_snowflake_destination()
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateRule(
            RuleWith(ChannelType.DiscordChannel, ChannelSnowflake),
            CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>().Subject;
        created.Channels.Should().ContainSingle()
            .Which.Destination.Should().Be(ChannelSnowflake);
    }

    [Theory]
    [InlineData("1234567890123456")]      // 16 digits — one short
    [InlineData("123456789012345678901")] // 21 digits — one long
    public async Task CreateRule_rejects_discord_channel_destination_outside_snowflake_length(string destination)
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateRule(
            RuleWith(ChannelType.DiscordChannel, destination),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateRule_rejects_webhook_channel_with_empty_destination()
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateRule(
            RuleWith(ChannelType.Webhook, "   "),
            CancellationToken.None);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("\"message\"").And.Contain("webhook");
    }

    [Fact]
    public async Task CreateRule_rejects_discord_dm_when_the_caller_has_no_linked_discord_account()
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateRule(
            RuleWith(ChannelType.DiscordDm, null),
            CancellationToken.None);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("\"message\"").And.Contain("Discord");
    }

    private static async Task LinkIdentityAsync(
        NocturneDbContext db, string platform, string platformUserId)
    {
        db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
        {
            Id = Guid.CreateVersion7(),
            Platform = platform,
            PlatformUserId = platformUserId,
            TenantId = Tenant,
            NocturneUserId = Subject,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(ChannelType.DiscordDm, "discord", DiscordUserSnowflake)]
    [InlineData(ChannelType.SlackDm, "slack", SlackMemberId)]
    [InlineData(ChannelType.TelegramDm, "telegram", TelegramUserId)]
    public async Task CreateRule_resolves_a_dm_destination_from_the_linked_identity(
        ChannelType channelType, string platform, string platformUserId)
    {
        var (controller, db) = CreateController();
        await LinkIdentityAsync(db, platform, platformUserId);

        var result = await controller.CreateRule(
            RuleWith(channelType, null),
            CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>().Subject;
        created.Channels.Should().ContainSingle()
            .Which.Destination.Should().Be(platformUserId);
    }

    [Theory]
    [InlineData(ChannelType.SlackDm, "Slack")]
    [InlineData(ChannelType.TelegramDm, "Telegram")]
    public async Task CreateRule_rejects_a_dm_channel_when_the_caller_has_no_link_on_that_platform(
        ChannelType channelType, string platform)
    {
        var (controller, db) = CreateController();
        await LinkIdentityAsync(db, "discord", DiscordUserSnowflake);

        var result = await controller.CreateRule(
            RuleWith(channelType, null),
            CancellationToken.None);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain(platform);
    }

    [Theory]
    [InlineData(ChannelType.SlackChannel, "C0123456789")]
    [InlineData(ChannelType.SlackChannel, "G0123456789")]
    [InlineData(ChannelType.TelegramGroup, "-1001234567890")]
    [InlineData(ChannelType.TelegramGroup, "@nocturne_family")]
    public async Task CreateRule_accepts_a_group_destination_the_adapter_can_address(
        ChannelType channelType, string destination)
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateRule(
            RuleWith(channelType, destination),
            CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>().Subject;
        created.Channels.Should().ContainSingle()
            .Which.Destination.Should().Be(destination);
    }

    [Theory]
    [InlineData(ChannelType.SlackChannel, null)]
    [InlineData(ChannelType.SlackChannel, "  ")]
    [InlineData(ChannelType.SlackChannel, "#general")]
    [InlineData(ChannelType.SlackChannel, "U00FAKEUSER1")]
    [InlineData(ChannelType.TelegramGroup, null)]
    [InlineData(ChannelType.TelegramGroup, "  ")]
    [InlineData(ChannelType.TelegramGroup, "1001234567890")]
    [InlineData(ChannelType.TelegramGroup, "https://t.me/nocturne")]
    [InlineData(ChannelType.SlackDm, "not-a-member-id")]
    [InlineData(ChannelType.TelegramDm, "-1001234567890")]
    public async Task CreateRule_rejects_a_destination_the_adapter_cannot_address(
        ChannelType channelType, string? destination)
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateRule(
            RuleWith(channelType, destination),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("+15551234567")]
    [InlineData("+61412345678")]
    [InlineData("+441632960001")]
    [InlineData("+1234567")]         // 7 digits — the E.164 minimum
    [InlineData("+123456789012345")] // 15 digits — the E.164 maximum
    public async Task CreateRule_accepts_a_whatsapp_destination_in_e164(string destination)
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateRule(
            RuleWith(ChannelType.WhatsAppDm, destination),
            CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>().Subject;
        created.Channels.Should().ContainSingle()
            .Which.Destination.Should().Be(destination);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("15551234567")]        // a webhook wa_id — no leading +
    [InlineData("+1 (555) 123-4567")]  // separators the Cloud API tolerates, we do not store
    [InlineData("+0155512345")]        // no country calling code starts with 0
    [InlineData("+123456")]            // 6 digits — one short
    [InlineData("+1234567890123456")]  // 16 digits — one past the E.164 maximum
    [InlineData("whatsapp:+15551234567")]
    public async Task CreateRule_rejects_a_whatsapp_destination_outside_e164(string? destination)
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateRule(
            RuleWith(ChannelType.WhatsAppDm, destination),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(ChannelType.Telegram, "telegram_dm")]
    [InlineData(ChannelType.WhatsApp, "whatsapp_dm")]
    [InlineData(ChannelType.WebPush, "in_app")]
    public async Task CreateRule_rejects_a_channel_type_with_no_delivery_path(
        ChannelType channelType, string replacement)
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateRule(
            RuleWith(channelType, "anything"),
            CancellationToken.None);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain(replacement);
    }

    [Fact]
    public async Task TestFireDryRun_rejects_a_destination_the_adapter_cannot_address()
    {
        var (controller, _) = CreateController();

        var result = await controller.TestFireDryRun(
            new TestFireDryRunRequest("Low", AlertRuleSeverity.Warning,
            [
                new CreateAlertRuleChannelRequest
                {
                    ChannelType = ChannelType.SlackChannel,
                    Destination = "#general",
                },
            ]),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task TestFireDryRun_fires_a_dm_channel_at_the_linked_identity()
    {
        var (controller, db) = CreateController();
        await LinkIdentityAsync(db, "slack", SlackMemberId);

        var result = await controller.TestFireDryRun(
            new TestFireDryRunRequest("Low", AlertRuleSeverity.Warning,
            [
                new CreateAlertRuleChannelRequest { ChannelType = ChannelType.SlackDm },
            ]),
            CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task CreateRule_ignores_a_discord_link_belonging_to_another_subject()
    {
        var (controller, db) = CreateController();
        db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
        {
            Id = Guid.CreateVersion7(),
            Platform = "discord",
            PlatformUserId = DiscordUserSnowflake,
            TenantId = Tenant,
            NocturneUserId = Guid.CreateVersion7(),
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await controller.CreateRule(
            RuleWith(ChannelType.DiscordDm, null),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    private static async Task AddMemberAsync(
        NocturneDbContext db, Guid subjectId, bool isSystemSubject = false, bool denied = false)
    {
        db.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = subjectId.ToString(),
            IsSystemSubject = isSystemSubject,
        });
        var membership = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = Tenant,
            SubjectId = subjectId,
            DirectPermissions = denied ? [] : ["*"],
        };
        db.TenantMembers.Add(membership);
        if (denied)
        {
            var role = new TenantRoleEntity
            {
                Id = Guid.CreateVersion7(), TenantId = Tenant, Name = "Denied", Slug = RoleSeeds.Denied,
            };
            db.TenantRoles.Add(role);
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(), TenantMemberId = membership.Id, TenantRoleId = role.Id,
            });
        }
        await db.SaveChangesAsync();
    }

    private static AlertRuleChannelResponse SingleChannel(ActionResult<AlertRuleResponse> result) =>
        result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>()
            .Subject.Channels.Should().ContainSingle().Subject;

    [Fact]
    public async Task CreateRule_routes_an_in_app_channel_to_the_caller()
    {
        var (controller, db) = CreateController();
        await AddMemberAsync(db, Subject);

        var result = await controller.CreateRule(RuleWith(ChannelType.InApp, null), CancellationToken.None);

        var channel = SingleChannel(result);
        channel.ChannelType.Should().Be(ChannelType.InApp);
        channel.Destination.Should().Be(Subject.ToString());
    }

    [Fact]
    public async Task CreateRule_keeps_an_in_app_channel_routed_to_another_member()
    {
        var (controller, db) = CreateController();
        var caregiver = Guid.CreateVersion7();
        await AddMemberAsync(db, Subject);
        await AddMemberAsync(db, caregiver);

        var result = await controller.CreateRule(
            RuleWith(ChannelType.InApp, caregiver.ToString()), CancellationToken.None);

        SingleChannel(result).Destination.Should().Be(caregiver.ToString());
    }

    [Fact]
    public async Task CreateRule_rejects_an_in_app_channel_routed_to_someone_outside_the_tenant()
    {
        var (controller, db) = CreateController();
        await AddMemberAsync(db, Subject);
        var outsider = Guid.CreateVersion7();
        db.Subjects.Add(new SubjectEntity { Id = outsider, Name = "Outsider" });
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            SubjectId = outsider,
            DirectPermissions = ["*"],
        });
        await db.SaveChangesAsync();

        var result = await controller.CreateRule(
            RuleWith(ChannelType.InApp, outsider.ToString()), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("user-1")]
    [InlineData("default")]
    public async Task CreateRule_rejects_an_in_app_destination_that_is_not_a_subject_id(string destination)
    {
        var (controller, db) = CreateController();
        await AddMemberAsync(db, Subject);

        var result = await controller.CreateRule(RuleWith(ChannelType.InApp, destination), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateRule_rejects_an_in_app_channel_routed_to_a_system_subject()
    {
        var (controller, db) = CreateController();
        var devices = Guid.CreateVersion7();
        await AddMemberAsync(db, Subject);
        await AddMemberAsync(db, devices, isSystemSubject: true);

        var result = await controller.CreateRule(
            RuleWith(ChannelType.InApp, devices.ToString()), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateRule_rejects_an_in_app_channel_when_the_caller_is_not_a_member()
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateRule(RuleWith(ChannelType.InApp, null), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task TestFireDryRun_fires_an_in_app_channel_at_the_caller()
    {
        var delivery = new Mock<IAlertDeliveryService>();
        IReadOnlyList<AlertRuleChannelSnapshot>? fired = null;
        delivery
            .Setup(d => d.TestFireDryRunAsync(
                It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
                It.IsAny<AlertPayload>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AlertRuleChannelSnapshot>, AlertPayload, CancellationToken>(
                (channels, _, _) => fired = channels)
            .Returns(Task.CompletedTask);
        var (controller, db) = CreateController(delivery.Object);
        await AddMemberAsync(db, Subject);

        var result = await controller.TestFireDryRun(
            new TestFireDryRunRequest("Low", AlertRuleSeverity.Warning,
            [
                new CreateAlertRuleChannelRequest { ChannelType = ChannelType.InApp },
            ]),
            CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        fired.Should().ContainSingle().Which.Destination.Should().Be(Subject.ToString());
    }

    private static async Task<AlertRuleEntity> SeedInAppRuleAsync(NocturneDbContext db, Guid recipient)
    {
        var rule = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = Tenant,
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
        };
        rule.Channels.Add(new AlertRuleChannelEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = Tenant,
            AlertRuleId = rule.Id,
            ChannelType = ChannelType.InApp,
            Destination = recipient.ToString(),
        });
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return rule;
    }

    private static UpdateAlertRuleRequest RenameWithInApp(string name, string destination) => new()
    {
        Name = name,
        ConditionType = AlertConditionType.Threshold,
        Channels = [new CreateAlertRuleChannelRequest { ChannelType = ChannelType.InApp, Destination = destination }],
    };

    [Fact]
    public async Task UpdateRule_sends_a_departed_recipients_channel_to_the_member_saving_it()
    {
        var (controller, db) = CreateController();
        await AddMemberAsync(db, Subject);
        var departed = Guid.CreateVersion7();
        db.Subjects.Add(new SubjectEntity { Id = departed, Name = "Departed" });
        var rule = await SeedInAppRuleAsync(db, departed);

        var result = await controller.UpdateRule(
            rule.Id, RenameWithInApp("Renamed", departed.ToString()), CancellationToken.None);

        var updated = result.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>().Subject;
        updated.Name.Should().Be("Renamed");
        updated.Channels.Should().ContainSingle()
            .Which.Destination.Should().Be(Subject.ToString(), "a stored recipient who left must not make the rule uneditable");
    }

    [Fact]
    public async Task UpdateRule_still_rejects_a_non_member_the_rule_never_named()
    {
        var (controller, db) = CreateController();
        await AddMemberAsync(db, Subject);
        var rule = await SeedInAppRuleAsync(db, Subject);

        var result = await controller.UpdateRule(
            rule.Id, RenameWithInApp("Low", Guid.CreateVersion7().ToString()), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateRule_keeps_a_stored_recipient_who_is_still_a_member_when_another_member_saves()
    {
        var (controller, db) = CreateController();
        var caregiver = Guid.CreateVersion7();
        await AddMemberAsync(db, Subject);
        await AddMemberAsync(db, caregiver);
        var rule = await SeedInAppRuleAsync(db, caregiver);

        var result = await controller.UpdateRule(
            rule.Id, RenameWithInApp("Renamed", caregiver.ToString()), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>()
            .Subject.Channels.Should().ContainSingle()
            .Which.Destination.Should().Be(caregiver.ToString());
    }

    [Fact]
    public async Task UpdateRule_treats_a_stored_denied_recipient_like_a_departed_one()
    {
        var (controller, db) = CreateController();
        var denied = Guid.CreateVersion7();
        await AddMemberAsync(db, Subject);
        await AddMemberAsync(db, denied, denied: true);
        var rule = await SeedInAppRuleAsync(db, denied);

        var result = await controller.UpdateRule(
            rule.Id, RenameWithInApp("Renamed", denied.ToString()), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>()
            .Subject.Channels.Should().ContainSingle()
            .Which.Destination.Should().Be(Subject.ToString());
    }

    [Fact]
    public async Task CreateRule_rejects_an_in_app_channel_routed_to_a_denied_member()
    {
        var (controller, db) = CreateController();
        var denied = Guid.CreateVersion7();
        await AddMemberAsync(db, Subject);
        await AddMemberAsync(db, denied, denied: true);

        var result = await controller.CreateRule(
            RuleWith(ChannelType.InApp, denied.ToString()), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task TestFireDryRun_fires_a_departed_stored_recipients_channel_at_the_caller()
    {
        var delivery = new Mock<IAlertDeliveryService>();
        IReadOnlyList<AlertRuleChannelSnapshot>? fired = null;
        delivery
            .Setup(d => d.TestFireDryRunAsync(
                It.IsAny<IReadOnlyList<AlertRuleChannelSnapshot>>(),
                It.IsAny<AlertPayload>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AlertRuleChannelSnapshot>, AlertPayload, CancellationToken>(
                (channels, _, _) => fired = channels)
            .Returns(Task.CompletedTask);
        var (controller, db) = CreateController(delivery.Object);
        await AddMemberAsync(db, Subject);
        var departed = Guid.CreateVersion7();
        await SeedInAppRuleAsync(db, departed);

        var result = await controller.TestFireDryRun(
            new TestFireDryRunRequest("Low", AlertRuleSeverity.Warning,
            [
                new CreateAlertRuleChannelRequest { ChannelType = ChannelType.InApp, Destination = departed.ToString() },
            ]),
            CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        fired.Should().ContainSingle().Which.Destination.Should().Be(Subject.ToString());
    }

    private static InAppRecipientResponse? RecipientOf(ActionResult<AlertRuleResponse> result) =>
        result.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>()
            .Subject.Channels.Should().ContainSingle().Subject.Recipient;

    [Fact]
    public async Task GetRule_names_the_in_app_recipient_for_a_caller_who_may_edit()
    {
        var (controller, db) = CreateController();
        await AddMemberAsync(db, Subject);
        var rule = await SeedInAppRuleAsync(db, Subject);

        var recipient = RecipientOf(await controller.GetRule(rule.Id, CancellationToken.None));

        recipient.Should().BeEquivalentTo(new InAppRecipientResponse
        {
            SubjectId = Subject,
            Name = Subject.ToString(),
            CanReceive = true,
        });
    }

    [Fact]
    public async Task GetRule_describes_no_recipient_to_a_read_only_caller()
    {
        var (controller, db) = CreateController(grantedScope: Scope.AlertsRead);
        await AddMemberAsync(db, Subject);
        var rule = await SeedInAppRuleAsync(db, Subject);

        RecipientOf(await controller.GetRule(rule.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetRule_marks_a_recipient_who_cannot_receive()
    {
        var (controller, db) = CreateController();
        var departed = Guid.CreateVersion7();
        var denied = Guid.CreateVersion7();
        await AddMemberAsync(db, denied, denied: true);
        var departedRule = await SeedInAppRuleAsync(db, departed);
        var deniedRule = await SeedInAppRuleAsync(db, denied);

        RecipientOf(await controller.GetRule(departedRule.Id, CancellationToken.None))
            .Should().BeEquivalentTo(new InAppRecipientResponse { SubjectId = departed, Name = null, CanReceive = false });
        RecipientOf(await controller.GetRule(deniedRule.Id, CancellationToken.None))
            .Should().BeEquivalentTo(new InAppRecipientResponse { SubjectId = denied, Name = null, CanReceive = false });
    }

    [Fact]
    public async Task GetRule_marks_a_stored_channel_that_names_no_one()
    {
        var (controller, db) = CreateController();
        var rule = await SeedInAppRuleAsync(db, Guid.Empty);
        rule = await db.AlertRules.Include(r => r.Channels).SingleAsync(r => r.Id == rule.Id);
        rule.Channels.Single().Destination = string.Empty;
        await db.SaveChangesAsync();

        RecipientOf(await controller.GetRule(rule.Id, CancellationToken.None))
            .Should().BeEquivalentTo(new InAppRecipientResponse { SubjectId = null, Name = null, CanReceive = false });
    }

    [Fact]
    public async Task GetInAppRecipients_lists_the_people_who_may_receive()
    {
        var (controller, db) = CreateController();
        var caregiver = Guid.CreateVersion7();
        await AddMemberAsync(db, Subject);
        await AddMemberAsync(db, caregiver);
        await AddMemberAsync(db, Guid.CreateVersion7(), isSystemSubject: true);
        await AddMemberAsync(db, Guid.CreateVersion7(), denied: true);

        var result = await controller.GetInAppRecipients(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeAssignableTo<List<InAppRecipientResponse>>()
            .Subject.Select(r => r.SubjectId).Should().BeEquivalentTo(new Guid?[] { Subject, caregiver });
    }

    [Fact]
    public async Task UpdateRule_rejects_discord_channel_destination_that_is_a_webhook_url()
    {
        var (controller, db) = CreateController();
        var rule = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = Tenant,
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
        };
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync();

        var result = await controller.UpdateRule(rule.Id, new UpdateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            Channels =
            [
                new CreateAlertRuleChannelRequest
                {
                    ChannelType = ChannelType.DiscordChannel,
                    Destination = "https://discord.com/api/webhooks/123/abc",
                },
            ],
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
