using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

[Trait("Category", "Unit")]
public class AlertRulesControllerConditionValidationTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static (AlertRulesController Controller, NocturneDbContext Db) CreateController(
        IAlertRuleConditionValidator validator)
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
            validator,
            Mock.Of<ISecretEncryptionService>(),
            Mock.Of<ILogger<AlertRulesController>>());
        return (controller, ctx);
    }

    private static object Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public async Task CreateRule_returns_the_validators_issues_as_a_validation_problem_and_saves_nothing()
    {
        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.Validate(AlertConditionType.Composite, It.IsAny<string>(), true, It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(
            [
                new RustValidationIssue("condition", "composite[1].composite", "conditions_empty", "conditions"),
                new RustValidationIssue("condition", "composite[1].composite", "unknown_operator", "operator"),
                new RustValidationIssue("auto_resolve", "auto_resolve", "not_an_object", null),
            ]);
        var (controller, db) = CreateController(validator.Object);

        var result = await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Composite,
            ConditionParams = Json("""{"operator": "and", "conditions": []}"""),
            AutoResolveEnabled = true,
            AutoResolveParams = Json("[]"),
        }, CancellationToken.None);

        var problem = result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().BeOfType<ValidationProblemDetails>().Which;
        problem.Errors.Should().BeEquivalentTo(new Dictionary<string, string[]>
        {
            ["condition:composite[1].composite"] = ["conditions_empty:conditions", "unknown_operator:operator"],
            ["auto_resolve:auto_resolve"] = ["not_an_object"],
        });
        problem.Extensions["issues"].Should().BeAssignableTo<IReadOnlyList<RustValidationIssue>>()
            .Which.Should().HaveCount(3);
        (await db.AlertRules.CountAsync()).Should().Be(0);
        validator.Verify(v => v.Validate(
            AlertConditionType.Composite,
            """{"operator":"and","conditions":[]}""",
            true,
            "[]",
            null));
    }

    [Fact]
    public async Task UpdateRule_rejects_before_touching_the_stored_rule()
    {
        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([new RustValidationIssue("condition", "threshold", "direction_missing", "direction")]);
        var (controller, _) = CreateController(validator.Object);

        var result = await controller.UpdateRule(Guid.NewGuid(), new UpdateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = Json("""{"value": 70}"""),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [NativeFact]
    public void Validator_checks_the_body_the_enabled_auto_resolve_tree_and_smart_snooze_conditions()
    {
        var validator = new AlertRuleConditionValidator(NullLogger<AlertRuleConditionValidator>.Instance);

        var issues = validator.Validate(
            AlertConditionType.Composite,
            """{"operator": "and", "conditions": [{"type": "sustained", "sustained": {"minutes": 0, "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}}]}""",
            autoResolveEnabled: true,
            """{"type": "alert_state", "alert_state": {"alert_id": ""}}""",
            """{"snooze": {"smartSnooze": true, "conditions": [{"type": "composite", "composite": {"operator": "and", "conditions": []}}]}}""");

        issues.Should().Equal(
            new RustValidationIssue("condition", "composite[0].sustained", "minutes_not_positive", "minutes"),
            new RustValidationIssue("auto_resolve", "auto_resolve", "invalid_field", "alert_id"),
            new RustValidationIssue("snooze", "snooze[0].composite", "conditions_empty", "conditions"));
    }

    [NativeFact]
    public void Validator_skips_trees_that_are_never_evaluated()
    {
        var validator = new AlertRuleConditionValidator(NullLogger<AlertRuleConditionValidator>.Instance);

        var issues = validator.Validate(
            AlertConditionType.Threshold,
            """{"direction": "below", "value": 70}""",
            autoResolveEnabled: false,
            """{"type": "not", "not": {}}""",
            """{"snooze": {"smartSnooze": false, "conditions": [{"type": "warp_drive"}]}}""");

        issues.Should().BeEmpty();
    }
}
