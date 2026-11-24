using Nocturne.Tests.Shared.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Migration;
using Nocturne.Connectors.Core.Utilities;
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
/// A Nightscout URL can carry a credential in its query (<c>?token=</c>) or as a path prefix a
/// proxy admits requests under, so a source is stored under a digest of the site and its URL, like
/// a run's description, is stored and returned masked.
/// </summary>
public class MigrationSourceIdentityTests
{
    private const string PathSecret = "AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI";
    private const string SiteBase = $"https://example-nightscout.invalid/{PathSecret}";
    private const string TokenUrl = $"{SiteBase}/?token=synthetic-token";
    private const string MaskedUrl = "example-nightscout.invalid/AGYL…";

    private (MigrationJobService Service, ServiceProvider Provider) CreateService(
        List<Guid>? notifiedTenants = null,
        params Guid[] activeTenants)
    {
        var dbName = $"migration-sources-{Guid.NewGuid():N}";
        var provider = new ServiceCollection()
            .AddDbContextFactory<NocturneDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddDataServices()
            .AddSingleton(AccountLinkRoutes.Off)
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

        return (MigrationJobHarness.CreateService(provider), provider);
    }

    /// <summary>Starts a job and runs it to its end.</summary>
    private static async Task<MigrationJobInfo> StartAndSettleAsync(
        MigrationJobService service, TenantContext tenant, string url)
    {
        var job = await service.StartMigrationAsync(Request(url), tenant);
        await MigrationJobHarness.RunToEndAsync(service, tenant);
        return job;
    }

    private static MigrationStartupService Startup(
        IServiceProvider provider, ILogger<MigrationStartupService>? logger = null) => new(
        provider,
        provider.GetRequiredService<ActiveTenantSnapshot>(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MIGRATION_MODE"] = "Api",
            ["MIGRATION_NS_URL"] = TokenUrl,
        }).Build(),
        logger ?? NullLogger<MigrationStartupService>.Instance);

    private static TenantContext Tenant() =>
        new(Guid.NewGuid(), "migrated", "Test Tenant", true, IsDemo: false);

    private static StartMigrationRequest Request(string url) => new()
    {
        Mode = MigrationMode.Api,
        NightscoutUrl = url,
    };

    [Fact]
    public async Task Neither_a_token_nor_a_path_secret_is_stored_or_returned()
    {
        var (service, provider) = CreateService();
        await using var owned = provider;
        var tenant = Tenant();

        var job = await StartAndSettleAsync(service, tenant, TokenUrl);

        job.SourceDescription.Should().Be(MaskedUrl);
        (await service.GetSourcesAsync(tenant.TenantId)).Should().ContainSingle()
            .Which.NightscoutUrl.Should().Be(MaskedUrl);
        (await service.GetHistoryAsync(tenant.TenantId)).Should().ContainSingle()
            .Which.SourceDescription.Should().Be(MaskedUrl);

        await using var scope = provider.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        var source = await db.MigrationSources.SingleAsync();
        source.SourceIdentifier.Should().Be(HashUtils.Sha256Hex(SiteBase));
        source.NightscoutUrl.Should().Be(MaskedUrl);
        (await db.MigrationRuns.SingleAsync()).SourceDescription.Should().Be(MaskedUrl);
    }

    [Fact]
    public async Task A_later_import_of_the_same_site_with_another_token_attaches_to_its_source()
    {
        var (service, provider) = CreateService();
        await using var owned = provider;
        var tenant = Tenant();

        await StartAndSettleAsync(service, tenant, TokenUrl);
        await StartAndSettleAsync(service, tenant, $"https://user:pass@EXAMPLE-nightscout.invalid:443/{PathSecret}?token=other");

        await using var scope = provider.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        var source = await db.MigrationSources.SingleAsync();
        (await db.MigrationRuns.Select(r => r.SourceId).ToListAsync()).Should().Equal(source.Id, source.Id);
    }

    [Fact]
    public async Task A_connection_test_names_the_site_masked()
    {
        await using var provider = MigrationJobHarness.BuildProvider(new StatusOnly());
        var service = MigrationJobHarness.CreateService(provider);

        var result = await service.TestConnectionAsync(new TestMigrationConnectionRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = TokenUrl,
        });

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.SiteName.Should().Be(MaskedUrl);
    }

    private sealed class StatusOnly : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task The_startup_check_matches_a_completed_run_of_the_source_an_import_stored()
    {
        var migrated = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var notified = new List<Guid>();
        var (service, provider) = CreateService(notified, migrated, pending);
        await using var owned = provider;
        await StartAndSettleAsync(service, new TenantContext(migrated, "migrated", "Test Tenant", true, IsDemo: false), TokenUrl);
        await CompleteRunsAsync(provider, migrated);
        var logger = new ListLogger<MigrationStartupService>();

        await Startup(provider, logger).StartAsync(CancellationToken.None);

        notified.Should().Equal([pending], "only the tenant with no completed run is prompted to migrate");
        logger.Entries.Should().NotBeEmpty().And.OnlyContain(e => !e.Message.Contains(PathSecret) && !e.Message.Contains("token"));
        logger.Entries.Should().Contain(e => e.Message.Contains(MaskedUrl));
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

    private static async Task CompleteRunsAsync(IServiceProvider provider, Guid tenantId)
    {
        await using var scope = provider.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenantId);
        foreach (var run in await db.MigrationRuns.Where(r => r.TenantId == tenantId).ToListAsync())
            run.State = nameof(MigrationJobState.Completed);
        await db.SaveChangesAsync();
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
        var service = MigrationJobHarness.CreateService(
            new ServiceCollection().BuildServiceProvider(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MIGRATION_MODE"] = "Api",
                ["MIGRATION_NS_URL"] = $"https://user:pass@example-nightscout.invalid/{PathSecret}/?token=synthetic-token",
            }).Build());

        var pending = service.GetPendingConfig();

        pending.HasPendingConfig.Should().BeTrue();
        pending.NightscoutUrl.Should().Be(SiteBase, "the form it fills needs the site's path");
    }
}
