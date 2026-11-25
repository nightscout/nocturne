using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
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
            new TestNocturneDbContextFactory(ctx),
            new TenantMembershipRepository(new TestNocturneDbContextFactory(ctx), AccountLinkRoutes.Off),
            new SubjectRepository(new TestNocturneDbContextFactory(ctx), AccountLinkRoutes.Off),
            new AlertRepository(new TestNocturneDbContextFactory(ctx)),
            new AlertRuleRepository(new TestNocturneDbContextFactory(ctx), TimeProvider.System),
            MockTenantAccessor.Create(ctx.TenantId).Object,
            Mock.Of<IAlertReferenceService>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            validator,
            Mock.Of<ISecretEncryptionService>(),
            Rearms.Unannounced(),
            new AlertRuleRetirement(Mock.Of<IExcursionTracker>(), Mock.Of<IExcursionResolutionHandler>(), Mock.Of<IUnevaluableRuleNotice>()),
            logger ?? Mock.Of<ILogger<AlertRulesController>>(), TimeProvider.System);
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
                new RustValidationIssue("condition", "composite[1].composite", "list_empty", "conditions"),
                new RustValidationIssue("condition", "composite[1].composite", "unknown_value", "operator"),
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
            ["condition:composite[1].composite"] = ["list_empty:conditions", "unknown_value:operator"],
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
    public async Task On_unknown_round_trips_create_read_update_and_snapshot()
    {
        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([]);
        var (controller, db) = CreateController(validator.Object);
        var body = Json("""{"direction": "below", "value": 70}""");

        var created = (await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = body,
            OnUnknown = AlertRuleOnUnknown.Met,
        }, CancellationToken.None)).Result.Should().BeOfType<CreatedAtActionResult>().Which.Value
            .Should().BeOfType<AlertRuleResponse>().Which;
        created.OnUnknown.Should().Be(AlertRuleOnUnknown.Met);

        var keptOnUpdate = await controller.UpdateRule(created.Id, new UpdateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = body,
        }, CancellationToken.None);
        keptOnUpdate.Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<AlertRuleResponse>().Which.OnUnknown.Should().Be(AlertRuleOnUnknown.Met, "an update without it keeps the stored word");

        var changed = await controller.UpdateRule(created.Id, new UpdateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = body,
            OnUnknown = AlertRuleOnUnknown.NotMet,
        }, CancellationToken.None);
        changed.Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<AlertRuleResponse>().Which.OnUnknown.Should().Be(AlertRuleOnUnknown.NotMet);

        (await db.AlertRules.AsNoTracking().SingleAsync()).OnUnknown.Should().Be(AlertRuleOnUnknown.NotMet);
        var snapshot = (await new AlertRepository(new TestNocturneDbContextFactory(db)).GetEnabledRulesAsync(CancellationToken.None))
            .Single();
        snapshot.OnUnknown.Should().Be(AlertRuleOnUnknown.NotMet);
    }

    [Fact]
    public async Task Glucose_freshness_round_trips_create_read_update_and_snapshot()
    {
        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([]);
        var (controller, db) = CreateController(validator.Object);
        var body = Json("""{"direction": "below", "value": 70}""");

        var unset = (await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Default",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = body,
        }, CancellationToken.None)).Result.Should().BeOfType<CreatedAtActionResult>().Which.Value
            .Should().BeOfType<AlertRuleResponse>().Which;
        unset.GlucoseFreshnessMinutes.Should().BeNull();

        var created = (await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = body,
            GlucoseFreshnessMinutes = 30,
        }, CancellationToken.None)).Result.Should().BeOfType<CreatedAtActionResult>().Which.Value
            .Should().BeOfType<AlertRuleResponse>().Which;
        created.GlucoseFreshnessMinutes.Should().Be(30);

        var changed = await controller.UpdateRule(created.Id, new UpdateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = body,
            GlucoseFreshnessMinutes = 20,
        }, CancellationToken.None);
        changed.Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<AlertRuleResponse>().Which.GlucoseFreshnessMinutes.Should().Be(20);

        (await db.AlertRules.AsNoTracking().SingleAsync(r => r.Id == created.Id))
            .GlucoseFreshnessMinutes.Should().Be(20);
        var snapshots = await new AlertRepository(new TestNocturneDbContextFactory(db))
            .GetEnabledRulesAsync(CancellationToken.None);
        snapshots.Single(r => r.Id == created.Id).GlucoseFreshnessMinutes.Should().Be(20);
        snapshots.Single(r => r.Id == unset.Id).GlucoseFreshnessMinutes.Should().BeNull();

        var kept = await controller.UpdateRule(created.Id, new UpdateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = body,
        }, CancellationToken.None);
        kept.Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<AlertRuleResponse>().Which.GlucoseFreshnessMinutes
            .Should().Be(20, "an update without it keeps the stored bound");

        var cleared = await controller.UpdateRule(created.Id, new UpdateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = body,
            ClearGlucoseFreshness = true,
        }, CancellationToken.None);
        cleared.Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<AlertRuleResponse>().Which.GlucoseFreshnessMinutes
            .Should().BeNull("clearing returns the rule to the engine's default");
        (await db.AlertRules.AsNoTracking().SingleAsync(r => r.Id == created.Id))
            .GlucoseFreshnessMinutes.Should().BeNull();
    }

    [Fact]
    public async Task CreateRule_without_on_unknown_holds()
    {
        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([]);
        var (controller, _) = CreateController(validator.Object);

        var created = (await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = Json("""{"direction": "below", "value": 70}"""),
        }, CancellationToken.None)).Result.Should().BeOfType<CreatedAtActionResult>().Which.Value
            .Should().BeOfType<AlertRuleResponse>().Which;

        created.OnUnknown.Should().Be(AlertRuleOnUnknown.Hold);
    }

    [Fact]
    public async Task UpdateRule_rejects_before_touching_the_stored_rule()
    {
        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([new RustValidationIssue("condition", "threshold", "field_missing", "direction")]);
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
            .Setup(v => v.Validate(It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([new RustValidationIssue("condition", "threshold", "field_missing", "direction")]);
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
    public async Task UpdateRule_rejects_an_unknown_property_even_one_the_stored_rule_has()
    {
        var (controller, db) = CreateController(
            new AlertRuleConditionValidator());
        var id = Guid.NewGuid();
        db.AlertRules.Add(LegacyRule(id));
        await db.SaveChangesAsync();

        var result = await controller.UpdateRule(id, new UpdateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = Json("""{"direction": "below", "value": 65, "legacyLabel": "Low"}"""),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().BeOfType<ValidationProblemDetails>().Which.Errors
            .Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                ["condition:threshold"] = ["unknown_field"],
            });
        (await db.AlertRules.AsNoTracking().SingleAsync()).ConditionParams.Should().Contain("legacyLabel");
    }

    [Theory]
    [InlineData("AUS Eastern Standard Time")]
    [InlineData("australia/sydney")]
    [InlineData("")]
    public void Validator_reports_a_zone_that_is_not_exactly_an_iana_id(string zone)
    {
        var issues = new AlertRuleConditionValidator().Validate(
            AlertConditionType.TimeOfDay,
            $$"""{"from": "22:00", "to": "06:00", "timezone": "{{zone}}"}""",
            autoResolveEnabled: false,
            autoResolveParamsJson: null,
            clientConfigurationJson: null);

        issues.Should().Equal(new RustValidationIssue("condition", "time_of_day", "invalid_field", "timezone"));
    }

    [Fact]
    public void Validator_reports_a_zone_nothing_resolves()
    {
        var validator = new AlertRuleConditionValidator();

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
        var validator = new AlertRuleConditionValidator();

        var issues = validator.Validate(
            AlertConditionType.Composite,
            """{"operator": "and", "conditions": [{"type": "sustained", "sustained": {"minutes": 0, "child": {"type": "threshold", "threshold": {"direction": "below", "value": 70}}}}]}""",
            autoResolveEnabled: true,
            """{"type": "alert_state", "alert_state": {"alert_id": ""}}""",
            """{"snooze": {"smartSnooze": true, "conditions": [{"type": "composite", "composite": {"operator": "and", "conditions": []}}]}}""");

        issues.Should().Equal(
            new RustValidationIssue("condition", "composite[0].sustained", "minutes_not_positive", "minutes"),
            new RustValidationIssue("auto_resolve", "auto_resolve", "invalid_field", "alert_id"),
            new RustValidationIssue("auto_resolve", "auto_resolve", "field_missing", "state"),
            new RustValidationIssue("snooze", "snooze[0].composite", "list_empty", "conditions"));
    }

    [NativeFact]
    public void Validator_skips_trees_that_are_never_evaluated()
    {
        var validator = new AlertRuleConditionValidator();

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
            new AlertRuleConditionValidator());

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

    [NativeTheory]
    [InlineData("Threshold", """{"direction": "below", "value": 70, "clear_margin": -5}""", "threshold", "margin_negative:clear_margin")]
    [InlineData("Iob", """{"operator": "==", "value": 2, "clear_margin": 1}""", "iob", "margin_on_equality:clear_margin")]
    [InlineData("Cob", """{"operator": ">", "value": 2, "clear_margin": "1"}""", "cob", "invalid_field:clear_margin")]
    [InlineData("SiteAge", """{"operator": ">=", "value": 72, "clear_margin": 5}""", "site_age", "unknown_field")]
    [InlineData("Not",
        """{"child": {"type": "reservoir", "reservoir": {"operator": "<", "value": 20, "clear_margin": -1}}}""",
        "not[0].reservoir", "margin_negative:clear_margin")]
    [InlineData("Sustained",
        """{"minutes": 15, "child": {"type": "composite", "composite": {"operator": "and", "conditions": [{"type": "iob", "iob": {"operator": ">", "value": 1}}, {"type": "threshold", "threshold": {"direction": "below", "value": 70, "clear_margin": 5}}]}}}""",
        "sustained[0].composite[1].threshold", "margin_under_sustained:clear_margin")]
    [InlineData("RateOfChange", """{"direction": "falling", "rate": 1, "clear_margin": 1.5}""", "rate_of_change", "margin_past_zero:clear_margin")]
    public async Task CreateRule_rejects_an_invalid_clear_margin(
        string type, string conditionParams, string path, string error)
    {
        var (controller, db) = CreateController(
            new AlertRuleConditionValidator());

        var result = await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = Enum.Parse<AlertConditionType>(type),
            ConditionParams = Json(conditionParams),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().BeOfType<ValidationProblemDetails>().Which.Errors
            .Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                [$"condition:{path}"] = [error],
            });
        (await db.AlertRules.CountAsync()).Should().Be(0);
    }

    [NativeTheory]
    [InlineData("Threshold", """{"direction": "below", "value": 70, "value_from": {"fact": "target_low"}}""", "threshold", "limit_conflict:value_from")]
    [InlineData("Threshold", """{"direction": "below"}""", "threshold", "field_missing:value")]
    [InlineData("Threshold", """{"direction": "above", "value_from": {"fact": "latest_glucose"}}""", "threshold", "self_reference:value_from")]
    [InlineData("Predicted", """{"operator": "<", "within_minutes": 30, "value_from": {"fact": "latest_glucose", "offset": -1000.5}}""", "predicted", "offset_out_of_range:offset")]
    [InlineData("EventualGlucose", """{"operator": "<", "value_from": {"fact": "target_low", "offset": "-10"}}""", "eventual_glucose", "invalid_field:offset")]
    [InlineData("EventualGlucose", """{"operator": "<", "value_from": {"fact": "max_iob"}}""", "eventual_glucose", "unknown_value:fact")]
    [InlineData("Iob", """{"operator": ">", "value": 2, "value_from": {"fact": "target_low"}}""", "iob", "unknown_field")]
    [InlineData("Sustained",
        """{"minutes": 15, "child": {"type": "threshold", "threshold": {"direction": "below", "value_from": {"fact": "target_low"}, "clear_margin": 5}}}""",
        "sustained[0].threshold", "margin_under_sustained:clear_margin")]
    public async Task CreateRule_rejects_an_invalid_limit_from_a_fact(
        string type, string conditionParams, string path, string error)
    {
        var (controller, db) = CreateController(
            new AlertRuleConditionValidator());

        var result = await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Below target",
            ConditionType = Enum.Parse<AlertConditionType>(type),
            ConditionParams = Json(conditionParams),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().BeOfType<ValidationProblemDetails>().Which.Errors
            .Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                [$"condition:{path}"] = [error],
            });
        (await db.AlertRules.CountAsync()).Should().Be(0);
    }

    [NativeTheory]
    [InlineData("""{"operator": "<", "value": 70, "within_minutes": 30, "curve": "iob", "min_of": ["cob"]}""", "selector_conflict:min_of")]
    [InlineData("""{"operator": "<", "value": 70, "within_minutes": 30, "min_of": ["iob"], "max_of": ["cob"]}""", "selector_conflict:max_of")]
    [InlineData("""{"operator": "<", "value": 70, "within_minutes": 30, "min_of": []}""", "list_empty:min_of")]
    [InlineData("""{"operator": "<", "value": 70, "within_minutes": 30, "curve": "ar2"}""", "unknown_value:curve")]
    [InlineData("""{"operator": "<", "value": 70, "within_minutes": 30, "max_of": ["iob", "Cob"]}""", "unknown_value:max_of")]
    public async Task CreateRule_rejects_an_invalid_curve_selection(string conditionParams, string error)
    {
        var (controller, db) = CreateController(
            new AlertRuleConditionValidator());

        var result = await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Low soon",
            ConditionType = AlertConditionType.Predicted,
            ConditionParams = Json(conditionParams),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().BeOfType<ValidationProblemDetails>().Which.Errors
            .Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                ["condition:predicted"] = [error],
            });
        (await db.AlertRules.CountAsync()).Should().Be(0);
    }

    [NativeTheory]
    [InlineData("Predicted", """{"operator": "<", "value": 70, "within_minutes": 30, "min_of": ["iob", "cob", "uam", "zt"]}""")]
    [InlineData("EventualGlucose", """{"operator": "<", "value": 80, "clear_margin": 5}""")]
    [InlineData("CarbsRequired", """{"operator": ">=", "value": 10}""")]
    public async Task CreateRule_saves_a_curve_selection_and_the_loop_conditions(string type, string conditionParams)
    {
        var (controller, db) = CreateController(
            new AlertRuleConditionValidator());

        var result = await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Loop",
            ConditionType = Enum.Parse<AlertConditionType>(type),
            ConditionParams = Json(conditionParams),
        }, CancellationToken.None);

        result.Result.Should().NotBeOfType<BadRequestObjectResult>();
        (await db.AlertRules.CountAsync()).Should().Be(1);
    }

    [NativeFact]
    public async Task CreateRule_saves_a_valid_clear_margin_as_written()
    {
        var (controller, db) = CreateController(
            new AlertRuleConditionValidator());

        var result = await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Low",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = Json("""{"direction": "below", "value": 70, "clear_margin": 5}"""),
        }, CancellationToken.None);

        result.Result.Should().NotBeOfType<BadRequestObjectResult>();
        var stored = await db.AlertRules.SingleAsync();
        JsonDocument.Parse(stored.ConditionParams).RootElement.GetProperty("clear_margin").GetDecimal()
            .Should().Be(5m);
    }
}
