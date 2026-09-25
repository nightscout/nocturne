using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Turning a rule off closes its open excursion with reason <c>rule-disabled</c> and resolves its
/// instance through <see cref="IExcursionResolutionHandler"/>; a save that leaves it enabled or
/// disabled does nothing new (docs/alerts/engine-semantics.md §6.2).
/// </summary>
[Trait("Category", "Unit")]
public class AlertRulesControllerDisableTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid RuleId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private const string Body = """{"direction":"below","value":70}""";

    private static async Task<(
        AlertRulesController Controller,
        Guid ExcursionId,
        Mock<IExcursionTracker> Tracker,
        Mock<IExcursionResolutionHandler> Handler)> CreateAsync(bool enabled)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new NocturneDbContext(options) { TenantId = Tenant };
        db.AlertRules.Add(new AlertRuleEntity
        {
            Id = RuleId,
            TenantId = Tenant,
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = Body,
            IsEnabled = enabled,
        });
        await db.SaveChangesAsync();

        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.ValidateUpdate(
                It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<StoredConditionTrees>()))
            .Returns((AlertConditionType _, string body, bool _, string? autoResolve, string? client, StoredConditionTrees _) =>
                new ConditionUpdateCheck([], body, autoResolve, client, []));

        var excursionId = Guid.NewGuid();
        var tracker = new Mock<IExcursionTracker>();
        tracker
            .Setup(t => t.ForceCloseAsync(RuleId, ExcursionCloseReason.RuleDisabled, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExcursionTransition(
                ExcursionTransitionType.ExcursionClosed, excursionId, ExcursionCloseReason.RuleDisabled));
        var handler = new Mock<IExcursionResolutionHandler>();

        var controller = new AlertRulesController(
            new TestTenantDbContextFactory(db),
            Mock.Of<IAlertReferenceService>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            validator.Object,
            Mock.Of<ISecretEncryptionService>(),
            new AlertRuleRearm(new AlertRuleEvaluationGate(), new AlertTrackerRepository(db)),
            new AlertRuleDisableHandler(tracker.Object, handler.Object),
            Mock.Of<ILogger<AlertRulesController>>());

        return (controller, excursionId, tracker, handler);
    }

    private static UpdateAlertRuleRequest Request(bool enabled) => new()
    {
        Name = "Low",
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = JsonSerializer.Deserialize<JsonElement>(Body),
        IsEnabled = enabled,
    };

    private static void VerifyClosed(
        Guid excursionId, Mock<IExcursionTracker> tracker, Mock<IExcursionResolutionHandler> handler)
    {
        tracker.Verify(
            t => t.ForceCloseAsync(RuleId, ExcursionCloseReason.RuleDisabled, It.IsAny<CancellationToken>()),
            Times.Once);
        handler.Verify(
            h => h.HandleClosedAsync(
                It.Is<ExcursionTransition>(t => t.ExcursionId == excursionId),
                Tenant, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static void VerifyNothingClosed(
        Mock<IExcursionTracker> tracker, Mock<IExcursionResolutionHandler> handler)
    {
        tracker.Verify(
            t => t.ForceCloseAsync(
                It.IsAny<Guid>(), It.IsAny<ExcursionCloseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
        handler.Verify(
            h => h.HandleClosedAsync(
                It.IsAny<ExcursionTransition>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Toggling_an_enabled_rule_off_closes_with_rule_disabled_and_resolves()
    {
        var (controller, excursionId, tracker, handler) = await CreateAsync(enabled: true);

        await controller.ToggleRule(RuleId, CancellationToken.None);

        VerifyClosed(excursionId, tracker, handler);
    }

    [Fact]
    public async Task Toggling_a_disabled_rule_on_closes_nothing()
    {
        var (controller, _, tracker, handler) = await CreateAsync(enabled: false);

        await controller.ToggleRule(RuleId, CancellationToken.None);

        VerifyNothingClosed(tracker, handler);
    }

    [Fact]
    public async Task Updating_an_enabled_rule_to_disabled_closes()
    {
        var (controller, excursionId, tracker, handler) = await CreateAsync(enabled: true);

        await controller.UpdateRule(RuleId, Request(enabled: false), CancellationToken.None);

        VerifyClosed(excursionId, tracker, handler);
    }

    [Fact]
    public async Task Updating_a_disabled_rule_that_stays_disabled_closes_nothing()
    {
        var (controller, _, tracker, handler) = await CreateAsync(enabled: false);

        await controller.UpdateRule(RuleId, Request(enabled: false), CancellationToken.None);

        VerifyNothingClosed(tracker, handler);
    }

    [Fact]
    public async Task Updating_a_disabled_rule_to_enabled_closes_nothing()
    {
        var (controller, _, tracker, handler) = await CreateAsync(enabled: false);

        await controller.UpdateRule(RuleId, Request(enabled: true), CancellationToken.None);

        VerifyNothingClosed(tracker, handler);
    }

    [Fact]
    public async Task Updating_an_enabled_rule_that_stays_enabled_closes_nothing()
    {
        var (controller, _, tracker, handler) = await CreateAsync(enabled: true);

        await controller.UpdateRule(RuleId, Request(enabled: true), CancellationToken.None);

        VerifyNothingClosed(tracker, handler);
    }
}
