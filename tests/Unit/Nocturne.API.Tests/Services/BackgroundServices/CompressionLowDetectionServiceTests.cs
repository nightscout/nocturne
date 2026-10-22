using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// Detection reads the tenant's enable flag, bedtime and wake time from
/// <see cref="IUISettingsService"/>, so a read it cannot trust leaves it with no window to analyse.
/// </summary>
[Trait("Category", "Unit")]
public class CompressionLowDetectionServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateOnly NightOf = new(2026, 4, 17);

    private readonly Mock<ICompressionLowRepository> _repository = new();
    private readonly Mock<IEntryService> _entryService = new();

    [Fact]
    public async Task DetectForNightAsync_doesNotDetectOnDefaultSettingsWhenTheSettingsReadFails()
    {
        var sut = Detector();
        await using var scope = (await BrokenSettingsProviderAsync()).CreateAsyncScope();

        var created = await sut.DetectForNightAsync(NightOf, scope.ServiceProvider, CancellationToken.None);

        created.Should().Be(0);
        _repository.Verify(
            r =>
                r.ActiveSuggestionsExistForNightAsync(
                    It.IsAny<DateOnly>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        _entryService.Verify(
            e =>
                e.GetEntriesAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    private static readonly double[] CompressionLow =
        [110, 110, 110, 100, 85, 65, 60, 80, 105, 110, 110, 110];

    private const long FiveMinutes = 5 * 60 * 1000;
    private const long NightStart = 1_000_000_000;

    private static List<Entry> Readings(
        string dataSource,
        string device,
        long offsetMills,
        IEnumerable<double> values,
        Func<int, bool>? present = null) =>
        values
            .Select((value, index) => (value, index))
            .Where(r => present?.Invoke(r.index) ?? true)
            .Select(r => new Entry
            {
                DataSource = dataSource,
                Device = device,
                Mills = NightStart + offsetMills + r.index * FiveMinutes,
                Sgv = r.value,
                Mgdl = r.value,
            })
            .ToList();

    private static List<Entry> Primary(Func<int, bool>? present = null) =>
        Readings("xdrip", "sensor-a", 0, CompressionLow, present);

    private static List<Entry> Canonical(params List<Entry>[] streams) =>
        streams.SelectMany(stream => stream).OrderBy(e => e.Mills).ToList();

    private static CompressionLowDetectionService Detector() =>
        new(
            Mock.Of<IServiceProvider>(),
            ActiveTenantSnapshotTestDoubles.Unread(),
            NullLogger<CompressionLowDetectionService>.Instance
        );

    [Fact]
    public void DetectVShapeCandidates_recoversOnThePrimarySensorWhenAnotherSensorFillsItsGap()
    {
        var primary = Primary(index => index != 7);
        var fill = Readings("dexcom", "sensor-b", FiveMinutes / 2, Enumerable.Repeat(110d, CompressionLow.Length), index => index == 7);

        var candidate = Detector().DetectVShapeCandidates(Canonical(primary, fill)).Should().ContainSingle().Subject;

        candidate.LowestGlucose.Should().Be(60);
        candidate.RecoveryMinutes.Should().Be(10);
        candidate.EndMills.Should().Be(primary[^4].Mills);
    }

    [Fact]
    public void DetectVShapeCandidates_separatesTwoSensorsThatShareADeviceByDataSource()
    {
        var primary = Readings("xdrip", "connector", 0, CompressionLow, index => index != 7);
        var fill = Readings("dexcom", "connector", FiveMinutes / 2, Enumerable.Repeat(110d, CompressionLow.Length), index => index == 7);

        var candidate = Detector().DetectVShapeCandidates(Canonical(primary, fill)).Should().ContainSingle().Subject;

        candidate.RecoveryMinutes.Should().Be(10);
        candidate.EndMills.Should().Be(primary[^4].Mills);
    }

    [Fact]
    public void DetectVShapeCandidates_findsNothingFromAConstantOffsetBetweenSensors()
    {
        var high = Readings("xdrip", "sensor-a", 0, Enumerable.Repeat(120d, CompressionLow.Length), index => index % 3 != 1);
        var offset = Readings("dexcom", "sensor-b", FiveMinutes / 2, Enumerable.Repeat(60d, CompressionLow.Length), index => index % 3 == 1);

        Detector().DetectVShapeCandidates(Canonical(high, offset)).Should().BeEmpty();
    }

    [Fact]
    public void DetectVShapeCandidates_detectsASingleSensorNightWhetherOrNotItNamesTheDataSource()
    {
        var named = Detector().DetectVShapeCandidates(Primary());
        var unnamed = Detector().DetectVShapeCandidates(
            Primary().Select(e => { e.DataSource = null; return e; }).ToList());

        named.Should().ContainSingle().Which.LowestGlucose.Should().Be(60);
        unnamed.Should().Equal(named);
    }

    /// <summary>
    /// A provider whose <see cref="IUISettingsService"/> is the real one over a database that can no
    /// longer be read, which is what a failed read looks like from inside the scope.
    /// </summary>
    private async Task<ServiceProvider> BrokenSettingsProviderAsync()
    {
        _entryService
            .Setup(e =>
                e.GetEntriesAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Array.Empty<Entry>());

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new NocturneDbContext(options) { TenantId = TenantId };
        var settingsService = new UISettingsService(
            context.AsFactory(),
            NullLogger<UISettingsService>.Instance
        );
        await context.DisposeAsync();

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(a => a.TenantId).Returns(TenantId);

        var services = new ServiceCollection();
        services.AddScoped<IUISettingsService>(_ => settingsService);
        services.AddScoped(_ => _repository.Object);
        services.AddScoped(_ => _entryService.Object);
        services.AddScoped(_ => Mock.Of<ITreatmentService>());
        services.AddScoped(_ => Mock.Of<IInAppNotificationService>());
        services.AddScoped(_ => Mock.Of<ITherapySettingsResolver>());
        services.AddScoped(_ => tenantAccessor.Object);

        return services.BuildServiceProvider();
    }
}
