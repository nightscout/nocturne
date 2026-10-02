using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Health.GoogleHealth;

public class GoogleHealthWorkerTests
{
    [Fact]
    public async Task Imports_are_concurrent_bounded_and_keep_claims_until_completion_or_shutdown()
    {
        var coordinator = new GoogleHealthCoordinator();
        var tenants = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var releases = tenants.ToDictionary(id => id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var started = Channel.CreateUnbounded<Guid>();
        var tenantService = new Mock<ITenantService>();
        tenantService.Setup(service => service.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>((id, _) => Task.FromResult<TenantDetailDto?>(
                new(id, id.ToString(), "tenant", true, DateTime.UtcNow, [])));
        var services = new ServiceCollection();
        services.AddSingleton(tenantService.Object);
        services.AddSingleton(Mock.Of<IConnectorConfigurationService>());
        services.AddScoped<ITenantAccessor, TenantAccessor>();
        services.AddScoped<IConnectorSyncService>(provider =>
        {
            var accessor = provider.GetRequiredService<ITenantAccessor>();
            var sync = new Mock<IConnectorSyncService>();
            sync.Setup(service => service.TriggerSyncAsync("googlehealth", It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()))
                .Returns<string, SyncRequest, CancellationToken>(async (_, _, ct) =>
                {
                    var id = accessor.TenantId;
                    started.Writer.TryWrite(id);
                    await releases[id].Task.WaitAsync(ct);
                    return new SyncResult { Success = true };
                });
            return sync.Object;
        });
        using var provider = services.BuildServiceProvider();
        using var worker = new GoogleHealthWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            coordinator, NullLogger<GoogleHealthWorker>.Instance);
        foreach (var tenant in tenants) Assert.True(await coordinator.QueueAsync(tenant, 1, default));
        await worker.StartAsync(default);
        try
        {
            var active = new List<Guid>();
            for (var index = 0; index < 4; index++)
                active.Add(await started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(4, active.Distinct().Count());
            using var noSlot = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => started.Reader.ReadAsync(noSlot.Token).AsTask());
            foreach (var id in active)
                Assert.Null(await coordinator.ClaimWorkerAsync(id, default));

            releases[active[0]].SetResult();
            var next = await started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(next, active);
            Assert.Null(await coordinator.ProgressAsync(active[0], default));
            active.RemoveAt(0);
            active.Add(next);
            await worker.StopAsync(default);

            foreach (var id in active)
            {
                Assert.True((await coordinator.ProgressAsync(id, default))!.WorkerOwned);
                await using var recovered = await coordinator.ClaimWorkerAsync(id, default);
                Assert.NotNull(recovered);
            }
        }
        finally { await worker.StopAsync(default); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduled_run_conflict_keeps_manual_request_until_retry_or_shutdown(bool shutdown)
    {
        var tenant = Guid.NewGuid();
        var coordinator = new GoogleHealthCoordinator();
        var conflicted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthUpdated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tenants = new Mock<ITenantService>();
        tenants.Setup(service => service.GetByIdAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantDetailDto(tenant, "tenant", "Tenant", true, DateTime.UtcNow, []));
        var configurations = new Mock<IConnectorConfigurationService>();
        configurations.Setup(service => service.UpdateHealthStateAsync(
                "GoogleHealth", It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Callback(() => healthUpdated.TrySetResult()).Returns(Task.CompletedTask);
        var calls = 0;
        var sync = new Mock<IConnectorSyncService>();
        sync.Setup(service => service.TriggerSyncAsync("googlehealth", It.IsAny<SyncRequest>(), It.IsAny<CancellationToken>()))
            .Returns<string, SyncRequest, CancellationToken>(async (_, _, ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    conflicted.TrySetResult();
                    return new SyncResult { AlreadyRunning = true };
                }
                retried.TrySetResult();
                await finish.Task.WaitAsync(ct);
                return new SyncResult { Success = true };
            });
        var services = new ServiceCollection();
        services.AddSingleton(tenants.Object);
        services.AddSingleton(configurations.Object);
        services.AddSingleton(sync.Object);
        services.AddScoped<ITenantAccessor, TenantAccessor>();
        using var provider = services.BuildServiceProvider();
        using var worker = new GoogleHealthWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            coordinator, NullLogger<GoogleHealthWorker>.Instance);
        Assert.True(await coordinator.QueueAsync(tenant, 1, default));
        var scheduled = await coordinator.AcquireAsync(tenant, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(default);
        try
        {
            await conflicted.Task.WaitAsync(timeout.Token);
            Assert.False(retried.Task.IsCompleted);
            Assert.False(healthUpdated.Task.IsCompleted);
            Assert.True((await coordinator.ProgressAsync(tenant, default))!.WorkerOwned);
            if (shutdown)
            {
                await worker.StopAsync(default);
                Assert.True((await coordinator.ProgressAsync(tenant, default))!.WorkerOwned);
                await using var recovered = await coordinator.ClaimWorkerAsync(tenant, default);
                Assert.NotNull(recovered);
                return;
            }
            await scheduled!.DisposeAsync();
            scheduled = null;
            await retried.Task.WaitAsync(timeout.Token);
            Assert.True((await coordinator.ProgressAsync(tenant, default))!.WorkerOwned);
            Assert.Null(await coordinator.ClaimWorkerAsync(tenant, default));
            finish.TrySetResult();
            await healthUpdated.Task.WaitAsync(timeout.Token);
            while (await coordinator.ProgressAsync(tenant, timeout.Token) is not null)
                await Task.Delay(10, timeout.Token);
            Assert.Equal(2, calls);
        }
        finally
        {
            if (scheduled is not null) await scheduled.DisposeAsync();
            await worker.StopAsync(default);
        }
    }

    private sealed class TenantAccessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public void SetTenant(TenantContext context) => Context = context;
    }
}
