using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Health.GoogleHealth;

/// <summary>A coordinator whose work in flight lives in memory, one entry per tenant.</summary>
internal static class InMemoryGoogleHealthCoordinator
{
    public static GoogleHealthCoordinator Create(TimeProvider? clock = null, ITenantService? tenants = null) =>
        Build(clock, tenants, null);

    /// <summary>As <see cref="Create(TimeProvider?, ITenantService?)"/>, and <paramref name="progressCleared"/> completes once progress is written as none.</summary>
    public static GoogleHealthCoordinator Create(
        out Task progressCleared, TimeProvider? clock = null, ITenantService? tenants = null)
    {
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        progressCleared = cleared.Task;
        return Build(clock, tenants, progress => { if (progress is null) cleared.TrySetResult(); });
    }

    private static GoogleHealthCoordinator Build(
        TimeProvider? clock, ITenantService? tenants, Action<GoogleHealthSyncProgress?>? progressWritten)
    {
        var stored = new ConcurrentDictionary<Guid, GoogleHealthWork>();
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, SettableTenantAccessor>();
        services.AddScoped<IGoogleHealthWorkStore>(provider =>
            new Work(provider.GetRequiredService<ITenantAccessor>(), stored, progressWritten));
        services.AddSingleton(tenants ?? Mock.Of<ITenantService>(service =>
            service.GetAllAsync(It.IsAny<CancellationToken>()) == Task.FromResult(new List<TenantDto>())));
        return new(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), clock ?? TimeProvider.System);
    }

    private sealed class Work(
        ITenantAccessor tenant, ConcurrentDictionary<Guid, GoogleHealthWork> stored,
        Action<GoogleHealthSyncProgress?>? progressWritten) : IGoogleHealthWorkStore
    {
        public Task<GoogleHealthWork> GetAsync(CancellationToken ct) =>
            Task.FromResult(stored.GetValueOrDefault(tenant.TenantId) ?? GoogleHealthWork.None);

        public Task SetProgressAsync(GoogleHealthSyncProgress? progress, CancellationToken ct)
        {
            stored.AddOrUpdate(tenant.TenantId,
                _ => GoogleHealthWork.None with { Progress = progress },
                (_, work) => work with { Progress = progress });
            progressWritten?.Invoke(progress);
            return Task.CompletedTask;
        }

        public Task SetDisconnectAsync(GoogleHealthDisconnectRequest? request, CancellationToken ct)
        {
            stored.AddOrUpdate(tenant.TenantId,
                _ => GoogleHealthWork.None with { Disconnect = request },
                (_, work) => work with { Disconnect = request });
            return Task.CompletedTask;
        }
    }
}
