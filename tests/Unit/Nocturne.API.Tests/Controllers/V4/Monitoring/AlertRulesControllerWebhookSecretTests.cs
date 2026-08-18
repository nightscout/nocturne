using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Shared.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Covers the webhook channel's signing secret on <see cref="AlertRulesController"/>: it is stored
/// as ciphertext, never echoed back, and survives an edit that cannot re-send what it was never
/// shown.
/// </summary>
[Trait("Category", "Unit")]
public class AlertRulesControllerWebhookSecretTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string Url = "https://receiver.example.com/hook";
    private const string PlaintextSecret = "not-a-real-secret";

    [Fact]
    public async Task CreateRule_stores_the_secret_encrypted_and_reports_only_that_one_exists()
    {
        var (controller, db, encryption) = CreateController();

        var response = Created(await controller.CreateRule(Rule(Channel(PlaintextSecret)), CancellationToken.None));

        response.Channels.Should().ContainSingle().Which.HasSecret.Should().BeTrue();
        JsonSerializer.Serialize(response).Should().NotContain(PlaintextSecret);

        var stored = await db.AlertRuleChannels.AsNoTracking().SingleAsync(CancellationToken.None);
        stored.Secret.Should().NotBeNullOrEmpty().And.NotBe(PlaintextSecret);
        encryption.Decrypt(stored.Secret!).Should().Be(PlaintextSecret);
    }

    [Fact]
    public async Task CreateRule_without_a_secret_leaves_the_channel_unsigned()
    {
        var (controller, db, _) = CreateController();

        var response = Created(await controller.CreateRule(Rule(Channel(null)), CancellationToken.None));

        response.Channels.Should().ContainSingle().Which.HasSecret.Should().BeFalse();
        (await db.AlertRuleChannels.AsNoTracking().SingleAsync(CancellationToken.None))
            .Secret.Should().BeNull();
    }

    [Fact]
    public async Task UpdateRule_keeps_the_stored_secret_when_the_editor_re_sends_none()
    {
        var (controller, db, encryption) = CreateController();
        var ruleId = Created(await controller.CreateRule(Rule(Channel(PlaintextSecret)), CancellationToken.None)).Id;

        var result = await controller.UpdateRule(ruleId, Update(Channel(null)), CancellationToken.None);

        Ok(result).Channels.Should().ContainSingle().Which.HasSecret.Should().BeTrue();
        var stored = await db.AlertRuleChannels.AsNoTracking().SingleAsync(CancellationToken.None);
        encryption.Decrypt(stored.Secret!).Should().Be(PlaintextSecret);
    }

    [Fact]
    public async Task UpdateRule_clears_the_stored_secret_when_the_editor_sends_an_empty_one()
    {
        var (controller, db, _) = CreateController();
        var ruleId = Created(await controller.CreateRule(Rule(Channel(PlaintextSecret)), CancellationToken.None)).Id;

        var result = await controller.UpdateRule(ruleId, Update(Channel("")), CancellationToken.None);

        Ok(result).Channels.Should().ContainSingle().Which.HasSecret.Should().BeFalse();
        (await db.AlertRuleChannels.AsNoTracking().SingleAsync(CancellationToken.None))
            .Secret.Should().BeNull();
    }

    private static CreateAlertRuleChannelRequest Channel(string? secret) => new()
    {
        ChannelType = ChannelType.Webhook,
        Destination = Url,
        Secret = secret,
    };

    private static CreateAlertRuleRequest Rule(CreateAlertRuleChannelRequest channel) => new()
    {
        Name = "Low",
        ConditionType = AlertConditionType.Threshold,
        Channels = [channel],
    };

    private static UpdateAlertRuleRequest Update(CreateAlertRuleChannelRequest channel) => new()
    {
        Name = "Low",
        ConditionType = AlertConditionType.Threshold,
        Channels = [channel],
    };

    private static AlertRuleResponse Created(ActionResult<AlertRuleResponse> result) =>
        result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>().Subject;

    private static AlertRuleResponse Ok(ActionResult<AlertRuleResponse> result) =>
        result.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<AlertRuleResponse>().Subject;

    private static (AlertRulesController Controller, NocturneDbContext Db, ISecretEncryptionService Encryption)
        CreateController()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var ctx = new NocturneDbContext(options) { TenantId = Tenant };

        var encryption = new SecretEncryptionService(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ServiceNames.ConfigKeys.InstanceKey] = "test-instance-key",
                })
                .Build(),
            NullLogger<SecretEncryptionService>.Instance);

        var controller = new AlertRulesController(
            new TestTenantDbContextFactory(ctx),
            Mock.Of<IAlertReferenceService>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            encryption,
            Mock.Of<ILogger<AlertRulesController>>());

        return (controller, ctx, encryption);
    }
}
