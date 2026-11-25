using FluentAssertions;
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
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

public class SampleDataLiveFeedServiceTests
{
    private static TenantRecord Tenant(Guid id, bool isDemo = false) =>
        new(id, "t", "T", IsActive: true, isDemo, AllowAccessRequests: true, AllowPublicDocs: false,
            OnboardingCompletedAt: null, SetupHubEnrolledAt: null, SetupStripDismissedRevision: null,
            PatientRelationship: null, HandedOverAt: null, DefaultGlucoseUnits: null, LastReadingAt: null,
            ShareTokenHash: null, ShareTokenEncrypted: null, ShareTokenSetAt: null, ShareLastAccessedAt: null,
            SysCreatedAt: default, SysUpdatedAt: default, DemoConfig: null);

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

        var services = new ServiceCollection();
        services.AddScoped(_ => Mock.Of<ITenantAccessor>());
        services.AddScoped(_ => Mock.Of<ICategoryReadContext>());
        services.AddSingleton(tenants.Object);
        services.AddScoped(sp => new SampleDataLiveFeed(
            sp.GetRequiredService<ITenantAccessor>(),
            contexts.Object,
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
        await using var provider = services.BuildServiceProvider();

        using var service = new SampleDataLiveFeedService(provider, time, NullLogger<SampleDataLiveFeedService>.Instance);
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
