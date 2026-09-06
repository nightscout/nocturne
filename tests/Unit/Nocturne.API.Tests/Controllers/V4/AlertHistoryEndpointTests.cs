using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class AlertHistoryEndpointTests
{
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly Mock<ITenantDbContextFactory> _contextFactoryMock = new();
    private readonly Mock<IAlertAcknowledgementService> _acknowledgementServiceMock = new();
    private readonly Mock<IAlertDeliveryService> _deliveryServiceMock = new();
    private readonly Mock<ITenantAccessor> _tenantAccessorMock = new();
    private readonly Mock<ILogger<AlertsController>> _loggerMock = new();

    private readonly Guid _tenantId = Guid.NewGuid();

    public AlertHistoryEndpointTests()
    {
        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"alerts_history_endpoint_tests_{Guid.NewGuid()}")
            .Options;
        using (var db = new NocturneDbContext(_options))
        {
            db.Database.EnsureCreated();
        }

        _tenantAccessorMock.Setup(t => t.IsResolved).Returns(true);
        _tenantAccessorMock.Setup(t => t.TenantId).Returns(_tenantId);

        _contextFactoryMock
            .Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var ctx = new NocturneDbContext(_options) { TenantId = _tenantId };
                return ValueTask.FromResult(ctx);
            });
    }

    private AlertsController CreateController()
    {
        var controller = new AlertsController(
            _contextFactoryMock.Object,
            _acknowledgementServiceMock.Object,
            _deliveryServiceMock.Object,
            _tenantAccessorMock.Object,
            _loggerMock.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    private async Task<Guid> SeedEndedExcursionAsync(DateTime endedAt, bool isTest = false)
    {
        await using var db = new NocturneDbContext(_options);
        db.TenantId = _tenantId;
        // The history query Includes AlertRule over a required FK, so the rule row must exist.
        var rule = new AlertRuleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Name = "Test rule",
        };
        db.AlertRules.Add(rule);
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            AlertRuleId = rule.Id,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            EndedAt = endedAt,
        };
        db.AlertExcursions.Add(excursion);
        db.AlertInstances.Add(new AlertInstanceEntity
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            AlertExcursionId = excursion.Id,
            Status = isTest ? "test" : "resolved",
            TriggeredAt = excursion.StartedAt,
            ResolvedAt = excursion.StartedAt,
            IsTest = isTest,
        });
        await db.SaveChangesAsync();
        return excursion.Id;
    }

    private static AlertHistoryResponse Body(ActionResult<AlertHistoryResponse> result)
    {
        return result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AlertHistoryResponse>().Subject;
    }

    [Fact]
    public async Task GetAlertHistory_FutureEndedTestFire_ExcludedUntilWindowLapses()
    {
        // A device_action test fire carries an EndedAt up to 90s in the future so it reads as
        // live in the active-intents snapshot; history is completed excursions only, so it must
        // not appear (pinned to the top by the EndedAt-desc ordering) while the window is open —
        // even when the caller opts into test fires.
        var pastId = await SeedEndedExcursionAsync(DateTime.UtcNow.AddMinutes(-10));
        await SeedEndedExcursionAsync(DateTime.UtcNow.AddSeconds(90), isTest: true);

        var controller = CreateController();
        var result = await controller.GetAlertHistory(includeTest: true, ct: CancellationToken.None);

        var body = Body(result);
        body.TotalCount.Should().Be(1);
        body.Items.Should().ContainSingle().Which.Id.Should().Be(pastId);
    }

    [Fact]
    public async Task GetAlertHistory_PastEndedTestFire_IncludedOnlyWithIncludeTest()
    {
        var realId = await SeedEndedExcursionAsync(DateTime.UtcNow.AddMinutes(-10));
        var testId = await SeedEndedExcursionAsync(DateTime.UtcNow.AddMinutes(-5), isTest: true);

        var controller = CreateController();

        var withTest = Body(await controller.GetAlertHistory(includeTest: true, ct: CancellationToken.None));
        withTest.Items.Select(i => i.Id).Should().BeEquivalentTo([realId, testId]);
        withTest.Items.First(i => i.Id == testId).IsTest.Should().BeTrue();

        var withoutTest = Body(await controller.GetAlertHistory(ct: CancellationToken.None));
        withoutTest.Items.Should().ContainSingle().Which.Id.Should().Be(realId);
    }

    [Fact]
    public async Task GetAlertHistory_PageSizeAtCeiling_IsServedUnchanged()
    {
        var controller = CreateController();

        var body = Body(await controller.GetAlertHistory(
            pageSize: V4ReadLimits.MaxOrdinalPageSize, ct: CancellationToken.None));

        body.PageSize.Should().Be(V4ReadLimits.MaxOrdinalPageSize);
    }

    [Fact]
    public async Task GetAlertHistory_PageSizeAboveCeiling_IsClamped()
    {
        var controller = CreateController();

        var body = Body(await controller.GetAlertHistory(
            pageSize: V4ReadLimits.MaxOrdinalPageSize + 1, ct: CancellationToken.None));

        body.PageSize.Should().Be(V4ReadLimits.MaxOrdinalPageSize);
    }

    [Fact]
    public async Task GetAlertHistory_PageBelowTheFirst_IsNormalized()
    {
        var controller = CreateController();

        var body = Body(await controller.GetAlertHistory(page: 0, ct: CancellationToken.None));

        body.Page.Should().Be(1);
    }

    [Fact]
    public async Task GetAlertHistory_PageAtTheDeepestReachable_IsServedUnchanged()
    {
        const int pageSize = V4ReadLimits.MaxOrdinalPageSize;
        var deepest = V4ReadLimits.MaxPageSize / pageSize;
        var controller = CreateController();

        var body = Body(await controller.GetAlertHistory(
            page: deepest, pageSize: pageSize, ct: CancellationToken.None));

        body.Page.Should().Be(deepest);
    }

    [Fact]
    public async Task GetAlertHistory_PagePastTheDeepestReachable_IsClamped()
    {
        const int pageSize = V4ReadLimits.MaxOrdinalPageSize;
        var deepest = V4ReadLimits.MaxPageSize / pageSize;
        var controller = CreateController();

        // int.MaxValue also overflows the page-to-offset multiplication when it is not clamped.
        var body = Body(await controller.GetAlertHistory(
            page: int.MaxValue, pageSize: pageSize, ct: CancellationToken.None));

        body.Page.Should().Be(deepest);
    }
}
