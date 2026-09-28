using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// Tenant-isolation and durability behaviour for <see cref="MigrationJobService"/>. A migration
/// job is owned by the tenant that started it, and status/cancel lookups must be scoped to that
/// tenant so one tenant cannot read or cancel another tenant's job by guessing its id. Job records
/// are persisted, so lookups survive the in-memory job map being lost (e.g. an API restart).
/// </summary>
public class MigrationJobServiceTests
{
    /// <summary>
    /// Real DI provider with an InMemory NocturneDbContext so StartMigrationAsync can persist the
    /// job record. The background migration task itself still fails fast (no HTTP/tenant services
    /// are registered) — these tests exercise ownership, lookup, and record durability, not the
    /// data transfer.
    /// </summary>
    private static (MigrationJobService Service, IServiceProvider Provider) CreateService(
        IServiceProvider? existingProvider = null)
    {
        var dbName = $"migration-jobs-{Guid.NewGuid():N}";
        var provider = existingProvider ?? new ServiceCollection()
            .AddDbContext<NocturneDbContext>(o => o.UseInMemoryDatabase(dbName))
            .BuildServiceProvider();

        var service = new MigrationJobService(
            NullLogger<MigrationJobService>.Instance,
            provider,
            new ConfigurationBuilder().Build(),
            new TenantRunGuard());

        return (service, provider);
    }

    private static TenantContext Tenant(Guid id) => new(id, $"slug-{id:N}", "Test Tenant", true, IsDemo: false);

    private static StartMigrationRequest ApiRequest() => new()
    {
        Mode = MigrationMode.Api,
        NightscoutUrl = "https://example-nightscout.invalid",
    };

    [Fact]
    public async Task GetStatusAsync_returns_status_for_owning_tenant()
    {
        var (service, _) = CreateService();
        var tenant = Tenant(Guid.NewGuid());

        var job = await service.StartMigrationAsync(ApiRequest(), tenant);

        var status = await service.GetStatusAsync(tenant.TenantId, job.Id);

        status.JobId.Should().Be(job.Id);
    }

    [Fact]
    public async Task GetStatusAsync_throws_for_a_different_tenant()
    {
        var (service, _) = CreateService();
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
        var (service, _) = CreateService();
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
        var (service, _) = CreateService();
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
        var (service, _) = CreateService();

        // Refusing to start without a resolved tenant prevents the detached migration task from
        // falling back to a stale pooled DbContext tenant and importing into the wrong tenant.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartMigrationAsync(ApiRequest(), tenantContext: null));
    }

    [Fact]
    public async Task StartMigrationAsync_throws_when_tenant_id_is_empty()
    {
        var (service, _) = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartMigrationAsync(ApiRequest(), Tenant(Guid.Empty)));
    }

    [Fact]
    public async Task GetStatusAsync_serves_the_persisted_record_when_the_job_is_not_in_memory()
    {
        // Regression: job state lived only in a ConcurrentDictionary, so an API restart made a
        // running job's id answer 404 — indistinguishable from a job that never existed.
        var (service, provider) = CreateService();
        var tenant = Tenant(Guid.NewGuid());
        var job = await service.StartMigrationAsync(ApiRequest(), tenant);

        // A fresh service over the same store models the post-restart process: empty job map,
        // same database.
        var (restarted, _) = CreateService(provider);

        var status = await restarted.GetStatusAsync(tenant.TenantId, job.Id);
        status.JobId.Should().Be(job.Id);

        var history = await restarted.GetHistoryAsync(tenant.TenantId);
        history.Should().ContainSingle(h => h.Id == job.Id);
    }

    [Fact]
    public async Task GetSourcesAsync_only_returns_the_calling_tenants_sources()
    {
        // A source URL frequently identifies a person; one tenant must never see another
        // tenant's sources, even when both migrated from the same URL.
        var (service, _) = CreateService();
        var tenantA = Tenant(Guid.NewGuid());
        var tenantB = Tenant(Guid.NewGuid());

        await service.StartMigrationAsync(ApiRequest(), tenantA);
        await service.StartMigrationAsync(ApiRequest(), tenantB);

        var sourcesA = await service.GetSourcesAsync(tenantA.TenantId);

        sourcesA.Should().ContainSingle(s => s.NightscoutUrl == "https://example-nightscout.invalid");

        var sourcesC = await service.GetSourcesAsync(Guid.NewGuid());
        sourcesC.Should().BeEmpty();
    }

    /// <summary>
    /// A second start while the tenant's job is still live is refused, and the refusal names the
    /// running job; once that job reaches a terminal state the slot is free again.
    /// </summary>
    [Fact]
    public async Task StartMigrationAsync_WhenTheTenantAlreadyHasALiveJob_IsRefusedUntilItFinishes()
    {
        var handler = new BlockingHandler();
        var service = new MigrationJobService(
            NullLogger<MigrationJobService>.Instance,
            MigrationJobHarness.BuildProvider(handler),
            new ConfigurationBuilder().Build(),
            new TenantRunGuard());
        var tenant = Tenant(Guid.NewGuid());

        var first = await service.StartMigrationAsync(ApiRequest(), tenant);

        var refused = await Assert.ThrowsAsync<MigrationAlreadyRunningException>(
            () => service.StartMigrationAsync(ApiRequest(), tenant));

        refused.JobId.Should().Be(first.Id, "the conflict points at the job that holds the slot");

        handler.Release();
        await WaitUntilTerminalAsync(service, tenant.TenantId, first.Id);

        var afterFinish = await service.StartMigrationAsync(ApiRequest(), tenant);
        afterFinish.Id.Should().NotBe(first.Id, "the slot is free once the first job is terminal");
    }

    [Fact]
    public async Task StartMigrationAsync_ForDifferentTenants_RunsConcurrently()
    {
        var handler = new BlockingHandler();
        var service = new MigrationJobService(
            NullLogger<MigrationJobService>.Instance,
            MigrationJobHarness.BuildProvider(handler),
            new ConfigurationBuilder().Build(),
            new TenantRunGuard());

        var first = await service.StartMigrationAsync(ApiRequest(), Tenant(Guid.NewGuid()));
        var second = await service.StartMigrationAsync(ApiRequest(), Tenant(Guid.NewGuid()));

        second.Id.Should().NotBe(first.Id);
        handler.Release();
    }

    private static async Task WaitUntilTerminalAsync(
        MigrationJobService service, Guid tenantId, Guid jobId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var status = await service.GetStatusAsync(tenantId, jobId);
            if (status.State is MigrationJobState.Completed or MigrationJobState.Failed
                or MigrationJobState.Cancelled or MigrationJobState.Interrupted)
                return;

            await Task.Delay(20);
        }

        throw new TimeoutException($"Migration job {jobId} did not reach a terminal state");
    }

    /// <summary>Holds every source request until released, keeping the job in a live state.</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _released.Task.WaitAsync(cancellationToken);
            throw new HttpRequestException("released");
        }
    }
}
