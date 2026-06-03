using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Lifecycle behaviour of <see cref="ConnectorCursorResetJobService"/>: it validates the tenant up
/// front (404 for unknown), seeds per-connector progress before any work runs, and drives the
/// background fan-out to a terminal state while reflecting per-connector outcomes from the engine's
/// <see cref="IConnectorResetProgress"/> callbacks. The engine itself is mocked so these tests are
/// deterministic and need no database.
/// </summary>
public class ConnectorCursorResetJobServiceTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();

    private static TenantConnectorsDto Connectors(Guid tenantId) => new(
        tenantId, "erik",
        [
            new TenantConnectorSummary("nightscout", true, null, null, null),
            new TenantConnectorSummary("dexcom", false, null, null, "boom"),
        ]);

    /// <summary>
    /// Builds a job service whose background scope resolves the supplied engine mock.
    /// </summary>
    private static ConnectorCursorResetJobService BuildService(IConnectorCursorResetService engine)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => engine);
        var provider = services.BuildServiceProvider();
        return new ConnectorCursorResetJobService(
            NullLogger<ConnectorCursorResetJobService>.Instance, provider);
    }

    private static async Task<ConnectorResetJobStatus> WaitForTerminalAsync(
        ConnectorCursorResetJobService service, Guid jobId)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = service.GetStatus(jobId);
            if (status.State is ConnectorResetJobState.Completed
                or ConnectorResetJobState.Failed
                or ConnectorResetJobState.Cancelled)
            {
                return status;
            }
            await Task.Delay(20);
        }

        throw new TimeoutException("Reset job did not reach a terminal state in time.");
    }

    [Fact]
    public async Task StartResetAsync_UnknownTenant_ReturnsNull()
    {
        var engine = new Mock<IConnectorCursorResetService>();
        engine.Setup(e => e.GetTenantConnectorsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantConnectorsDto?)null);

        var service = BuildService(engine.Object);

        var info = await service.StartResetAsync(_tenantId, null, null, CancellationToken.None);

        info.Should().BeNull();
    }

    [Fact]
    public async Task StartResetAsync_SeedsEveryConnectorAsPending()
    {
        var engine = new Mock<IConnectorCursorResetService>();
        engine.Setup(e => e.GetTenantConnectorsAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Connectors(_tenantId));
        // Never completes, so the status we read is the seeded one.
        engine.Setup(e => e.ResetTenantCursorsAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<List<SyncDataType>?>(),
                It.IsAny<IConnectorResetProgress?>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<TenantCursorResetResult?>().Task);

        var service = BuildService(engine.Object);

        var info = await service.StartResetAsync(_tenantId, null, null, CancellationToken.None);

        info.Should().NotBeNull();
        info!.TenantSlug.Should().Be("erik");
        info.TotalConnectors.Should().Be(2);

        var status = service.GetStatus(info.JobId);
        status.TotalConnectors.Should().Be(2);
        status.CompletedConnectors.Should().Be(0);
        status.Connectors.Select(c => c.ConnectorName).Should().ContainInOrder("nightscout", "dexcom");
        status.Connectors.Should().OnlyContain(c => c.State == ConnectorResetConnectorState.Pending);
    }

    [Fact]
    public async Task StartResetAsync_RunsToCompletion_ReflectingPerConnectorOutcomes()
    {
        var engine = new Mock<IConnectorCursorResetService>();
        engine.Setup(e => e.GetTenantConnectorsAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Connectors(_tenantId));
        engine.Setup(e => e.ResetTenantCursorsAsync(
                _tenantId, It.IsAny<DateTime?>(), It.IsAny<List<SyncDataType>?>(),
                It.IsAny<IConnectorResetProgress?>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, DateTime?, List<SyncDataType>?, IConnectorResetProgress?, CancellationToken>(
                (tenantId, _, _, progress, _) =>
                {
                    progress!.ConnectorStarted("nightscout");
                    progress.ConnectorCompleted(new ConnectorCursorResetResult(
                        "nightscout", new SyncResult { Success = true, Message = "ok" }));
                    progress.ConnectorStarted("dexcom");
                    progress.ConnectorCompleted(new ConnectorCursorResetResult(
                        "dexcom", new SyncResult { Success = false, Message = "nope" }));
                    return Task.FromResult<TenantCursorResetResult?>(
                        new TenantCursorResetResult(tenantId, "erik", []));
                });

        var service = BuildService(engine.Object);

        var info = await service.StartResetAsync(_tenantId, null, null, CancellationToken.None);
        info.Should().NotBeNull();

        var status = await WaitForTerminalAsync(service, info!.JobId);

        status.State.Should().Be(ConnectorResetJobState.Completed);
        status.CompletedConnectors.Should().Be(2);
        status.Connectors.Should().Contain(c =>
            c.ConnectorName == "nightscout" && c.State == ConnectorResetConnectorState.Succeeded);
        status.Connectors.Should().Contain(c =>
            c.ConnectorName == "dexcom" && c.State == ConnectorResetConnectorState.Failed && c.Message == "nope");
    }

    [Fact]
    public void GetStatus_UnknownJob_Throws()
    {
        var service = BuildService(Mock.Of<IConnectorCursorResetService>());
        Assert.Throws<KeyNotFoundException>(() => service.GetStatus(Guid.CreateVersion7()));
    }

    [Fact]
    public void Cancel_UnknownJob_Throws()
    {
        var service = BuildService(Mock.Of<IConnectorCursorResetService>());
        Assert.Throws<KeyNotFoundException>(() => service.Cancel(Guid.CreateVersion7()));
    }
}
