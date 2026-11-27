using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Contracts.Timezones;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>A re-correction re-pulls a bounded window, so it never relies on how a connector reads an open one.</summary>
public class TimezoneTimelineControllerRecorrectTests
{
    private static (TimezoneTimelineController Controller, Func<SyncRequest?> Captured) Controller()
    {
        SyncRequest? captured = null;
        var sync = new Mock<IConnectorSyncService>();
        sync.Setup(s => s.TriggerSyncAsync("glooko", It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, SyncRequest, CancellationToken>((_, r, _) => captured = r)
            .ReturnsAsync(new SyncResult { Success = true });
        return (new TimezoneTimelineController(
            Mock.Of<ITimezoneTimelineService>(), sync.Object, NullLogger<TimezoneTimelineController>.Instance),
            () => captured);
    }

    [Fact]
    public async Task Recorrect_WithNoLowerBound_ReachesBackAFullWalk()
    {
        var (controller, captured) = Controller();

        await controller.Recorrect(new RecorrectRequest(null), CancellationToken.None);

        var request = captured()!;
        request.From.Should().Be(GlookoConnectorService.FullWalkStart(request.To!.Value));
    }

    [Fact]
    public async Task Recorrect_WithALowerBound_KeepsIt()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var (controller, captured) = Controller();

        await controller.Recorrect(new RecorrectRequest(from), CancellationToken.None);

        captured()!.From.Should().Be(from);
    }
}
