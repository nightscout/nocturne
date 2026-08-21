using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Covers the device_action channel handling on <see cref="AlertRulesController"/>: a typo'd target
/// kind is rejected at save (so it can't silently never actuate), and the capabilities metadata
/// round-trips through create.
/// </summary>
[Trait("Category", "Unit")]
public class AlertRulesControllerDeviceActionTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static AlertRulesController CreateController()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var ctx = new NocturneDbContext(options) { TenantId = Tenant };
        var factory = new TestTenantDbContextFactory(ctx);

        return new AlertRulesController(
            factory,
            Mock.Of<IAlertReferenceService>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            Mock.Of<ISecretEncryptionService>(),
            Mock.Of<ILogger<AlertRulesController>>());
    }

    [Fact]
    public async Task CreateRule_rejects_device_action_channel_with_invalid_kind()
    {
        var controller = CreateController();
        var request = new CreateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            Channels =
            [
                new CreateAlertRuleChannelRequest
                {
                    ChannelType = ChannelType.DeviceAction,
                    Destination = "smartfridge",
                },
            ],
        };

        var result = await controller.CreateRule(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateRule_rejects_unknown_device_capability()
    {
        var controller = CreateController();
        var request = new CreateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            Channels =
            [
                new CreateAlertRuleChannelRequest
                {
                    ChannelType = ChannelType.DeviceAction,
                    Destination = DeviceKinds.Companion,
                    Metadata = new Dictionary<string, object> { ["capabilities"] = new[] { "laser" } },
                },
            ],
        };

        var result = await controller.CreateRule(request, CancellationToken.None);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        // The body must carry a `message` key (the generated web client's error extractor)
        // naming the offending capability.
        JsonSerializer.Serialize(bad.Value).Should().Contain("\"message\"").And.Contain("laser");
    }

    [Fact]
    public async Task CreateRule_rejects_capability_not_allowed_for_target_kind()
    {
        var controller = CreateController();
        var request = new CreateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            Channels =
            [
                new CreateAlertRuleChannelRequest
                {
                    ChannelType = ChannelType.DeviceAction,
                    Destination = DeviceKinds.Companion,
                    // Torch is Prelude-only.
                    Metadata = new Dictionary<string, object>
                    {
                        ["capabilities"] = new[] { DeviceCapabilities.Torch },
                    },
                },
            ],
        };

        var result = await controller.CreateRule(request, CancellationToken.None);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("\"message\"")
            .And.Contain(DeviceCapabilities.Torch);
    }

    [Fact]
    public async Task CreateRule_invalid_kind_rejection_uses_message_body()
    {
        var controller = CreateController();
        var request = new CreateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            Channels =
            [
                new CreateAlertRuleChannelRequest
                {
                    ChannelType = ChannelType.DeviceAction,
                    Destination = "smartfridge",
                },
            ],
        };

        var result = await controller.CreateRule(request, CancellationToken.None);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("\"message\"");
    }

    [Fact]
    public async Task CreateRule_persists_device_action_channel_metadata()
    {
        var controller = CreateController();
        var request = new CreateAlertRuleRequest
        {
            Name = "Urgent Low",
            ConditionType = AlertConditionType.Threshold,
            Severity = AlertRuleSeverity.Critical,
            Channels =
            [
                new CreateAlertRuleChannelRequest
                {
                    ChannelType = ChannelType.DeviceAction,
                    Destination = DeviceKinds.Companion,
                    Metadata = new Dictionary<string, object> { ["capabilities"] = new[] { "notify" } },
                },
            ],
        };

        var result = await controller.CreateRule(request, CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>().Subject;
        created.Channels.Should().ContainSingle();
        var channel = created.Channels[0];
        channel.ChannelType.Should().Be(ChannelType.DeviceAction);
        channel.Destination.Should().Be(DeviceKinds.Companion);
        JsonSerializer.Serialize(channel.Metadata).Should().Contain("notify");
    }
}
