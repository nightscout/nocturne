using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.API.Tests.TestDoubles;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A Nightscout URL can carry a <c>?token=</c> credential, so what is stored for a source and
/// shown in its history is the URL without its query, user info or fragment, while sources
/// recorded under the raw URL before that keep matching.
/// </summary>
public class MigrationSourceIdentityTests
{
    private const string TokenUrl = "https://example-nightscout.invalid/nightscout/?token=synthetic-token";
    private const string CleanUrl = "https://example-nightscout.invalid/nightscout";

    private readonly TenantRunGuard _runGuard = new();

    private (MigrationJobService Service, ServiceProvider Provider) CreateService(
        List<Guid>? notifiedTenants = null,
        params Guid[] activeTenants)
    {
        var dbName = $"migration-sources-{Guid.NewGuid():N}";
        var provider = new ServiceCollection()
            .AddDbContextFactory<NocturneDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddDataServices()
            .AddScoped<ITenantAccessor, HttpContextTenantAccessor>()
            .AddActiveTenantSnapshot()
            .AddScoped<INotificationV1Service>(sp => RecordingNotifications(
                sp.GetRequiredService<ITenantAccessor>(), notifiedTenants ?? []))
            .BuildServiceProvider();

        if (activeTenants.Length > 0)
        {
            using var seedScope = provider.CreateScope();
            using var seed = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext();
            foreach (var id in activeTenants)
                seed.Tenants.Add(new TenantEntity { Id = id, Slug = $"t{id:N}", DisplayName = "Tenant", IsActive = true });
            seed.SaveChanges();
        }

        var service = new MigrationJobService(
            NullLogger<MigrationJobService>.Instance,
            provider,
            new ConfigurationBuilder().Build(),
            _runGuard);

        return (service, provider);
    }

    /// <summary>
    /// Starts a job and waits for its background run to release the tenant's lease, which it does
    /// only after writing its final record, so nothing rewrites the rows under the assertions.
    /// </summary>
    private async Task<MigrationJobInfo> StartAndSettleAsync(
        MigrationJobService service, TenantContext tenant, string url)
    {
        var job = await service.StartMigrationAsync(Request(url), tenant);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (_runGuard.TryGetHolder(tenant.TenantId, MigrationJobService.MigrationRunName, out _))
            await Task.Delay(10, timeout.Token);

        return job;
    }

