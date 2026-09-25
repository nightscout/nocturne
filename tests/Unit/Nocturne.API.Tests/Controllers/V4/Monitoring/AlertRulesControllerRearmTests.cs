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
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// A rule's re-arm hold (docs/alerts/engine-semantics.md §6.3) is dropped by an edit of its
/// conditions or enablement, and kept by any other edit.
/// </summary>
[Trait("Category", "Unit")]
public class AlertRulesControllerRearmTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid RuleId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private const string Body = """{"direction":"below","value":70}""";
    private const string Resolve = """{"type":"threshold","threshold":{"direction":"above","value":80}}""";

    private static async Task<(AlertRulesController Controller, NocturneDbContext Db)> CreateAsync(
        AlertRuleEvaluationGate? gate = null)
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
            IsEnabled = true,
            AutoResolveEnabled = true,
            AutoResolveParams = Resolve,
        });
        db.AlertTrackerState.Add(new AlertTrackerStateEntity
        {
            AlertRuleId = RuleId,
            TenantId = Tenant,
            State = "idle",
            AwaitingRearm = true,
        });
        await db.SaveChangesAsync();

        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.ValidateUpdate(
                It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<StoredConditionTrees>()))
            .Returns((AlertConditionType _, string body, bool _, string? autoResolve, string? client, StoredConditionTrees _) =>
                new ConditionUpdateCheck([], body, autoResolve, client, []));
        var controller = new AlertRulesController(
            new TestTenantDbContextFactory(db),
            Mock.Of<IAlertReferenceService>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            validator.Object,
            Mock.Of<ISecretEncryptionService>(),
            new AlertRuleRearm(gate ?? new AlertRuleEvaluationGate()),
            Mock.Of<ILogger<AlertRulesController>>());
        return (controller, db);
    }

    private static UpdateAlertRuleRequest Unchanged(string name = "Low") => new()
    {
        Name = name,
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = JsonSerializer.Deserialize<JsonElement>(Body),
        IsEnabled = true,
        AutoResolveEnabled = true,
        AutoResolveParams = JsonSerializer.Deserialize<JsonElement>(Resolve),
    };

    private static async Task<bool> AwaitingRearm(NocturneDbContext db) =>
        (await db.AlertTrackerState.AsNoTracking().SingleAsync(s => s.AlertRuleId == RuleId)).AwaitingRearm;

    public static TheoryData<string, Func<UpdateAlertRuleRequest, UpdateAlertRuleRequest>> ConditionEdits => new()
    {
        { "body", r => { r.ConditionParams = JsonSerializer.Deserialize<JsonElement>("""{"direction":"below","value":60}"""); return r; } },
        { "auto-resolve tree", r => { r.AutoResolveParams = JsonSerializer.Deserialize<JsonElement>("""{"type":"threshold","threshold":{"direction":"above","value":90}}"""); return r; } },
        { "auto-resolve off", r => { r.AutoResolveEnabled = false; return r; } },
        { "disabled", r => { r.IsEnabled = false; return r; } },
    };

    [Theory]
    [MemberData(nameof(ConditionEdits))]
    public async Task An_edit_of_the_conditions_or_enablement_rearms_the_rule(
        string edit, Func<UpdateAlertRuleRequest, UpdateAlertRuleRequest> change)
    {
        var (controller, db) = await CreateAsync();

        await controller.UpdateRule(RuleId, change(Unchanged()), CancellationToken.None);

        (await AwaitingRearm(db)).Should().BeFalse(edit);
    }

    [Fact]
    public async Task Another_edit_keeps_the_hold()
    {
        var (controller, db) = await CreateAsync();

        await controller.UpdateRule(RuleId, Unchanged(name: "Low glucose"), CancellationToken.None);

        (await AwaitingRearm(db)).Should().BeTrue();
    }

    [Fact]
    public async Task A_tree_reformatted_but_equal_as_json_keeps_the_hold_and_its_stored_text()
    {
        var (controller, db) = await CreateAsync();
        var request = Unchanged();
        request.ConditionParams = JsonSerializer.Deserialize<JsonElement>("""{ "value": 70, "direction": "below" }""");
        request.AutoResolveParams = JsonSerializer.Deserialize<JsonElement>(
            """{ "threshold": { "value": 80, "direction": "above" }, "type": "threshold" }""");

        await controller.UpdateRule(RuleId, request, CancellationToken.None);

        (await AwaitingRearm(db)).Should().BeTrue();
        var stored = await db.AlertRules.AsNoTracking().SingleAsync(r => r.Id == RuleId);
        stored.ConditionParams.Should().Be(Body);
        stored.AutoResolveParams.Should().Be(Resolve);
    }

    [Fact]
    public async Task The_hold_is_cleared_only_once_an_evaluation_holding_the_rule_lets_go()
    {
        var gate = new AlertRuleEvaluationGate();
        var (controller, db) = await CreateAsync(gate);
        var evaluation = await gate.AcquireAsync(RuleId, CancellationToken.None);

        var toggle = controller.ToggleRule(RuleId, CancellationToken.None);
        await Task.Delay(100);
        toggle.IsCompleted.Should().BeFalse();
        (await AwaitingRearm(db)).Should().BeTrue();

        evaluation.Dispose();
        await toggle.WaitAsync(TimeSpan.FromSeconds(5));
        (await AwaitingRearm(db)).Should().BeFalse();
    }

    [Fact]
    public async Task A_toggle_rearms_the_rule()
    {
        var (controller, db) = await CreateAsync();

        await controller.ToggleRule(RuleId, CancellationToken.None);

        (await AwaitingRearm(db)).Should().BeFalse();
    }
}
