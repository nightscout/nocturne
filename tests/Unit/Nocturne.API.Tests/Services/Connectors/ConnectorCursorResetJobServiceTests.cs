using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Lifecycle and durability behaviour of <see cref="ConnectorCursorResetJobService"/>: it validates
/// the tenant up front (404 for unknown), seeds per-connector progress before any work runs, drives
/// the background fan-out to a terminal state while reflecting per-connector outcomes from the
/// engine's <see cref="IConnectorResetProgress"/> callbacks, and persists job records so lookups
/// survive the in-memory job map being lost (an API restart mid-run previously turned a running job
/// into a bare 404). The engine itself is mocked so these tests are deterministic.
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
    /// Builds a job service whose background scope resolves the supplied engine mock, backed by an
    /// InMemory NocturneDbContext for job record persistence. Pass an existing provider to model a
    /// restarted process sharing the same database.
    /// </summary>
    private static (ConnectorCursorResetJobService Service, IServiceProvider Provider) BuildService(
        IConnectorCursorResetService engine,
        IServiceProvider? existingProvider = null)
    {
        var dbName = $"reset-jobs-{Guid.NewGuid():N}";
        var provider = existingProvider ?? new ServiceCollection()
            .AddScoped(_ => engine)
            .AddDbContext<NocturneDbContext>(o => o.UseInMemoryDatabase(dbName))
            .BuildServiceProvider();

        var service = new ConnectorCursorResetJobService(
            NullLogger<ConnectorCursorResetJobService>.Instance, provider);
        return (service, provider);
    }

    private static async Task<ConnectorResetJobStatus> WaitForTerminalAsync(
        ConnectorCursorResetJobService service, Guid jobId)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await service.GetStatusAsync(jobId);
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

        var (service, _) = BuildService(engine.Object);

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

        var (service, _) = BuildService(engine.Object);

        var info = await service.StartResetAsync(_tenantId, null, null, CancellationToken.None);

        info.Should().NotBeNull();
        info!.TenantSlug.Should().Be("erik");
        info.TotalConnectors.Should().Be(2);

        var status = await service.GetStatusAsync(info.JobId);
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

        var (service, _) = BuildService(engine.Object);

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
    public async Task GetStatusAsync_UnknownJob_Throws()
    {
        var (service, _) = BuildService(Mock.Of<IConnectorCursorResetService>());
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetStatusAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task CancelAsync_UnknownJob_Throws()
    {
        var (service, _) = BuildService(Mock.Of<IConnectorCursorResetService>());
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.CancelAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task GetStatusAsync_ServesThePersistedRecord_WhenTheJobIsNotInMemory()
    {
        // Regression: reset jobs lived only in a ConcurrentDictionary, so an API restart mid-run
        // made the job id answer 404 — indistinguishable from a job that never existed.
        var engine = new Mock<IConnectorCursorResetService>();
        engine.Setup(e => e.GetTenantConnectorsAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Connectors(_tenantId));
        engine.Setup(e => e.ResetTenantCursorsAsync(
                _tenantId, It.IsAny<DateTime?>(), It.IsAny<List<SyncDataType>?>(),
                It.IsAny<IConnectorResetProgress?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantCursorResetResult(_tenantId, "erik", []));

        var (service, provider) = BuildService(engine.Object);

        var info = await service.StartResetAsync(_tenantId, null, null, CancellationToken.None);
        await WaitForTerminalAsync(service, info!.JobId);

        // A fresh service over the same store models the post-restart process: empty job map,
        // same database.
        var (restarted, _) = BuildService(engine.Object, provider);

        var status = await restarted.GetStatusAsync(info.JobId);
        status.JobId.Should().Be(info.JobId);
        status.TenantId.Should().Be(_tenantId);
        status.TenantSlug.Should().Be("erik");
        status.State.Should().Be(ConnectorResetJobState.Completed);

        // Cancel on the persisted terminal record must not 404 and must not throw.
        await restarted.CancelAsync(info.JobId);
    }
}