    private static MigrationStartupService Startup(IServiceProvider provider) => new(
        provider,
        provider.GetRequiredService<ActiveTenantSnapshot>(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MIGRATION_MODE"] = "Api",
            ["MIGRATION_NS_URL"] = TokenUrl,
        }).Build(),
        NullLogger<MigrationStartupService>.Instance);

    private static TenantContext Tenant() =>
        new(Guid.NewGuid(), "migrated", "Test Tenant", true, IsDemo: false);

    private static StartMigrationRequest Request(string url) => new()
    {
        Mode = MigrationMode.Api,
        NightscoutUrl = url,
    };

    [Fact]
    public async Task A_url_token_is_neither_stored_nor_returned()
    {
        var (service, provider) = CreateService();
        await using var owned = provider;
        var tenant = Tenant();

        var job = await StartAndSettleAsync(service, tenant, TokenUrl);

        job.SourceDescription.Should().Be(CleanUrl);
        (await service.GetSourcesAsync(tenant.TenantId)).Should().ContainSingle()
            .Which.NightscoutUrl.Should().Be(CleanUrl);
        (await service.GetHistoryAsync(tenant.TenantId)).Should().ContainSingle()
            .Which.SourceDescription.Should().Be(CleanUrl);

        await using var scope = provider.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        var source = await db.MigrationSources.SingleAsync();
        source.SourceIdentifier.Should().Be(CleanUrl);
        source.NightscoutUrl.Should().Be(CleanUrl);
        (await db.MigrationRuns.SingleAsync()).SourceDescription.Should().Be(CleanUrl);
    }

    [Fact]
    public async Task A_source_stored_under_the_raw_url_is_taken_over_and_scrubbed()
    {
        var (service, provider) = CreateService();
        await using var owned = provider;
        var tenant = Tenant();
        var legacyId = Guid.CreateVersion7();

        await using (var seedScope = provider.CreateAsyncScope())
        {
            await using var seed = await seedScope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
            seed.MigrationSources.Add(new MigrationSourceEntity
            {
                Id = legacyId,
                TenantId = tenant.TenantId,
                Mode = "Api",
                SourceIdentifier = MigrationJob.LegacyApiSourceIdentifier(TokenUrl),
                NightscoutUrl = TokenUrl,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        (await service.GetSourcesAsync(tenant.TenantId)).Should().ContainSingle()
            .Which.NightscoutUrl.Should().Be(CleanUrl, "a row stored before the change is scrubbed on the way out");

        await StartAndSettleAsync(service, tenant, TokenUrl);

        await using var scope = provider.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        var source = await db.MigrationSources.SingleAsync();
        source.Id.Should().Be(legacyId);
        source.SourceIdentifier.Should().Be(CleanUrl);
        source.NightscoutUrl.Should().Be(CleanUrl);
        (await db.MigrationRuns.SingleAsync()).SourceId.Should().Be(legacyId);
    }

    [Fact]
    public void A_run_recorded_with_a_token_is_shown_without_it()
    {
        var run = new MigrationRunEntity
        {
            Mode = nameof(MigrationMode.Api),
            State = nameof(MigrationJobState.Completed),
            SourceDescription = TokenUrl,
        };

        MigrationJob.InfoFromRecord(run).SourceDescription.Should().Be(CleanUrl);
    }

    [Fact]
    public void A_mongo_run_description_is_left_as_recorded()
    {
        var run = new MigrationRunEntity
        {
            Mode = nameof(MigrationMode.MongoDb),
            State = nameof(MigrationJobState.Completed),
            SourceDescription = "MongoDB: nightscout?x",
        };

        MigrationJob.InfoFromRecord(run).SourceDescription.Should().Be("MongoDB: nightscout?x");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_startup_check_matches_a_completed_run_under_either_identifier(bool legacy)
    {
        var migrated = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var notified = new List<Guid>();
        var (_, provider) = CreateService(notified, migrated, pending);
        await using var owned = provider;

        await SeedCompletedRunAsync(provider, migrated, legacy ? MigrationJob.LegacyApiSourceIdentifier(TokenUrl) : CleanUrl);

        await Startup(provider).StartAsync(CancellationToken.None);

        notified.Should().Equal([pending], "only the tenant with no completed run is prompted to migrate");
    }

    [Fact]
    public async Task The_startup_check_notifies_when_no_run_completed_for_the_source()
    {
        var tenantId = Guid.NewGuid();
        var notified = new List<Guid>();
        var (_, provider) = CreateService(notified, tenantId);
        await using var owned = provider;

        await Startup(provider).StartAsync(CancellationToken.None);

        notified.Should().Equal([tenantId]);
    }

    [Fact]
    public async Task A_run_completed_for_another_source_does_not_clear_the_notice()
    {
        var tenantId = Guid.NewGuid();
        var notified = new List<Guid>();
        var (_, provider) = CreateService(notified, tenantId);
        await using var owned = provider;

        await SeedCompletedRunAsync(provider, tenantId, "https://other.invalid");

        await Startup(provider).StartAsync(CancellationToken.None);

        notified.Should().Equal([tenantId]);
    }

    private static async Task SeedCompletedRunAsync(IServiceProvider provider, Guid tenantId, string sourceIdentifier)
    {
        await using var seedScope = provider.CreateAsyncScope();
        await using var seed = await seedScope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenantId);
        var source = new MigrationSourceEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SourceIdentifier = sourceIdentifier,
            CreatedAt = DateTime.UtcNow,
        };
        seed.MigrationSources.Add(source);
        seed.MigrationRuns.Add(new MigrationRunEntity
        {
            Id = Guid.CreateVersion7(),
            SourceId = source.Id,
            TenantId = tenantId,
            State = nameof(MigrationJobState.Completed),
            CreatedAt = DateTime.UtcNow,
        });
        await seed.SaveChangesAsync();
    }

    private static INotificationV1Service RecordingNotifications(ITenantAccessor tenant, List<Guid> notified)
    {
        var notifications = new Mock<INotificationV1Service>();
        notifications
            .Setup(n => n.AddAdminNotificationAsync(It.IsAny<AdminNotification>(), It.IsAny<CancellationToken>()))
            .Callback(() => notified.Add(tenant.TenantId))
            .ReturnsAsync(new NotificationAckResponse());
        return notifications.Object;
    }

    [Fact]
    public void The_pending_config_offers_the_configured_url_without_its_token()
    {
        var service = new MigrationJobService(
            NullLogger<MigrationJobService>.Instance,
            new ServiceCollection().BuildServiceProvider(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MIGRATION_MODE"] = "Api",
                ["MIGRATION_NS_URL"] = "https://user:pass@example-nightscout.invalid/nightscout/?token=synthetic-token",
            }).Build(),
            new TenantRunGuard());

        var pending = service.GetPendingConfig();

        pending.HasPendingConfig.Should().BeTrue();
        pending.NightscoutUrl.Should().Be(CleanUrl);
    }
}
