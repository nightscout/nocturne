using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// A rule's review reason and the problems a save would find in its stored conditions reach the
/// rule list, and the reason lasts until the rule is edited.
/// </summary>
[Trait("Category", "Unit")]
public class AlertRulesControllerReviewReasonTests
{
    private static readonly Guid Tenant = Guid.Parse("85385385-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid RuleId = Guid.Parse("85385385-bbbb-7bbb-8bbb-bbbbbbbbbbbb");

    private const string Body = """{"direction":"below","value":54}""";

    private static readonly RustValidationIssue Issue = new("condition", "threshold", "field_missing", "value");

    private static (AlertRulesController Controller, NocturneDbContext Db) Create()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new NocturneDbContext(options) { TenantId = Tenant };
        db.AlertRules.Add(new AlertRuleEntity
        {
            Id = RuleId,
            TenantId = Tenant,
            Name = "Urgent Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = Body,
            IsEnabled = true,
            ReviewReason = AlertRuleReviewReasons.LegacyConditionConverted,
        });
        db.SaveChanges();

        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.Validate(
                It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((AlertConditionType _, string body, bool _, string? _, string? _) =>
                body == Body ? new List<RustValidationIssue> { Issue } : new List<RustValidationIssue>());

        var referenceService = new Mock<IAlertReferenceService>();
        referenceService
            .Setup(r => r.FindReferencingRulesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());

        var controller = new AlertRulesController(
            new TestNocturneDbContextFactory(db),
            new AlertRepository(new TestNocturneDbContextFactory(db)),
            MockTenantAccessor.Create(db.TenantId).Object,
            referenceService.Object,
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            validator.Object,
            Mock.Of<ISecretEncryptionService>(),
            new AlertRuleRearm(new AlertRuleEvaluationGate()),
            new AlertRuleRetirement(Mock.Of<IExcursionTracker>(), Mock.Of<IExcursionResolutionHandler>()),
            Mock.Of<ILogger<AlertRulesController>>());

        return (controller, db);
    }

    [Fact]
    public async Task The_rule_list_carries_the_review_reason_and_the_stored_conditions_problems()
    {
        var (controller, _) = Create();

        var result = await controller.GetRules(CancellationToken.None);

        var rule = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<List<AlertRuleResponse>>()
            .Which.Should().ContainSingle().Subject;
        rule.ReviewReason.Should().Be(AlertRuleReviewReasons.LegacyConditionConverted);
        rule.ConditionIssues.Should().Equal(Issue);
    }

    [Fact]
    public async Task An_edit_clears_the_review_reason()
    {
        var (controller, db) = Create();

        await controller.UpdateRule(RuleId, new UpdateAlertRuleRequest
        {
            Name = "Urgent Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = JsonSerializer.Deserialize<JsonElement>("""{"direction":"below","value":55}"""),
            IsEnabled = true,
        }, CancellationToken.None);

        (await db.AlertRules.AsNoTracking().SingleAsync(r => r.Id == RuleId)).ReviewReason.Should().BeNull();
    }

    [Fact]
    public async Task A_toggle_keeps_the_review_reason()
    {
        var (controller, db) = Create();

        await controller.ToggleRule(RuleId, CancellationToken.None);

        (await db.AlertRules.AsNoTracking().SingleAsync(r => r.Id == RuleId)).ReviewReason
            .Should().Be(AlertRuleReviewReasons.LegacyConditionConverted);
    }
}
