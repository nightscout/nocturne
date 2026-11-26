using Nocturne.Tests.Shared.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.TenantAdmin;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Migration;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.API.Tests.TestDoubles;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A Nightscout URL can carry a credential in its query (<c>?token=</c>) or as a path prefix a
/// proxy admits requests under, so a source is stored under a digest of the site and its URL, like
/// a run's description, is stored and returned as its host with no path.
/// </summary>
public class MigrationSourceIdentityTests
{
    private const string PathSecret = "AGYLdvgzOeq24CB8XwYVaZDkXyxFxPGI";
    private const string SiteBase = $"https://example-nightscout.invalid/{PathSecret}";
    private const string TokenUrl = $"{SiteBase}/?token=synthetic-token";
    private const string ShownUrl = "example-nightscout.invalid/…";

    private static readonly IConfiguration Configured = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MIGRATION_MODE"] = "Api",
            ["MIGRATION_NS_URL"] = TokenUrl,
        })
        .Build();

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

        SeedTenants(provider, activeTenants);
        return (MigrationJobHarness.CreateService(provider, Configured), provider);
    }

    private static void SeedTenants(IServiceProvider provider, params Guid[] tenantIds)
    {
        if (tenantIds.Length == 0)
            return;

        using var seedScope = provider.CreateScope();
        using var seed = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext();
        foreach (var id in tenantIds)
            seed.Tenants.Add(new TenantEntity { Id = id, Slug = $"t{id:N}", DisplayName = "Tenant", IsActive = true });
        seed.SaveChanges();
    }

    /// <summary>Starts a job and runs it to its end.</summary>
    private static async Task<MigrationJobInfo> StartAndSettleAsync(
        MigrationJobService service, TenantContext tenant, string url)
    {
        var job = await service.StartMigrationAsync(Request(url), tenant, starter: null);
        await MigrationJobHarness.RunToEndAsync(service, tenant);
        return job;
    }

    private static MigrationStartupService Startup(
        IServiceProvider provider, ILogger<MigrationStartupService>? logger = null) => new(
        provider,
        provider.GetRequiredService<ActiveTenantSnapshot>(),
        Configured,
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

        job.SourceDescription.Should().Be(ShownUrl);
        (await service.GetSourcesAsync(tenant.TenantId)).Should().ContainSingle()
            .Which.NightscoutUrl.Should().Be(ShownUrl);
        (await service.GetHistoryAsync(tenant.TenantId)).Should().ContainSingle()
            .Which.SourceDescription.Should().Be(ShownUrl);

        await using var scope = provider.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        var source = await db.MigrationSources.SingleAsync();
        source.SourceIdentifier.Should().Be(HashUtils.Sha256Hex(SiteBase));
        source.NightscoutUrl.Should().Be(ShownUrl);
        (await db.MigrationRuns.SingleAsync()).SourceDescription.Should().Be(ShownUrl);
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
    public async Task Two_paths_on_one_host_are_two_sources_and_neither_path_is_stored_or_returned()
    {
        var (service, provider) = CreateService();
        await using var owned = provider;
        var tenant = Tenant();

        await StartAndSettleAsync(service, tenant, "https://example-nightscout.invalid/s3cr3t");
        await StartAndSettleAsync(service, tenant, "https://example-nightscout.invalid/0th3r");

        var sources = await service.GetSourcesAsync(tenant.TenantId);
        sources.Should().HaveCount(2).And.OnlyContain(s => s.NightscoutUrl == ShownUrl);
        (await service.GetHistoryAsync(tenant.TenantId)).Should().HaveCount(2)
            .And.OnlyContain(r => r.SourceDescription == ShownUrl);
    }

    [Fact]
    public async Task A_connection_test_names_the_site_by_its_host()
    {
        await using var provider = MigrationJobHarness.BuildProvider(new StatusOnly());
        var service = MigrationJobHarness.CreateService(provider);

        var result = await service.TestConnectionAsync(new TestMigrationConnectionRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = TokenUrl,
        });

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.SiteName.Should().Be(ShownUrl);
    }

    private sealed class StatusOnly : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task The_startup_check_matches_a_completed_run_of_the_source_an_import_stored()
    {
        var migrated = Guid.NewGuid();
        var notified = new List<Guid>();
        var (service, provider) = CreateService(notified, migrated);
        await using var owned = provider;
        await StartAndSettleAsync(service, new TenantContext(migrated, "migrated", "Test Tenant", true, IsDemo: false), TokenUrl);
        await CompleteRunsAsync(provider, migrated);
        var logger = new ListLogger<MigrationStartupService>();

        await Startup(provider, logger).StartAsync(CancellationToken.None);

        notified.Should().BeEmpty("the tenant completed a run of the configured source");
        logger.Entries.Should().NotBeEmpty().And.OnlyContain(e => !e.Message.Contains(PathSecret) && !e.Message.Contains("token"));
        logger.Entries.Should().Contain(e => e.Message.Contains(ShownUrl));
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
    public async Task The_startup_check_notifies_no_tenant_of_an_instance_with_more_than_one()
    {
        var notified = new List<Guid>();
        var (_, provider) = CreateService(notified, Guid.NewGuid(), Guid.NewGuid());
        await using var owned = provider;

        await Startup(provider).StartAsync(CancellationToken.None);

        notified.Should().BeEmpty("the configured source is the operator's, not every tenant's");
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
    public void The_pending_config_offers_the_configured_url_by_its_host()
    {
        var service = MigrationJobHarness.CreateService(
            new ServiceCollection().BuildServiceProvider(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MIGRATION_MODE"] = "Api",
                ["MIGRATION_NS_URL"] = $"https://user:pass@example-nightscout.invalid/{PathSecret}/?token=synthetic-token",
            }).Build());

        var pending = service.GetPendingConfig(instanceStanding: true);

        pending.HasPendingConfig.Should().BeTrue();
        pending.NightscoutUrl.Should().Be(ShownUrl);
    }

    /// <summary>A superuser of <paramref name="tenant"/>, a platform admin when <paramref name="platformAdmin"/>.</summary>
    private static MigrationController Controller(
        MigrationJobService service, IServiceProvider provider, TenantContext tenant, bool platformAdmin = false)
    {
        var requestServices = provider.CreateScope().ServiceProvider;
        requestServices.GetRequiredService<ITenantAccessor>().SetTenant(tenant);
        var httpContext = new DefaultHttpContext { RequestServices = requestServices };
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Guid.CreateVersion7(),
            TenantId = tenant.TenantId,
            IsPlatformAdmin = platformAdmin,
        };

        return new MigrationController(
            service,
            Mock.Of<IConnectorConfigurationService>(),
            Mock.Of<ITenantAccessor>(a => a.Context == tenant && a.TenantId == tenant.TenantId),
            TimeProvider.System,
            NullLogger<MigrationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            ProblemDetailsFactory = new EchoingProblemDetailsFactory(),
        };
    }

    [Fact]
    public async Task A_start_that_sends_no_url_imports_the_configured_source_the_startup_check_matches()
    {
        var tenantId = Guid.NewGuid();
        var notified = new List<Guid>();
        var (service, provider) = CreateService(notified, tenantId);
        await using var owned = provider;
        var tenant = new TenantContext(tenantId, "migrated", "Test Tenant", true, IsDemo: false);

        var started = await Controller(service, provider, tenant).StartMigration(
            new StartMigrationRequest { Mode = MigrationMode.Api, NightscoutUrl = "" }, CancellationToken.None);
        started.Result.Should().BeOfType<AcceptedAtActionResult>();
        await MigrationJobHarness.RunToEndAsync(service, tenant);
        await CompleteRunsAsync(provider, tenantId);

        await using (var scope = provider.CreateAsyncScope())
        await using (var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync())
        {
            var source = await db.MigrationSources.SingleAsync();
            source.SourceIdentifier.Should().Be(HashUtils.Sha256Hex(SiteBase));
            source.NightscoutUrl.Should().Be(ShownUrl);
        }

        await Startup(provider).StartAsync(CancellationToken.None);
        notified.Should().BeEmpty("the run started from the pending config completed for the configured source");
    }

    [Fact]
    public async Task A_typed_url_is_started_as_typed_when_a_url_is_configured()
    {
        var (service, provider) = CreateService();
        await using var owned = provider;
        var tenant = Tenant();

        await Controller(service, provider, tenant).StartMigration(
            new StartMigrationRequest { Mode = MigrationMode.Api, NightscoutUrl = "https://other-nightscout.invalid/site" },
            CancellationToken.None);
        await MigrationJobHarness.RunToEndAsync(service, tenant);

        await using var scope = provider.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        (await db.MigrationSources.SingleAsync()).NightscoutUrl.Should().Be("other-nightscout.invalid/…");
    }

    [Theory]
    [InlineData("", $"https://example-nightscout.invalid/{PathSecret}/api/v1/status?token=synthetic-token")]
    [InlineData("https://other-nightscout.invalid", "https://other-nightscout.invalid/api/v1/status")]
    public async Task A_connection_test_reads_the_configured_url_unless_one_is_typed(string typed, string reached)
    {
        var handler = new StatusOnly();
        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var service = MigrationJobHarness.CreateService(provider, Configured);
        var tenant = Tenant();
        SeedTenants(provider, tenant.TenantId);

        var response = await Controller(service, provider, tenant).TestConnection(
            new TestMigrationConnectionRequest { Mode = MigrationMode.Api, NightscoutUrl = typed }, CancellationToken.None);

        var result = response.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<TestMigrationConnectionResult>().Subject;
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        handler.Requests.Should().ContainSingle().Which.GetLeftPart(UriPartial.Query).Should().Be(reached);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_caller_with_standing_over_the_instance_is_offered_the_configured_url_and_reads_it(bool platformAdmin)
    {
        var handler = new StatusOnly();
        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var service = MigrationJobHarness.CreateService(provider, Configured);
        var tenant = Tenant();
        if (platformAdmin)
            SeedTenants(provider, Guid.NewGuid(), tenant.TenantId);
        else
            SeedTenants(provider, tenant.TenantId);
        var controller = Controller(service, provider, tenant, platformAdmin);

        var pending = (await controller.GetPendingConfig()).Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<PendingMigrationConfig>().Subject;
        await controller.TestConnection(
            new TestMigrationConnectionRequest { Mode = MigrationMode.Api, NightscoutUrl = "" }, CancellationToken.None);

        pending.HasPendingConfig.Should().BeTrue();
        pending.NightscoutUrl.Should().Be(ShownUrl);
        handler.Requests.Should().ContainSingle().Which.GetLeftPart(UriPartial.Query)
            .Should().Be($"{SiteBase}/api/v1/status?token=synthetic-token");
    }

    [Fact]
    public async Task A_superuser_of_another_tenant_is_not_offered_the_configured_url_and_cannot_read_it()
    {
        var handler = new StatusOnly();
        await using var provider = MigrationJobHarness.BuildProvider(handler);
        var service = MigrationJobHarness.CreateService(provider, Configured);
        var tenant = Tenant();
        SeedTenants(provider, Guid.NewGuid(), tenant.TenantId);
        var controller = Controller(service, provider, tenant);

        var pending = (await controller.GetPendingConfig()).Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<PendingMigrationConfig>().Subject;
        var tested = await controller.TestConnection(
            new TestMigrationConnectionRequest { Mode = MigrationMode.Api, NightscoutUrl = "" }, CancellationToken.None);
        var started = await controller.StartMigration(
            new StartMigrationRequest { Mode = MigrationMode.Api, NightscoutUrl = "" }, CancellationToken.None);

        pending.HasPendingConfig.Should().BeFalse();
        pending.NightscoutUrl.Should().BeNull();
        tested.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        started.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await MigrationJobHarness.RunToEndAsync(service, tenant);
        handler.Requests.Should().BeEmpty();
    }
}
