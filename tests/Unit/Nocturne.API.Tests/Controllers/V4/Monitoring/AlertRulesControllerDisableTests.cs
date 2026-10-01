using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
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
/// Retiring a rule -- turning it off or deleting it -- closes its open excursion with reason
/// <c>rule-disabled</c> and resolves its instance through <see cref="IExcursionResolutionHandler"/>;
/// a save that leaves it enabled or disabled does nothing new, and a refused delete closes nothing
/// (docs/alerts/engine-semantics.md §6.2).
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
        Mock<IExcursionResolutionHandler> Handler,
        Func<bool> RuleExistedAtClose)> CreateAsync(
        bool enabled, string? managedBy = null, IReadOnlyList<Guid>? referencing = null)
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
            ManagedBy = managedBy,
        });
        await db.SaveChangesAsync();

        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.ValidateUpdate(
                It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<StoredConditionTrees>()))
            .Returns((AlertConditionType _, string body, bool _, string? autoResolve, string? client, StoredConditionTrees _) =>
                new ConditionUpdateCheck([], body, autoResolve, client, []));

        var referenceService = new Mock<IAlertReferenceService>();
        referenceService
            .Setup(r => r.FindReferencingRulesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(referencing ?? (IReadOnlyList<Guid>)Array.Empty<Guid>());

        var excursionId = Guid.NewGuid();
        var ruleExistedAtClose = false;
        var tracker = new Mock<IExcursionTracker>();
        tracker
            .Setup(t => t.ForceCloseAsync(RuleId, ExcursionCloseReason.RuleDisabled, It.IsAny<CancellationToken>()))
            .Callback(() => ruleExistedAtClose = db.AlertRules.AsNoTracking().Any(r => r.Id == RuleId))
            .ReturnsAsync(new ExcursionTransition(
                ExcursionTransitionType.ExcursionClosed, excursionId, ExcursionCloseReason.RuleDisabled));
        var handler = new Mock<IExcursionResolutionHandler>();

        var controller = new AlertRulesController(
            new TestTenantDbContextFactory(db),
            referenceService.Object,
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            validator.Object,
            Mock.Of<ISecretEncryptionService>(),
            new AlertRuleRearm(new AlertRuleEvaluationGate(), new AlertTrackerRepository(db)),
            new AlertRuleRetirement(tracker.Object, handler.Object),
            Mock.Of<ILogger<AlertRulesController>>());

        return (controller, excursionId, tracker, handler, () => ruleExistedAtClose);
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
        var (controller, excursionId, tracker, handler, _) = await CreateAsync(enabled: true);

        await controller.ToggleRule(RuleId, CancellationToken.None);

        VerifyClosed(excursionId, tracker, handler);
    }

    [Fact]
    public async Task Toggling_a_disabled_rule_on_closes_nothing()
    {
        var (controller, _, tracker, handler, _) = await CreateAsync(enabled: false);

        await controller.ToggleRule(RuleId, CancellationToken.None);

        VerifyNothingClosed(tracker, handler);
    }

    [Fact]
    public async Task Updating_an_enabled_rule_to_disabled_closes()
    {
        var (controller, excursionId, tracker, handler, _) = await CreateAsync(enabled: true);

        await controller.UpdateRule(RuleId, Request(enabled: false), CancellationToken.None);

        VerifyClosed(excursionId, tracker, handler);
    }

    [Fact]
    public async Task Updating_a_disabled_rule_that_stays_disabled_closes_nothing()
    {
        var (controller, _, tracker, handler, _) = await CreateAsync(enabled: false);

        await controller.UpdateRule(RuleId, Request(enabled: false), CancellationToken.None);

        VerifyNothingClosed(tracker, handler);
    }

    [Fact]
    public async Task Updating_a_disabled_rule_to_enabled_closes_nothing()
    {
        var (controller, _, tracker, handler, _) = await CreateAsync(enabled: false);

        await controller.UpdateRule(RuleId, Request(enabled: true), CancellationToken.None);

        VerifyNothingClosed(tracker, handler);
    }

    [Fact]
    public async Task Updating_an_enabled_rule_that_stays_enabled_closes_nothing()
    {
        var (controller, _, tracker, handler, _) = await CreateAsync(enabled: true);

        await controller.UpdateRule(RuleId, Request(enabled: true), CancellationToken.None);

        VerifyNothingClosed(tracker, handler);
    }

    [Fact]
    public async Task Deleting_an_enabled_rule_closes_before_removing_it()
    {
        var (controller, excursionId, tracker, handler, ruleExistedAtClose) = await CreateAsync(enabled: true);

        var result = await controller.DeleteRule(RuleId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        VerifyClosed(excursionId, tracker, handler);
        ruleExistedAtClose().Should().BeTrue("the close must run while the rule still exists");
    }

    [Fact]
    public async Task Deleting_a_disabled_rule_closes_nothing()
    {
        var (controller, _, tracker, handler, _) = await CreateAsync(enabled: false);

        var result = await controller.DeleteRule(RuleId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        VerifyNothingClosed(tracker, handler);
    }

    [Fact]
    public async Task Deleting_a_managed_rule_is_refused_and_closes_nothing()
    {
        var (controller, _, tracker, handler, _) = await CreateAsync(enabled: true, managedBy: "tracker:abc");

        var result = await controller.DeleteRule(RuleId, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        VerifyNothingClosed(tracker, handler);
    }

    [Fact]
    public async Task Deleting_a_referenced_rule_is_refused_and_closes_nothing()
    {
        var (controller, _, tracker, handler, _) =
            await CreateAsync(enabled: true, referencing: [Guid.NewGuid()]);

        var result = await controller.DeleteRule(RuleId, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        VerifyNothingClosed(tracker, handler);
    }
}
