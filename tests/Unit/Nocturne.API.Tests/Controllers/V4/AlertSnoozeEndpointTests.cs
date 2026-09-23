using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// The snooze endpoint over a real <see cref="AlertSnoozeService"/>. Its cap is read from the same
/// <see cref="SmartSnoozeConfig"/> the sweep extends against, so the two cannot disagree about a
/// rule's <c>maxCount</c>; the active-alerts read exposes the snooze the endpoint wrote.
/// </summary>
[Trait("Category", "Unit")]
public class AlertSnoozeEndpointTests
{
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly Mock<ITenantDbContextFactory> _contextFactory = new();
    private readonly Guid _tenantId = Guid.NewGuid();

    private sealed class InMemoryFactory(DbContextOptions<NocturneDbContext> options) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    public AlertSnoozeEndpointTests()
    {
        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"alerts_snooze_endpoint_tests_{Guid.NewGuid()}")
            .Options;
        using (var db = new NocturneDbContext(_options))
        {
            db.Database.EnsureCreated();
        }

        _contextFactory
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(new NocturneDbContext(_options) { TenantId = _tenantId }));
    }

    private AlertsController CreateController()
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);

        var snoozeService = new AlertSnoozeService(
            new InMemoryFactory(_options),
            tenantAccessor.Object,
            Mock.Of<IAlertRepository>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<ISensorContextEnricher>(),
            Mock.Of<ICanonicalGlucoseService>(),
            Mock.Of<ISignalRBroadcastService>(),
            TimeProvider.System,
            NullLogger<AlertSnoozeService>.Instance);

        return new AlertsController(
            _contextFactory.Object,
            Mock.Of<IAlertAcknowledgementService>(),
            Mock.Of<IAlertDeliveryService>(),
            snoozeService,
            tenantAccessor.Object,
            Mock.Of<ILogger<AlertsController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private async Task<(Guid ExcursionId, Guid InstanceId)> SeedInstanceAsync(
        string clientConfiguration = "{}",
        int snoozeCount = 0,
        DateTime? snoozedUntil = null,
        bool resolved = false)
    {
        await using var db = new NocturneDbContext(_options) { TenantId = _tenantId };
        var rule = new AlertRuleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Name = "Low",
            ClientConfiguration = clientConfiguration,
        };
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            AlertRuleId = rule.Id,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            EndedAt = resolved ? DateTime.UtcNow.AddMinutes(-1) : null,
        };
        var instance = new AlertInstanceEntity
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            AlertExcursionId = excursion.Id,
            TriggeredAt = DateTime.UtcNow.AddMinutes(-5),
            ResolvedAt = resolved ? DateTime.UtcNow.AddMinutes(-1) : null,
            SnoozeCount = snoozeCount,
            SnoozedUntil = snoozedUntil,
        };
        db.AlertRules.Add(rule);
        db.AlertExcursions.Add(excursion);
        db.AlertInstances.Add(instance);
        await db.SaveChangesAsync();
        return (excursion.Id, instance.Id);
    }

    private async Task<ActionResult> SnoozeAsync(Guid instanceId)
        => await CreateController().SnoozeInstance(
            instanceId, new SnoozeRequest { Minutes = 15 }, CancellationToken.None);

    private static void ShouldBeConflict(ActionResult result, string detail)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(detail);
    }

    private async Task<ActiveExcursionResponse> SingleActiveAsync()
    {
        var result = await CreateController().GetActiveAlerts(CancellationToken.None);
        var list = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<ActiveExcursionResponse>>().Subject;
        return list.Should().ContainSingle().Subject;
    }

    // ---- Snooze ----

    [Fact]
    public async Task Snooze_returns_no_content_and_is_reported_on_active_alerts()
    {
        var (_, instanceId) = await SeedInstanceAsync();
        var before = DateTime.UtcNow;

        (await SnoozeAsync(instanceId)).Should().BeOfType<NoContentResult>();

        var excursion = await SingleActiveAsync();
        excursion.SnoozedUntil.Should().BeCloseTo(before.AddMinutes(15), TimeSpan.FromSeconds(30));
        excursion.AcknowledgedAt.Should().BeNull("a snoozed alert is still unacknowledged");
        var instance = excursion.ActiveInstances.Should().ContainSingle().Subject;
        instance.Id.Should().Be(instanceId);
        instance.SnoozedUntil.Should().Be(excursion.SnoozedUntil);
        instance.SnoozeCount.Should().Be(1);
    }

    [Fact]
    public async Task Snooze_of_unknown_instance_is_404()
    {
        (await SnoozeAsync(Guid.NewGuid())).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Snooze_of_resolved_instance_is_409()
    {
        var (_, instanceId) = await SeedInstanceAsync(resolved: true);

        ShouldBeConflict(await SnoozeAsync(instanceId), "Alert is no longer active");
    }

    [Fact]
    public async Task MaxCountOmitted_AllowsSnoozesUpToTheSharedDefault()
    {
        var (_, belowCap) = await SeedInstanceAsync(snoozeCount: SmartSnoozeConfig.DefaultMaxCount - 1);
        var (_, atCap) = await SeedInstanceAsync(snoozeCount: SmartSnoozeConfig.DefaultMaxCount);

        (await SnoozeAsync(belowCap)).Should().BeOfType<NoContentResult>();
        ShouldBeConflict(await SnoozeAsync(atCap), "Maximum snooze count reached");
    }

    [Fact]
    public async Task ExplicitMaxCount_IsHonoured()
    {
        var (_, instanceId) = await SeedInstanceAsync("""{"snooze":{"maxCount":5}}""", 4);

        (await SnoozeAsync(instanceId)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task MaxCountOfTheWrongKind_FallsBackToTheDefault()
    {
        var (_, instanceId) = await SeedInstanceAsync("""{"snooze":{"maxCount":"5"}}""", SmartSnoozeConfig.DefaultMaxCount);

        ShouldBeConflict(await SnoozeAsync(instanceId), "Maximum snooze count reached");
    }

    // ---- Active alerts ----

    [Fact]
    public async Task Active_alerts_do_not_report_a_lapsed_snooze_the_sweep_has_not_cleared_yet()
    {
        await SeedInstanceAsync(snoozeCount: 1, snoozedUntil: DateTime.UtcNow.AddSeconds(-5));

        var excursion = await SingleActiveAsync();

        excursion.SnoozedUntil.Should().BeNull();
        excursion.ActiveInstances.Single().SnoozedUntil.Should().BeNull();
    }

    [Fact]
    public async Task Active_alerts_report_no_snooze_for_an_unsnoozed_alert()
    {
        await SeedInstanceAsync();

        var excursion = await SingleActiveAsync();

        excursion.SnoozedUntil.Should().BeNull();
    }
}
