using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.Services.Alerts.Webhooks;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
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

    [Fact]
    public async Task CreateRule_stores_a_padded_secret_without_its_surrounding_whitespace()
    {
        var (controller, db, encryption) = CreateController();

        Created(await controller.CreateRule(Rule(Channel($"  {PlaintextSecret}  ")), CancellationToken.None));

        var stored = await db.AlertRuleChannels.AsNoTracking().SingleAsync(CancellationToken.None);
        encryption.Decrypt(stored.Secret!).Should().Be(PlaintextSecret);
    }

    [Fact]
    public async Task UpdateRule_treats_a_whitespace_only_secret_as_a_clear()
    {
        var (controller, db, _) = CreateController();
        var ruleId = Created(await controller.CreateRule(Rule(Channel(PlaintextSecret)), CancellationToken.None)).Id;

        var result = await controller.UpdateRule(ruleId, Update(Channel("   ")), CancellationToken.None);

        Ok(result).Channels.Should().ContainSingle().Which.HasSecret.Should().BeFalse();
        (await db.AlertRuleChannels.AsNoTracking().SingleAsync(CancellationToken.None))
            .Secret.Should().BeNull();
    }

    [Fact]
    public async Task CreateRule_rejects_a_secret_over_the_byte_bound_rather_than_overflowing_the_column()
    {
        var (controller, _, _) = CreateController();

        // 100 characters, but 300 bytes once UTF-8 encoded: a character cap alone would admit it.
        var result = await controller.CreateRule(
            Rule(Channel(new string('あ', 100))), CancellationToken.None);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("\"message\"").And.Contain("bytes");
    }

    [Fact]
    public async Task CreateRule_accepts_a_non_latin_secret_inside_the_byte_bound()
    {
        var (controller, db, encryption) = CreateController();
        var secret = new string('あ', 85);

        Created(await controller.CreateRule(Rule(Channel(secret)), CancellationToken.None));

        var stored = await db.AlertRuleChannels.AsNoTracking().SingleAsync(CancellationToken.None);
        encryption.Decrypt(stored.Secret!).Should().Be(secret);
    }

    [Fact]
    public async Task UpdateRule_retains_no_secret_when_two_channels_share_a_destination()
    {
        var (controller, db, _) = CreateController();
        var ruleId = Created(await controller.CreateRule(
            Rule(Channel(PlaintextSecret), Channel("not-a-real-secret-either")), CancellationToken.None)).Id;

        var result = await controller.UpdateRule(
            ruleId, Update(Channel(null), Channel(null)), CancellationToken.None);

        Ok(result).Channels.Should().HaveCount(2).And.OnlyContain(c => !c.HasSecret);
        (await db.AlertRuleChannels.AsNoTracking().ToListAsync(CancellationToken.None))
            .Should().OnlyContain(c => c.Secret == null);
    }

    private static CreateAlertRuleChannelRequest Channel(string? secret) => new()
    {
        ChannelType = ChannelType.Webhook,
        Destination = Url,
        Secret = secret,
    };

    private static CreateAlertRuleRequest Rule(params CreateAlertRuleChannelRequest[] channels) => new()
    {
        Name = "Low",
        ConditionType = AlertConditionType.Threshold,
        Channels = [.. channels],
    };

    private static UpdateAlertRuleRequest Update(params CreateAlertRuleChannelRequest[] channels) => new()
    {
        Name = "Low",
        ConditionType = AlertConditionType.Threshold,
        Channels = [.. channels],
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

        var encryption = WebhookTestHarness.Encryption();

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
