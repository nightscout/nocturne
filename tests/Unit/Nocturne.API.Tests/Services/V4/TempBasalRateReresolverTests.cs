using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

public class TempBasalRateReresolverTests
{
    private static readonly DateTime VersionStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NextVersionStart = VersionStart.AddDays(10);

    private readonly Mock<IBasalRateResolver> _basalRates = new();
    private readonly Mock<ITempBasalRepository> _tempBasals = new();
    private readonly Mock<IBasalScheduleRepository> _basalSchedules = new();
    private readonly Mock<ITherapySettingsRepository> _therapySettings = new();
    private readonly Mock<ITenantCache> _cache = new();

    public TempBasalRateReresolverTests()
    {
        _basalSchedules
            .Setup(r => r.GetByProfileNameAsync("Day", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BasalSchedule { Timestamp = VersionStart }, new BasalSchedule { Timestamp = NextVersionStart }]);
        _therapySettings
            .Setup(r => r.GetByProfileNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _tempBasals
            .Setup(r => r.GetScheduleDerivedAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<(DateTime, Guid)?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _tempBasals.Setup(r => r.HasScheduleDerivedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    private TempBasalRateReresolver Reresolver(IProfileActivationRepository? activations = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Mock.Of<ITenantAccessor>());
        services.AddScoped(_ => Mock.Of<ICategoryReadContext>());
        services.AddScoped(_ => new ProfileVersionCache(TestTenantCache.Create()));
        services.AddScoped(_ => _basalSchedules.Object);
        services.AddScoped(_ => _therapySettings.Object);
        services.AddScoped(_ => _tempBasals.Object);
        services.AddScoped(_ => activations ?? Mock.Of<IProfileActivationRepository>());
        services.AddScoped(_ => _cache.Object);
        services.AddScoped(_ => TempBasalTestDeps.Rates(_basalRates.Object));

        var request = services.BuildServiceProvider().CreateScope().ServiceProvider;
        var tenant = Mock.Get(request.GetRequiredService<ITenantAccessor>());
        tenant.Setup(t => t.IsResolved).Returns(true);
        tenant.Setup(t => t.Context).Returns(new TenantContext(Guid.CreateVersion7(), "t", "T", true, false));

        return new TempBasalRateReresolver(request, NullLogger<TempBasalRateReresolver>.Instance);
    }

    private static TempBasal Derived(DateTime start, double? rate, double percent = -50) => new()
    {
        Id = Guid.CreateVersion7(),
        StartTimestamp = start,
        Percent = percent,
        Rate = rate,
        ScheduledRate = rate is null ? null : rate * 2,
        RateFromSchedule = rate is not null,
    };

    [Fact]
    public async Task AfterSchedulesWritten_ReresolvesTheDerivedRatesTheVersionGoverns()
    {
        var stale = Derived(VersionStart.AddDays(1), 0.4);
        var unknown = Derived(VersionStart.AddDays(2), null);
        var current = Derived(VersionStart.AddDays(3), 0.5);
        _tempBasals
            .Setup(r => r.GetScheduleDerivedAsync(VersionStart, NextVersionStart, null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([stale, unknown, current]);
        _basalRates
            .Setup(r => r.FindBasalRateAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1.0);
        _tempBasals
            .Setup(r => r.SetScheduleDerivedRateAsync(It.IsAny<Guid>(), -50, It.IsAny<double?>(), 0.5, 1.0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await Reresolver().AfterSchedulesWrittenAsync([("Day", VersionStart)], CancellationToken.None);

        _tempBasals.Verify(r => r.SetScheduleDerivedRateAsync(stale.Id, -50, 0.4, 0.5, 1.0, It.IsAny<CancellationToken>()), Times.Once);
        _tempBasals.Verify(r => r.SetScheduleDerivedRateAsync(unknown.Id, -50, null, 0.5, 1.0, It.IsAny<CancellationToken>()), Times.Once);
        _tempBasals.Verify(r => r.SetScheduleDerivedRateAsync(current.Id, It.IsAny<double>(), It.IsAny<double?>(),
            It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()), Times.Never);
        _cache.Verify(c => c.RemoveFamilyAsync(TenantCacheFamily.Treatments, It.IsAny<CancellationToken>()), Times.Once);
        _cache.Verify(c => c.RemoveFamilyAsync(TenantCacheFamily.Statistics, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AfterSchedulesWritten_TheNewestVersionAlsoCoversTheTimeBeforeTheFirst()
    {
        await Reresolver().AfterSchedulesWrittenAsync([("Day", NextVersionStart)], CancellationToken.None);

        _tempBasals.Verify(r => r.GetScheduleDerivedAsync(null, VersionStart, null, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _tempBasals.Verify(r => r.GetScheduleDerivedAsync(NextVersionStart, null, null, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AFailureWhileReresolving_DoesNotFailTheWrite()
    {
        _tempBasals
            .Setup(r => r.GetScheduleDerivedAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<(DateTime, Guid)?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database went away"));

        var act = () => Reresolver().AfterSchedulesWrittenAsync([("Day", VersionStart)], CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AnActivationWithNoEnd_IsOpenEnded()
    {
        var start = VersionStart.AddDays(1);

        await Reresolver().AfterActivationsChangedAsync([("Day", start, null)], CancellationToken.None);

        _tempBasals.Verify(r => r.GetScheduleDerivedAsync(start, null, null, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A permanent activation P applies again after a temporary one T ends, so writing P re-resolves
    /// the temps after T as well, not only those before T starts.
    /// </summary>
    [Fact]
    public async Task APermanentActivationWrittenBeforeATemporaryOne_CoversTheTimeAfterItEnds()
    {
        var permanentStart = VersionStart.AddDays(1);
        var temporaryStart = permanentStart.AddHours(2);
        var afterTemporary = temporaryStart.AddHours(3);
        var tempAfter = Derived(afterTemporary, 0.4);
        _tempBasals
            .Setup(r => r.GetScheduleDerivedAsync(permanentStart, null, null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([tempAfter]);
        _basalRates
            .Setup(r => r.FindBasalRateAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1.5);
        _tempBasals
            .Setup(r => r.SetScheduleDerivedRateAsync(tempAfter.Id, -50, 0.4, 0.75, 1.5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await Reresolver().AfterActivationsChangedAsync(
            [("Day", permanentStart, null), ("Day", temporaryStart, temporaryStart.AddHours(1))], CancellationToken.None);

        _tempBasals.Verify(r => r.SetScheduleDerivedRateAsync(tempAfter.Id, -50, 0.4, 0.75, 1.5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ATenantWithNoScheduleDerivedRates_ReadsNoVersionsOrWindows()
    {
        _tempBasals.Setup(r => r.HasScheduleDerivedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Reresolver().AfterSchedulesWrittenAsync([("Day", VersionStart)], CancellationToken.None);

        _basalSchedules.Verify(r => r.GetByProfileNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _tempBasals.Verify(r => r.GetScheduleDerivedAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<(DateTime, Guid)?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletingTheLastVersion_CoversEveryTime()
    {
        _basalSchedules
            .Setup(r => r.GetByProfileNameAsync("Gone", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await Reresolver().AfterSchedulesWrittenAsync([("Gone", VersionStart)], CancellationToken.None);

        _tempBasals.Verify(r => r.GetScheduleDerivedAsync(null, null, null, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
