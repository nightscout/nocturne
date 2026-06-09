using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// Tenant-isolation behaviour for <see cref="MigrationJobService"/>. A migration job is owned by
/// the tenant that started it, and status/cancel lookups must be scoped to that tenant so one
/// tenant cannot read or cancel another tenant's job by guessing its id.
/// </summary>
public class MigrationJobServiceTests
{
    private static MigrationJobService CreateService()
    {
        // The background migration task will fail fast when it tries to create a DI scope from this
        // empty provider — that's fine and intentional: these tests only exercise job ownership and
        // lookup, which happen synchronously in StartMigrationAsync before any background work runs.
        var serviceProvider = new Mock<IServiceProvider>().Object;
        return new MigrationJobService(
            NullLogger<MigrationJobService>.Instance,
            serviceProvider,
            new ConfigurationBuilder().Build());
    }

    private static TenantContext Tenant(Guid id) => new(id, $"slug-{id:N}", "Test Tenant", true);

    private static StartMigrationRequest ApiRequest() => new()
    {
        Mode = MigrationMode.Api,
        NightscoutUrl = "https://example-nightscout.invalid",
    };

    [Fact]
    public async Task GetStatusAsync_returns_status_for_owning_tenant()
    {
        var service = CreateService();
        var tenant = Tenant(Guid.NewGuid());

        var job = await service.StartMigrationAsync(ApiRequest(), tenant);

        var status = await service.GetStatusAsync(tenant.TenantId, job.Id);

        status.JobId.Should().Be(job.Id);
    }

    [Fact]
    public async Task GetStatusAsync_throws_for_a_different_tenant()
    {
        var service = CreateService();
        var owner = Tenant(Guid.NewGuid());
        var other = Tenant(Guid.NewGuid());

        var job = await service.StartMigrationAsync(ApiRequest(), owner);

        // A different tenant must not be able to read the job, even with the correct id.
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetStatusAsync(other.TenantId, job.Id));
    }

    [Fact]
    public async Task CancelAsync_throws_for_a_different_tenant()
    {
        var service = CreateService();
        var owner = Tenant(Guid.NewGuid());
        var other = Tenant(Guid.NewGuid());

        var job = await service.StartMigrationAsync(ApiRequest(), owner);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.CancelAsync(other.TenantId, job.Id));

        // The owning tenant can still cancel it.
        await service.CancelAsync(owner.TenantId, job.Id);
    }

    [Fact]
    public async Task GetHistoryAsync_only_returns_the_calling_tenants_jobs()
    {
        var service = CreateService();
        var tenantA = Tenant(Guid.NewGuid());
        var tenantB = Tenant(Guid.NewGuid());

        var jobA = await service.StartMigrationAsync(ApiRequest(), tenantA);
        await service.StartMigrationAsync(ApiRequest(), tenantB);

        var historyA = await service.GetHistoryAsync(tenantA.TenantId);

        historyA.Should().ContainSingle(h => h.Id == jobA.Id);
        historyA.Should().OnlyContain(h => h.Id == jobA.Id);
    }

    [Fact]
    public async Task StartMigrationAsync_throws_when_tenant_context_is_null()
    {
        var service = CreateService();

        // Refusing to start without a resolved tenant prevents the detached migration task from
        // falling back to a stale pooled DbContext tenant and importing into the wrong tenant.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartMigrationAsync(ApiRequest(), tenantContext: null));
    }

    [Fact]
    public async Task StartMigrationAsync_throws_when_tenant_id_is_empty()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartMigrationAsync(ApiRequest(), Tenant(Guid.Empty)));
    }
}
