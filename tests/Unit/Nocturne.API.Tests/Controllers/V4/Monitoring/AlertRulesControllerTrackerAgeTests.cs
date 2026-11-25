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
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>A tracker_age rule must name a tracker definition of the tenant, or it never fires.</summary>
[Trait("Category", "Unit")]
public class AlertRulesControllerTrackerAgeTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static (AlertRulesController Controller, NocturneDbContext Db) CreateController()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new NocturneDbContext(options) { TenantId = Tenant };
        var validator = new Mock<IAlertRuleConditionValidator>();
        validator
            .Setup(v => v.Validate(
                It.IsAny<AlertConditionType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns([]);
        var controller = new AlertRulesController(
            new TestNocturneDbContextFactory(db),
            new TenantMembershipRepository(new TestNocturneDbContextFactory(db), AccountLinkRoutes.Off),
            new SubjectRepository(new TestNocturneDbContextFactory(db), AccountLinkRoutes.Off),
            new AlertRepository(new TestNocturneDbContextFactory(db)),
            new AlertRuleRepository(new TestNocturneDbContextFactory(db), TimeProvider.System),
            MockTenantAccessor.Create(db.TenantId).Object,
            Mock.Of<IAlertReferenceService>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<IRuleScopeClassifier>(),
            validator.Object,
            Mock.Of<ISecretEncryptionService>(),
            Rearms.Unannounced(),
            new AlertRuleRetirement(Mock.Of<IExcursionTracker>(), Mock.Of<IExcursionResolutionHandler>(), Mock.Of<IUnevaluableRuleNotice>()),
            Mock.Of<ILogger<AlertRulesController>>(), TimeProvider.System);
        return (controller, db);
    }

    private static JsonElement TrackerAge(Guid definitionId) => JsonSerializer.Deserialize<JsonElement>(
        $$"""{"tracker_definition_id":"{{definitionId}}","operator":">=","minutes":600}""");

    [Fact]
    public async Task CreateRule_naming_an_unknown_tracker_definition_is_a_bad_request_and_saves_nothing()
    {
        var (controller, db) = CreateController();
        var unknown = Guid.NewGuid();

        var result = await controller.CreateRule(new CreateAlertRuleRequest
        {
            Name = "Sensor old",
            ConditionType = AlertConditionType.TrackerAge,
            ConditionParams = TrackerAge(unknown),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().Be($"Unknown tracker definition '{unknown}'.");
        (await db.AlertRules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpdateRule_naming_an_unknown_tracker_definition_is_a_bad_request_and_keeps_the_stored_rule()
    {
        var (controller, db) = CreateController();
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
        var unknown = Guid.NewGuid();

        var result = await controller.UpdateRule(id, new UpdateAlertRuleRequest
        {
            Name = "Sensor old",
            ConditionType = AlertConditionType.TrackerAge,
            ConditionParams = TrackerAge(unknown),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value
            .Should().Be($"Unknown tracker definition '{unknown}'.");
        (await db.AlertRules.AsNoTracking().SingleAsync()).Name.Should().Be("Stored");
    }
}
