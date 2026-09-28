using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

public class ConnectorSyncServiceTests
{
    private static ConnectorSyncService CreateService(
        IServiceProvider serviceProvider,
        ITenantAccessor? tenantAccessor = null)
    {
        var ta = tenantAccessor ?? CreateTenantAccessor();
        var logger = NullLogger<ConnectorSyncService>.Instance;
        var progressReporter = Mock.Of<ISyncProgressReporter>();
        return new ConnectorSyncService(serviceProvider, ta, logger, progressReporter, new TenantRunGuard());
    }

    private static ITenantAccessor CreateTenantAccessor(TenantContext? context = null)
    {
        var mock = new Mock<ITenantAccessor>();
        mock.Setup(x => x.Context).Returns(context);
        return mock.Object;
    }

    private static IServiceProvider BuildProvider(
        params IConnectorSyncExecutor[] executors)
    {
        var services = new ServiceCollection();

        // Register a scoped ITenantAccessor so the service can resolve it in the child scope
        services.AddScoped<ITenantAccessor>(_ =>
        {
            var mock = new Mock<ITenantAccessor>();
            return mock.Object;
        });

        // Mirrors the production registration (Program.cs) — the sync scope resolves
        // the mutable scoped AuditContext to mark it system for the sync's duration.
        services.AddScoped<IAuditContext, AuditContext>();

        // A real NocturneDbContext: the sync scope stamps its AuditContext property, which the
        // mutation interceptor prefers over the ambient context. Sqlite rather than InMemory so
        // the relational model builds; no connection is ever opened because nothing queries.
        services.AddScoped(_ => new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseSqlite("DataSource=:memory:")
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options));

        foreach (var executor in executors)
        {
            services.AddSingleton<IConnectorSyncExecutor>(executor);
        }

