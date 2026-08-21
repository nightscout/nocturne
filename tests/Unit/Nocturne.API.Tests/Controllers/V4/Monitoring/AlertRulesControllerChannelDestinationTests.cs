using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
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

    private static (AlertRulesController Controller, NocturneDbContext Db) CreateController()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var ctx = new NocturneDbContext(options) { TenantId = Tenant };

        var controller = new AlertRulesController(
            new TestTenantDbContextFactory(ctx),
            Mock.Of<IAlertReferenceService>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            Mock.Of<ISecretEncryptionService>(),
            Mock.Of<ILogger<AlertRulesController>>());

        var http = new DefaultHttpContext();
        http.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Subject,
            TenantId = Tenant,
        };
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

    [Fact]
    public async Task CreateRule_keeps_saving_channels_that_need_no_destination()
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateRule(
            RuleWith(ChannelType.InApp, null),
            CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>().Subject;
        created.Channels.Should().ContainSingle()
            .Which.ChannelType.Should().Be(ChannelType.InApp);
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
