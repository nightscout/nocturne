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

    [Fact]
    public async Task CreateRule_resolves_discord_dm_destination_from_the_linked_identity()
    {
        var (controller, db) = CreateController();
        db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
        {
            Id = Guid.CreateVersion7(),
            Platform = "discord",
            PlatformUserId = DiscordUserSnowflake,
            TenantId = Tenant,
            NocturneUserId = Subject,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await controller.CreateRule(
            RuleWith(ChannelType.DiscordDm, null),
            CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>().Subject;
        created.Channels.Should().ContainSingle()
            .Which.Destination.Should().Be(DiscordUserSnowflake);
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
