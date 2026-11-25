using Nocturne.Tests.Shared.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// Tenant-isolation and durability behaviour for <see cref="MigrationJobService"/>. A migration
/// job is owned by the tenant that started it, and status/cancel lookups must be scoped to that
/// tenant so one tenant cannot read or cancel another tenant's job by guessing its id. A job is its
/// stored record, so every lookup is answered by a process that never ran it.
/// </summary>
public class MigrationJobServiceTests
{
    /// <summary>
    /// Real DI provider with an InMemory NocturneDbContext so StartMigrationAsync can persist the
    /// job record. Nothing runs the job, which stays pending: these tests exercise ownership,
    /// lookup, and record durability, not the data transfer.
    /// </summary>
    private static (MigrationJobService Service, IServiceProvider Provider) CreateService(
        IServiceProvider? existingProvider = null)
    {
        var dbName = $"migration-jobs-{Guid.NewGuid():N}";
        var provider = existingProvider ?? new ServiceCollection()
            .AddDbContextFactory<NocturneDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddDataServices()
            .AddSingleton(AccountLinkRoutes.Off)
            .BuildServiceProvider();

        return (MigrationJobHarness.CreateService(provider), provider);
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

        var job = await service.StartMigrationAsync(ApiRequest(), tenant, starter: null);

        var status = await service.GetStatusAsync(tenant.TenantId, job.Id);

        status.JobId.Should().Be(job.Id);
    }

    [Fact]
    public async Task GetStatusAsync_throws_for_a_different_tenant()
    {
        var (service, _) = CreateService();
        var owner = Tenant(Guid.NewGuid());
        var other = Tenant(Guid.NewGuid());

        var job = await service.StartMigrationAsync(ApiRequest(), owner, starter: null);

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

        var job = await service.StartMigrationAsync(ApiRequest(), owner, starter: null);

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

        var jobA = await service.StartMigrationAsync(ApiRequest(), tenantA, starter: null);
        await service.StartMigrationAsync(ApiRequest(), tenantB, starter: null);

        var historyA = await service.GetHistoryAsync(tenantA.TenantId);

        historyA.Should().ContainSingle(h => h.Id == jobA.Id);
        historyA.Should().OnlyContain(h => h.Id == jobA.Id);
    }

    [Fact]
    public async Task StartMigrationAsync_throws_when_tenant_context_is_null()
    {
        var (service, _) = CreateService();

        // The detached migration task writes under whatever tenant it is given, so it refuses to
        // start without one.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartMigrationAsync(ApiRequest(), tenantContext: null, starter: null));
    }

