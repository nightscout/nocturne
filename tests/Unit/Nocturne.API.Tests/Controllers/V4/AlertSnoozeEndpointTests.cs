using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// The manual snooze endpoint reads its cap from the same <see cref="SmartSnoozeConfig"/> the
/// sweep extends against, so the two cannot disagree about a rule's <c>maxCount</c>.
/// </summary>
[Trait("Category", "Unit")]
public class AlertSnoozeEndpointTests
{
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly Mock<ITenantDbContextFactory> _contextFactory = new();
    private readonly Guid _tenantId = Guid.NewGuid();

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

        return new AlertsController(
            _contextFactory.Object,
            Mock.Of<IAlertAcknowledgementService>(),
            Mock.Of<IAlertDeliveryService>(),
            tenantAccessor.Object,
            Mock.Of<ILogger<AlertsController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private async Task<Guid> SeedInstanceAsync(string clientConfiguration, int snoozeCount)
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
        };
        var instance = new AlertInstanceEntity
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            AlertExcursionId = excursion.Id,
            TriggeredAt = DateTime.UtcNow.AddMinutes(-5),
            SnoozeCount = snoozeCount,
        };
        db.AlertRules.Add(rule);
        db.AlertExcursions.Add(excursion);
        db.AlertInstances.Add(instance);
        await db.SaveChangesAsync();
        return instance.Id;
    }

    private async Task<ActionResult> SnoozeAsync(Guid instanceId)
        => await CreateController().SnoozeInstance(
            instanceId, new SnoozeRequest { Minutes = 15 }, CancellationToken.None);

    [Fact]
    public async Task MaxCountOmitted_AllowsSnoozesUpToTheSharedDefault()
    {
        var belowCap = await SeedInstanceAsync("{}", SmartSnoozeConfig.DefaultMaxCount - 1);
        var atCap = await SeedInstanceAsync("{}", SmartSnoozeConfig.DefaultMaxCount);

        (await SnoozeAsync(belowCap)).Should().BeOfType<NoContentResult>();
        (await SnoozeAsync(atCap)).Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task ExplicitMaxCount_IsHonoured()
    {
        var instance = await SeedInstanceAsync("""{"snooze":{"maxCount":5}}""", 4);

        (await SnoozeAsync(instance)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task MaxCountOfTheWrongKind_FallsBackToTheDefault()
    {
        var instance = await SeedInstanceAsync("""{"snooze":{"maxCount":"5"}}""", SmartSnoozeConfig.DefaultMaxCount);

        (await SnoozeAsync(instance)).Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }
}
