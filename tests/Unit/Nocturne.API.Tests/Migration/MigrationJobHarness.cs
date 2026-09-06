using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// Runs a real <see cref="MigrationJob"/> in API mode against a stubbed Nightscout, with every
/// decomposer mocked out — what the source answers is the input under test, not what the
/// decomposers do with it.
/// </summary>
internal static class MigrationJobHarness
{
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedTenantAccessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }

        public bool IsResolved => Context is not null;

        public Guid TenantId => Context?.TenantId ?? Guid.Empty;

        public void SetTenant(TenantContext? tenant) => Context = tenant;
    }

    public static ServiceProvider BuildProvider(HttpMessageHandler handler)
    {
        var database = $"migration-{Guid.NewGuid():N}";

        var entries = new Mock<IEntryDecomposer>();
        entries
            .Setup(d => d.DecomposeBatchAsync(It.IsAny<IReadOnlyList<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());

        var treatments = new Mock<ITreatmentDecomposer>();
        treatments
            .Setup(d => d.DecomposeBatchAsync(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());

        var deviceStatuses = new Mock<IDeviceStatusDecomposer>();
        deviceStatuses
            .Setup(d => d.DecomposeBatchAsync(It.IsAny<IReadOnlyList<DeviceStatus>>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());

        var activities = new Mock<IActivityDecomposer>();
        activities
            .Setup(d => d.DecomposeBatchAsync(It.IsAny<IReadOnlyList<Activity>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());

        return new ServiceCollection()
            .AddDbContext<NocturneDbContext>(o => o.UseInMemoryDatabase(database))
            .AddScoped<ITenantAccessor, FixedTenantAccessor>()
            .AddScoped<IAuditContext, AuditContext>()
            .AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler))
            .AddSingleton(entries.Object)
            .AddSingleton(treatments.Object)
            .AddSingleton(deviceStatuses.Object)
            .AddSingleton(activities.Object)
            .BuildServiceProvider();
    }

    public static Task<MigrationJobStatus> RunAsync(
        IServiceProvider provider, params string[] collections) =>
        RunAsync(provider, onCreated: null, collections);

    /// <summary>
    /// <paramref name="onCreated"/> receives the job before it starts, so a test's stub source can
    /// cancel it mid-fetch the way the user's Cancel button does.
    /// </summary>
    public static async Task<MigrationJobStatus> RunAsync(
        IServiceProvider provider, Action<MigrationJob>? onCreated, string[] collections)
    {
        var tenant = new TenantContext(
            Guid.CreateVersion7(), "migrated", "Migrated Tenant", true, IsDemo: false);

        var job = new MigrationJob(
            Guid.CreateVersion7(),
            tenant.TenantId,
            new StartMigrationRequest
            {
                Mode = MigrationMode.Api,
                NightscoutUrl = "https://example-nightscout.invalid",
                Collections = [.. collections],
            },
            new MigrationJobInfo
            {
                Id = Guid.CreateVersion7(),
                Mode = MigrationMode.Api,
                CreatedAt = DateTime.UtcNow,
            },
            tenant,
            NullLogger.Instance,
            provider);

        onCreated?.Invoke(job);

        await job.ExecuteAsync(CancellationToken.None);

        return job.GetStatus();
    }
}
