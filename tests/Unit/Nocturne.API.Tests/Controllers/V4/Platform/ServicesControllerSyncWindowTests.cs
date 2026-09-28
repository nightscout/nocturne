using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// A window whose lower bound is not before its upper bound covers no time, and every connector
/// answers one with zero work and a success. Both sync endpoints refuse it before it reaches one.
/// </summary>
public class ServicesControllerSyncWindowTests
{
    private readonly Mock<IConnectorSyncService> _syncService = new();

    private ServicesController CreateController() =>
        new(
            Mock.Of<IDataSourceService>(),
            Mock.Of<IConnectorHealthService>(),
            _syncService.Object,
            Mock.Of<ILogger<ServicesController>>(),
            Mock.Of<ITenantAccessor>(),
            Options.Create(new BaseDomainOptions()));

    private static void ShouldBeBadRequestNamingFrom(ActionResult<SyncResult> result)
    {
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Contain("'from'");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task TriggerConnectorSync_WhenFromIsNotBeforeTo_Returns400WithoutSyncing(int fromAfterToDays)
    {
        var to = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await CreateController().TriggerConnectorSync(
            "glooko", new SyncRequest { From = to.AddDays(fromAfterToDays), To = to }, CancellationToken.None);

        ShouldBeBadRequestNamingFrom(result);
        _syncService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TriggerConnectorSync_WhenFromIsBeforeTo_Syncs()
    {
        _syncService
            .Setup(s => s.TriggerSyncAsync("glooko", It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncResult { Success = true });
        var to = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await CreateController().TriggerConnectorSync(
            "glooko", new SyncRequest { From = to.AddDays(-1), To = to }, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ResetConnectorCursor_WhenFromIsInTheFuture_Returns400WithoutSyncing()
    {
        var result = await CreateController().ResetConnectorCursor(
            "glooko", new ResetCursorRequest { From = DateTime.UtcNow.AddDays(1) }, CancellationToken.None);

        ShouldBeBadRequestNamingFrom(result);
        _syncService.VerifyNoOtherCalls();
    }
}
