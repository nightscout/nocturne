using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Seeding;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Demo;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

public class SampleDataLiveFeedServiceTests
{
    private static TenantRecord Tenant(Guid id, bool isDemo = false, string? demoKind = null) =>
        new(id, "t", "T", IsActive: true, isDemo, AllowAccessRequests: true, AllowPublicDocs: false,
            OnboardingCompletedAt: null, SetupHubEnrolledAt: null, SetupStripDismissedRevision: null,
            PatientRelationship: null, HandedOverAt: null, DefaultGlucoseUnits: null, LastReadingAt: null,
            ShareTokenHash: null, ShareTokenEncrypted: null, ShareTokenSetAt: null, ShareLastAccessedAt: null,
            SysCreatedAt: default, SysUpdatedAt: default,
            DemoConfig: demoKind is null ? null : new DemoTenantConfig(null, null, "open", 30, 5, 0, demoKind, null));

    [Theory]
    [InlineData(true, false, 1, 0)]
    [InlineData(false, true, 0, 1)]
    [InlineData(true, true, 1, 1)]
    [InlineData(false, false, 0, 0)]
    public void A_tenant_is_admitted_only_by_the_flag_that_names_its_kind(
        bool sampleTenants, bool demoPeople, int nonDemoAdmitted, int personAdmitted)
    {
        var scope = new SampleDataFeedScope(sampleTenants, demoPeople);

        (scope.Admits(Tenant(Guid.NewGuid())) ? 1 : 0).Should().Be(nonDemoAdmitted);
        (scope.Admits(Tenant(Guid.NewGuid(), isDemo: true, DemoTenantKind.Person)) ? 1 : 0).Should().Be(personAdmitted);
        scope.Admits(Tenant(Guid.NewGuid(), isDemo: true, DemoTenantKind.Host)).Should().BeFalse("the demo service streams the host");
        scope.Admits(Tenant(Guid.NewGuid(), isDemo: true)).Should().BeFalse("a demo tenant of no kind is not a person");
        scope.Any.Should().Be(sampleTenants || demoPeople);
    }

    [Theory]
    [InlineData(null, null, null, false, false)]
    [InlineData("true", null, null, true, false)]
    [InlineData("false", null, null, false, false)]
    [InlineData(null, "true", null, false, true)]
    [InlineData(null, null, "true", false, true)]
    [InlineData("true", "true", null, true, true)]
    public void The_scope_is_read_from_the_sample_data_key_and_from_demo_mode(
        string? live, string? demoService, string? demoParameters, bool sampleTenants, bool demoPeople)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SampleDataLiveFeed.EnabledKey] = live,
                ["DemoService:Enabled"] = demoService,
                ["Parameters:DemoMode:Enabled"] = demoParameters,
            })
            .Build();

        SampleDataFeedScope.From(configuration).Should().Be(new SampleDataFeedScope(sampleTenants, demoPeople));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Only_the_kind_of_tenant_the_scope_names_is_carried_on(bool sampleTenants, bool demoPeople)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero));
        var advanced = new SemaphoreSlim(0);
        var tenants = new Mock<ITenantRepository>();
        tenants.Setup(r => r.ListAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Tenant(Guid.NewGuid(), isDemo: true, DemoTenantKind.Host),
                Tenant(Guid.NewGuid(), isDemo: true),
                Tenant(Guid.NewGuid(), isDemo: true, DemoTenantKind.Person),
                Tenant(Guid.NewGuid()),
            ]);
        var contexts = new Mock<INocturneDbContextFactory>();
        contexts.Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                advanced.Release();
                throw new InvalidDataException("the feed reached a tenant");
            });

        await using var provider = Provider(tenants.Object, contexts.Object, time);
        using var service = new SampleDataLiveFeedService(
            provider, time, new SampleDataFeedScope(sampleTenants, demoPeople), NullLogger<SampleDataLiveFeedService>.Instance);
        await service.StartAsync(CancellationToken.None);

        (await advanced.WaitAsync(Eventually.DefaultPatience)).Should().BeTrue();
        await service.StopAsync(CancellationToken.None);
        contexts.Verify(f => f.CreateAsync(It.IsAny<CancellationToken>()), Times.Once, "one tenant of the two kinds is reached");
    }

    private static ServiceProvider Provider(ITenantRepository tenants, INocturneDbContextFactory contexts, TimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor>(_ => new SettableTenantAccessor());
        services.AddScoped(_ => Mock.Of<ICategoryReadContext>());
        services.AddSingleton(tenants);
        services.AddScoped(sp => new SampleDataLiveFeed(
            sp.GetRequiredService<ITenantAccessor>(),
            contexts,
            Mock.Of<IEntryService>(),
            Mock.Of<ITreatmentService>(),
            Mock.Of<IDeviceStatusService>(),
            Mock.Of<IHeartRateService>(),
            Mock.Of<IStepCountService>(),
            Mock.Of<ISleepService>(),
            Mock.Of<ITempBasalRepository>(),
            time,
            NullLoggerFactory.Instance,
            NullLogger<SampleDataLiveFeed>.Instance));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_failure_listing_tenants_or_advancing_one_is_retried_on_the_next_tick()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero));
        var listed = new SemaphoreSlim(0);
        var advanced = new SemaphoreSlim(0);
        var listCalls = 0;

        var tenants = new Mock<ITenantRepository>();
        tenants.Setup(r => r.ListAsync(true, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                listed.Release();
                return ++listCalls == 1
                    ? throw new InvalidDataException("the database restarted")
                    : Task.FromResult<IReadOnlyList<TenantRecord>>([Tenant(Guid.NewGuid()), Tenant(Guid.NewGuid(), isDemo: true)]);
            });

        var contexts = new Mock<INocturneDbContextFactory>();
        contexts.Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                advanced.Release();
                throw new InvalidDataException("a write the feed does not expect failed");
            });

        await using var provider = Provider(tenants.Object, contexts.Object, time);

        using var service = new SampleDataLiveFeedService(provider, time, new SampleDataFeedScope(true, true), NullLogger<SampleDataLiveFeedService>.Instance);
        await service.StartAsync(CancellationToken.None);

        (await listed.WaitAsync(Eventually.DefaultPatience)).Should().BeTrue();
        for (var tick = 0; tick < 2; tick++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            (await advanced.WaitAsync(Eventually.DefaultPatience)).Should().BeTrue("the loop survived the last failure");
        }

        await service.StopAsync(CancellationToken.None);
        service.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        contexts.Verify(f => f.CreateAsync(It.IsAny<CancellationToken>()), Times.Exactly(2), "the demo tenant is skipped");
    }
}