    [Fact]
    public async Task StartMigrationAsync_throws_when_tenant_id_is_empty()
    {
        var (service, _) = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartMigrationAsync(ApiRequest(), Tenant(Guid.Empty), starter: null));
    }

    [Fact]
    public async Task GetStatusAsync_serves_the_persisted_record_when_the_job_is_not_in_memory()
    {
        var (service, provider) = CreateService();
        var tenant = Tenant(Guid.NewGuid());
        var job = await service.StartMigrationAsync(ApiRequest(), tenant, starter: null);

        // A fresh service over the same store models the post-restart process.
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

        await service.StartMigrationAsync(ApiRequest(), tenantA, starter: null);
        await service.StartMigrationAsync(ApiRequest(), tenantB, starter: null);

        var sourcesA = await service.GetSourcesAsync(tenantA.TenantId);

        sourcesA.Should().ContainSingle(s => s.NightscoutUrl == "example-nightscout.invalid");

        var sourcesC = await service.GetSourcesAsync(Guid.NewGuid());
        sourcesC.Should().BeEmpty();
    }

    /// <summary>
    /// A second start while the tenant's job is still live is refused, and the refusal names the
    /// running job.
    /// </summary>
    [Fact]
    public async Task StartMigrationAsync_WhenTheTenantAlreadyHasALiveJob_IsRefused()
    {
        var (service, _) = CreateService();
        var tenant = Tenant(Guid.NewGuid());

        var first = await service.StartMigrationAsync(ApiRequest(), tenant, starter: null);

        var refused = await Assert.ThrowsAsync<MigrationAlreadyRunningException>(
            () => service.StartMigrationAsync(ApiRequest(), tenant, starter: null));

        refused.JobId.Should().Be(first.Id, "the conflict points at the job that holds the slot");
    }

    [Fact]
    public async Task StartMigrationAsync_AfterTheTenantsJobEnded_IsAccepted()
    {
        var (service, _) = CreateService();
        var tenant = Tenant(Guid.NewGuid());
        var first = await service.StartMigrationAsync(ApiRequest(), tenant, starter: null);
        await service.CancelAsync(tenant.TenantId, first.Id);

        var second = await service.StartMigrationAsync(ApiRequest(), tenant, starter: null);

        second.Id.Should().NotBe(first.Id);
        (await service.GetStatusAsync(tenant.TenantId, first.Id)).State.Should().Be(MigrationJobState.Cancelled);
        (await service.GetStatusAsync(tenant.TenantId, second.Id)).State.Should().Be(MigrationJobState.Pending);
    }

    [Fact]
    public async Task StartMigrationAsync_ForDifferentTenants_IsAcceptedForEach()
    {
        var (service, _) = CreateService();

        var first = await service.StartMigrationAsync(ApiRequest(), Tenant(Guid.NewGuid()), starter: null);
        var second = await service.StartMigrationAsync(ApiRequest(), Tenant(Guid.NewGuid()), starter: null);

        second.Id.Should().NotBe(first.Id);
    }

    private static MigrationJob CreateJob()
    {
        var (_, provider) = CreateService();
        var id = Guid.NewGuid();
        return new MigrationJob(
            id,
            Guid.NewGuid(),
            ApiRequest(),
            new MigrationJobInfo { Id = id, Mode = MigrationMode.Api, CreatedAt = DateTime.UtcNow },
            tenantContext: null,
            NullLogger.Instance,
            provider,
            TimeProvider.System);
    }

    [Fact]
    public async Task A_count_that_outlasts_its_budget_is_given_up_on_when_the_clock_passes_it()
    {
        var (_, provider) = CreateService();
        var clock = new FakeTimeProvider();
        var id = Guid.NewGuid();
        var job = new MigrationJob(
            id,
            Guid.NewGuid(),
            ApiRequest(),
            new MigrationJobInfo { Id = id, Mode = MigrationMode.Api, CreatedAt = DateTime.UtcNow },
            tenantContext: null,
            NullLogger.Instance,
            provider,
            clock);
        var counting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new StallingHandler(counting)) { BaseAddress = new Uri("https://example-nightscout.invalid/") };

        var count = job.FetchCollectionCountAsync(http, "entries", CancellationToken.None);
        await counting.Task.WaitAsync(Eventually.DefaultPatience);
        count.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(30));

        (await count.WaitAsync(Eventually.DefaultPatience)).Should().BeNull("the run goes on without a total");
    }

    [Fact]
    public void UpdateCollectionProgress_keeps_a_recorded_failure_reason()
    {
        var job = CreateJob();
        job.UpdateCollectionProgress("entries", 10, 4, 0, false);
        job.RecordCollectionFailure("entries", "the source stopped answering");

        job.UpdateCollectionProgress("entries", 10, 5, 1, true);

        var progress = job.GetStatus().CollectionProgress["entries"];
        progress.FailureReason.Should().Be("the source stopped answering");
        progress.DocumentsMigrated.Should().Be(5);
        progress.DocumentsFailed.Should().Be(1);
    }

    [Fact]
    public void UpdateCollectionProgress_keeps_a_recorded_skip_reason()
    {
        var job = CreateJob();
        job.RecordCollectionSkipped("profile", "no profile endpoint");

        job.UpdateCollectionProgress("profile", 3, 3, 0, true);

        job.GetStatus().CollectionProgress["profile"].SkippedReason.Should().Be("no profile endpoint");
    }

    /// <summary>Never answers, and says when a request has arrived.</summary>
    private sealed class StallingHandler(TaskCompletionSource arrived) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            arrived.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
