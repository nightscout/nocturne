using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// Tests for the connector cursor-reset endpoint. The key behaviour is that it forces an
/// explicit-range re-pull (sets <see cref="SyncRequest.To"/>), which bypasses the per-type
/// catch-up cursors so history is genuinely re-ingested rather than resumed from the latest
/// stored record.
/// </summary>
public class ServicesControllerResetCursorTests
{
    private static ServicesController CreateController(IConnectorSyncService syncService) =>
        new(
            Mock.Of<IDataSourceService>(),
            Mock.Of<IConnectorHealthService>(),
            syncService,
            Mock.Of<ILogger<ServicesController>>(),
            Mock.Of<IConfiguration>());

    [Fact]
    public async Task ResetConnectorCursor_SetsUpperBound_ToForceExplicitRangeRePull()
    {
        SyncRequest? captured = null;
        var syncService = new Mock<IConnectorSyncService>();
        syncService
            .Setup(s => s.TriggerSyncAsync("nightscout", It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, SyncRequest, CancellationToken>((_, r, _) => captured = r)
            .ReturnsAsync(new SyncResult { Success = true });

        var from = new DateTime(2025, 12, 27, 0, 0, 0, DateTimeKind.Utc);
        var controller = CreateController(syncService.Object);

        var result = await controller.ResetConnectorCursor(
            "nightscout",
            new ResetCursorRequest { From = from, DataTypes = [SyncDataType.Boluses, SyncDataType.CarbIntake] },
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        captured.Should().NotBeNull();
        captured!.To.Should().NotBeNull(
            "an explicit upper bound is what switches the connector out of catch-up mode into a full re-pull");
        captured.From.Should().Be(from, "the requested lower bound is passed through verbatim");
        captured.DataTypes.Should().BeEquivalentTo([SyncDataType.Boluses, SyncDataType.CarbIntake]);
    }

    [Fact]
    public async Task ResetConnectorCursor_EmptyRequest_RePullsAllTypesFromBeginning()
    {
        SyncRequest? captured = null;
        var syncService = new Mock<IConnectorSyncService>();
        syncService
            .Setup(s => s.TriggerSyncAsync(It.IsAny<string>(), It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, SyncRequest, CancellationToken>((_, r, _) => captured = r)
            .ReturnsAsync(new SyncResult { Success = true });

        var controller = CreateController(syncService.Object);

        await controller.ResetConnectorCursor("nightscout", new ResetCursorRequest(), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.From.Should().BeNull("a null lower bound means re-pull all available history");
        captured.To.Should().NotBeNull();
        captured.DataTypes.Should().BeEmpty("an empty data-type set means reset every supported type");
    }
}
