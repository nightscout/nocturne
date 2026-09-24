using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

[Trait("Category", "Unit")]
public class AlertRulesControllerConditionValidationTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static (AlertRulesController Controller, NocturneDbContext Db) CreateController(
        IAlertRuleConditionValidator validator, ILogger<AlertRulesController>? logger = null)
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
            logger ?? Mock.Of<ILogger<AlertRulesController>>());
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
            .Setup(v => v.ValidateUpdate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<StoredConditionTrees>()))
            .Returns((AlertConditionType _, string body, bool _, string? autoResolve, string? client, StoredConditionTrees _) =>
                new ConditionUpdateCheck(
                    [new RustValidationIssue("condition", "threshold", "direction_missing", "direction")],
                    body, autoResolve, client, []));
        var (controller, db) = CreateController(validator.Object);
        var id = Guid.NewGuid();
        db.AlertRules.Add(new AlertRuleEntity
        {
            Id = id,
            TenantId = Tenant,
            Name = "Stored",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = """{"direction":"below","value":70}""",
        });
        await db.SaveChangesAsync();

        var result = await controller.UpdateRule(id, new UpdateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = Json("""{"value": 70}"""),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.AlertRules.AsNoTracking().SingleAsync()).Name.Should().Be("Stored");
    }

    [Fact]
    public async Task UpdateRule_of_a_missing_rule_is_not_found_whatever_its_conditions()
    {
        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.ValidateUpdate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<StoredConditionTrees>()))
            .Returns((AlertConditionType _, string body, bool _, string? autoResolve, string? client, StoredConditionTrees _) =>
                new ConditionUpdateCheck(
                    [new RustValidationIssue("condition", "threshold", "direction_missing", "direction")],
                    body, autoResolve, client, []));
        var (controller, _) = CreateController(validator.Object);

        var result = await controller.UpdateRule(Guid.NewGuid(), new UpdateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = Json("""{"value": 70}"""),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    private static AlertRuleEntity LegacyRule(Guid id) => new()
    {
        Id = id,
        TenantId = Tenant,
        Name = "Low",
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = """{"direction":"below","value":70,"legacyLabel":"Low"}""",
    };

    [NativeFact]
    public async Task UpdateRule_saves_a_legacy_rule_without_the_properties_no_condition_reads()
    {
        var logger = new ListLogger<AlertRulesController>();
        var (controller, db) = CreateController(
            new AlertRuleConditionValidator(NullLogger<AlertRuleConditionValidator>.Instance), logger);
        var id = Guid.NewGuid();
        db.AlertRules.Add(LegacyRule(id));
        await db.SaveChangesAsync();

        var result = await controller.UpdateRule(id, new UpdateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = Json("""{"direction": "below", "value": 65, "legacyLabel": "Low"}"""),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        JsonNode.Parse((await db.AlertRules.AsNoTracking().SingleAsync()).ConditionParams)!.AsObject()
            .Select(p => p.Key).Should().Equal("direction", "value");
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("legacyLabel").And.Contain(id.ToString())
            .And.NotContain("Low");
    }

    [NativeFact]
    public async Task UpdateRule_rejects_an_unknown_property_the_stored_rule_did_not_have()
    {
        var (controller, db) = CreateController(
            new AlertRuleConditionValidator(NullLogger<AlertRuleConditionValidator>.Instance));
        var id = Guid.NewGuid();
        db.AlertRules.Add(LegacyRule(id));
        await db.SaveChangesAsync();

        var result = await controller.UpdateRule(id, new UpdateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = Json("""{"direction": "below", "value": 65, "legacyLabel": "Low", "forMinutes": 10}"""),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().BeOfType<ValidationProblemDetails>().Which.Errors
            .Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                ["condition:threshold"] = ["unknown_field"],
            });
        (await db.AlertRules.AsNoTracking().SingleAsync()).ConditionParams.Should().Contain("legacyLabel");
    }

    [Fact]
    public async Task CreateRule_stores_a_windows_rule_zone_as_iana()
    {
        var (controller, db) = CreateController(new NoConditionIssues());

        await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Overnight",
            ConditionType = AlertConditionType.TimeOfDay,
            ConditionParams = Json("""{"from": "22:00", "to": "06:00", "timezone": "AUS Eastern Standard Time"}"""),
        }, CancellationToken.None);

        var stored = await db.AlertRules.SingleAsync();
        JsonDocument.Parse(stored.ConditionParams).RootElement.GetProperty("timezone").GetString()
            .Should().Be("Australia/Sydney");
    }

    [Fact]
    public void Validator_reports_a_zone_nothing_resolves()
    {
        var validator = new AlertRuleConditionValidator(NullLogger<AlertRuleConditionValidator>.Instance);

        var issues = validator.Validate(
            AlertConditionType.TimeOfDay,
            """{"from": "22:00", "to": "06:00", "timezone": "Not/AZone"}""",
            autoResolveEnabled: true,
            """{"type": "time_of_day", "time_of_day": {"from": "06:00", "to": "07:00", "timezone": "Also/NotAZone"}}""",
            clientConfigurationJson: null);

        issues.Should().Contain(new RustValidationIssue("condition", "time_of_day", "invalid_field", "timezone"))
            .And.Contain(new RustValidationIssue("auto_resolve", "auto_resolve", "invalid_field", "timezone"));
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

    [NativeTheory]
    [InlineData("StateSpanActive", """{"category": "PumpMode", "is_active": true}""", "state_span_active")]
    [InlineData("Sustained",
        """{"minutes": 10, "child": {"type": "not", "not": {"child": {"type": "state_span_active", "state_span_active": {"category": "PumpMode", "is_active": false}}}}}""",
        "sustained[0].not[0].state_span_active")]
    public async Task CreateRule_rejects_a_generic_state_span_on_the_pump_mode_category(
        string type, string conditionParams, string path)
    {
        var (controller, db) = CreateController(
            new AlertRuleConditionValidator(NullLogger<AlertRuleConditionValidator>.Instance));

        var result = await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Suspended",
            ConditionType = Enum.Parse<AlertConditionType>(type),
            ConditionParams = Json(conditionParams),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().BeOfType<ValidationProblemDetails>().Which.Errors
            .Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                [$"condition:{path}"] = ["pump_mode_category:category"],
            });
        (await db.AlertRules.CountAsync()).Should().Be(0);
    }
}