        return services.BuildServiceProvider();
    }

    private static IConnectorSyncExecutor CreateMockExecutor(
        string connectorId,
        SyncResult? result = null,
        Action<IServiceProvider>? onSyncScope = null)
    {
        var syncResult = result ?? new SyncResult { Success = true, Message = "OK" };
        var mock = new Mock<IConnectorSyncExecutor>();
        mock.Setup(x => x.ConnectorId).Returns(connectorId);
        mock.Setup(x => x.ExecuteSyncAsync(
                It.IsAny<IServiceProvider>(),
                It.IsAny<SyncRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ISyncProgressReporter?>()))
            .ReturnsAsync((IServiceProvider sp, SyncRequest _, CancellationToken _, ISyncProgressReporter? _) =>
            {
                onSyncScope?.Invoke(sp);
                return syncResult;
            });
        return mock.Object;
    }

    [Fact]
    public async Task TriggerSyncAsync_WithKnownConnector_DelegatesToExecutor()
    {
        // Arrange
        var expectedResult = new SyncResult { Success = true, Message = "Synced 42 entries" };
        var executor = CreateMockExecutor("test", expectedResult);
        var provider = BuildProvider(executor);
        var sut = CreateService(provider);
        var request = new SyncRequest();

        // Act
        var result = await sut.TriggerSyncAsync("test", request, CancellationToken.None);

        // Assert
        result.Should().BeSameAs(expectedResult);
        result.Success.Should().BeTrue();
        result.Message.Should().Be("Synced 42 entries");

        Mock.Get(executor).Verify(
            x => x.ExecuteSyncAsync(
                It.IsAny<IServiceProvider>(),
                request,
                CancellationToken.None,
                It.IsAny<ISyncProgressReporter?>()),
            Times.Once);
    }

    [Fact]
    public async Task TriggerSyncAsync_WhenTheExecutorIsCancelled_PropagatesTheCancellation()
    {
        // A cancelled manual sync is the caller withdrawing it, not a failure of the connector, so
        // it must travel through rather than being reported as "Sync failed: A task was canceled."
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var executor = new Mock<IConnectorSyncExecutor>();
        executor.Setup(x => x.ConnectorId).Returns("test");
        executor.Setup(x => x.ExecuteSyncAsync(
                It.IsAny<IServiceProvider>(),
                It.IsAny<SyncRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ISyncProgressReporter?>()))
            .ThrowsAsync(new OperationCanceledException());
        var sut = CreateService(BuildProvider(executor.Object));

        var act = async () =>
            await sut.TriggerSyncAsync("test", new SyncRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TriggerSyncAsync_WhenTheExecutorTimesOut_ReturnsAFailedResultCarryingTheText()
    {
        // A source timeout arrives as a TaskCanceledException while the caller's token is still
        // live, so it is a failure of the connector with a reason, not a withdrawn run.
        var executor = new Mock<IConnectorSyncExecutor>();
        executor.Setup(x => x.ConnectorId).Returns("test");
        executor.Setup(x => x.ExecuteSyncAsync(
                It.IsAny<IServiceProvider>(),
                It.IsAny<SyncRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ISyncProgressReporter?>()))
            .ThrowsAsync(new TaskCanceledException("HttpClient.Timeout"));
        var sut = CreateService(BuildProvider(executor.Object));

        var result = await sut.TriggerSyncAsync("test", new SyncRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("HttpClient.Timeout");
    }

    [Fact]
    public async Task TriggerSyncAsync_WithUnknownConnector_ReturnsFailure()
    {
        // Arrange - no executors registered
        var provider = BuildProvider();
        var sut = CreateService(provider);
        var request = new SyncRequest();

        // Act
        var result = await sut.TriggerSyncAsync("nonexistent", request, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Unknown connector");
    }

    [Fact]
    public async Task TriggerSyncAsync_MarksScopedAuditContextAsSystem()
    {
        // Regression test for the mutation_audit_log firehose: V4 repositories stamp their
        // factory-created DbContexts from the scoped IAuditContext, and the child scope this
        // service creates starts with a blank user context (IsSystem = false), so every record
        // a manually triggered sync imported was audited with null attribution instead of being
        // skipped as a system mutation.
        bool? capturedIsSystemMutation = null;
        var executor = CreateMockExecutor(
            "test",
            onSyncScope: sp =>
                capturedIsSystemMutation = sp.GetRequiredService<IAuditContext>().IsSystemMutation());
        var sut = CreateService(BuildProvider(executor));

        // Act
        await sut.TriggerSyncAsync("test", new SyncRequest(), CancellationToken.None);

        // Assert
        capturedIsSystemMutation.Should().BeTrue();
    }

    [Fact]
    public async Task TriggerSyncAsync_SystemAttributesTheScopeInjectedDbContext()
    {
        // Writes on the scope's directly-injected context are attributed by that context's own
        // AuditContext, not the ambient one, so it needs stamping too.
        IAuditContext? captured = null;
        var executor = CreateMockExecutor(
            "test",
            onSyncScope: sp => captured = sp.GetRequiredService<NocturneDbContext>().AuditContext);
        var sut = CreateService(BuildProvider(executor));

        // Act
        await sut.TriggerSyncAsync("test", new SyncRequest(), CancellationToken.None);

        // Assert
        captured.Should().NotBeNull();
        captured!.IsSystem.Should().BeTrue();
        captured.Endpoint.Should().Be("connector:test");
    }

    [Fact]
    public async Task TriggerSyncAsync_IsCaseInsensitive()
    {
        // Arrange - register with lowercase, trigger with uppercase
        var expectedResult = new SyncResult { Success = true, Message = "Dexcom sync complete" };
        var executor = CreateMockExecutor("dexcom", expectedResult);
        var provider = BuildProvider(executor);
        var sut = CreateService(provider);
        var request = new SyncRequest();

        // Act
        var result = await sut.TriggerSyncAsync("DEXCOM", request, CancellationToken.None);

        // Assert
        result.Should().BeSameAs(expectedResult);
        result.Success.Should().BeTrue();

        Mock.Get(executor).Verify(
            x => x.ExecuteSyncAsync(
                It.IsAny<IServiceProvider>(),
                request,
                CancellationToken.None,
                It.IsAny<ISyncProgressReporter?>()),
            Times.Once);
    }

    [Fact]
    public async Task TriggerSyncAsync_WhenTheSameConnectorIsAlreadyRunning_RefusesTheSecondRunAndRunsTheExecutorOnce()
    {
        var tenant = new TenantContext(Guid.NewGuid(), "test-tenant", "Test", true, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var executor = new Mock<IConnectorSyncExecutor>();
        executor.Setup(x => x.ConnectorId).Returns("test");
        executor.Setup(x => x.ExecuteSyncAsync(
                It.IsAny<IServiceProvider>(),
                It.IsAny<SyncRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ISyncProgressReporter?>()))
            .Returns(async (IServiceProvider _, SyncRequest _, CancellationToken _, ISyncProgressReporter? _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return new SyncResult { Success = true, Message = "OK" };
            });

        var sut = CreateService(BuildProvider(executor.Object), CreateTenantAccessor(tenant));

        var first = sut.TriggerSyncAsync("test", new SyncRequest(), CancellationToken.None);
        await entered.Task;

        var second = await sut.TriggerSyncAsync("test", new SyncRequest(), CancellationToken.None);

        second.Success.Should().BeFalse("the run was refused, not attempted");
        second.AlreadyRunning.Should().BeTrue();
        second.Message.Should().Contain("already running");

        release.SetResult();
        (await first).Success.Should().BeTrue();

        executor.Verify(
            x => x.ExecuteSyncAsync(
                It.IsAny<IServiceProvider>(),
                It.IsAny<SyncRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ISyncProgressReporter?>()),
            Times.Once,
            "the refused run must not reach the executor");
    }

    [Fact]
    public async Task TriggerSyncAsync_DifferentConnectors_RunConcurrently()
    {
        var tenant = new TenantContext(Guid.NewGuid(), "test-tenant", "Test", true, false);
        var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        static IConnectorSyncExecutor Blocking(string id, TaskCompletionSource entered, TaskCompletionSource release)
        {
            var mock = new Mock<IConnectorSyncExecutor>();
            mock.Setup(x => x.ConnectorId).Returns(id);
            mock.Setup(x => x.ExecuteSyncAsync(
                    It.IsAny<IServiceProvider>(),
                    It.IsAny<SyncRequest>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<ISyncProgressReporter?>()))
                .Returns(async (IServiceProvider _, SyncRequest _, CancellationToken _, ISyncProgressReporter? _) =>
                {
                    entered.TrySetResult();
                    await release.Task;
                    return new SyncResult { Success = true };
                });
            return mock.Object;
        }

        var sut = CreateService(
            BuildProvider(
                Blocking("dexcom", enteredA, release),
                Blocking("libre", enteredB, release)),
            CreateTenantAccessor(tenant));

        var first = sut.TriggerSyncAsync("dexcom", new SyncRequest(), CancellationToken.None);
        var second = sut.TriggerSyncAsync("libre", new SyncRequest(), CancellationToken.None);

        var bothEntered = Task.WhenAll(enteredA.Task, enteredB.Task);
        (await Task.WhenAny(bothEntered, Task.Delay(TimeSpan.FromSeconds(5))))
            .Should().Be(bothEntered, "unrelated connectors must not block each other");

        release.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task TriggerSyncAsync_WhenTheExecutorThrows_ReleasesTheKeyForALaterRun()
    {
        var tenant = new TenantContext(Guid.NewGuid(), "test-tenant", "Test", true, false);
        var calls = 0;

        var executor = new Mock<IConnectorSyncExecutor>();
        executor.Setup(x => x.ConnectorId).Returns("test");
        executor.Setup(x => x.ExecuteSyncAsync(
                It.IsAny<IServiceProvider>(),
                It.IsAny<SyncRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ISyncProgressReporter?>()))
            .ReturnsAsync(() => Interlocked.Increment(ref calls) == 1
                ? throw new InvalidOperationException("executor exploded")
                : new SyncResult { Success = true, Message = "OK" });

        var sut = CreateService(BuildProvider(executor.Object), CreateTenantAccessor(tenant));

        var failed = await sut.TriggerSyncAsync("test", new SyncRequest(), CancellationToken.None);
        failed.Success.Should().BeFalse();

        var succeeded = await sut.TriggerSyncAsync("test", new SyncRequest(), CancellationToken.None);

        succeeded.Success.Should().BeTrue("the key the failed run held must have been released");
        calls.Should().Be(2);
    }
}
