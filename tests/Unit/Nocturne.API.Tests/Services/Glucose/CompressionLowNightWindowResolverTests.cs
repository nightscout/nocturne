using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;
using Xunit;
using Nocturne.Core.Contracts.Entries;

namespace Nocturne.API.Tests.Services.Glucose;

[Trait("Category", "Unit")]
public class CompressionLowNightWindowResolverTests
{
    private static readonly DateOnly NightOf = new(2026, 4, 17);
    private const int OffsetMinutes = 600;

    private readonly Mock<ITherapySettingsResolver> _therapySettings = new();
    private readonly Mock<IEntryService> _entries = new();
    private readonly List<string> _windowQueries = [];

    private static SleepScheduleSettings Schedule(string? timezone = null) =>
        new() { Timezone = timezone ?? "", BedtimeHour = 22, WakeTimeHour = 6 };

    private CompressionLowNightWindowResolver Resolver() =>
        new(_therapySettings.Object, _entries.Object, NullLogger<CompressionLowNightWindowResolver>.Instance);

    private void EntriesCarryOffset(int? offsetMinutes)
    {
        _entries
            .Setup(e => e.GetEntriesAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? find, int? count, int? _, CancellationToken _) =>
            {
                if (count == 10)
                    return offsetMinutes is null
                        ? Array.Empty<Entry>()
                        : [new Entry { Mills = 1_000, Sgv = 100, Mgdl = 100, UtcOffset = offsetMinutes }];

                _windowQueries.Add(find!);
                return Array.Empty<Entry>();
            });
    }

    [Fact]
    public async Task ResolveAsync_infersTheZoneFromEntryOffsetsWhenNoZoneIsConfigured()
    {
        EntriesCarryOffset(OffsetMinutes);

        var window = await Resolver().ResolveAsync(NightOf, Schedule(), CancellationToken.None);

        window.TimeZone.BaseUtcOffset.Should().Be(TimeSpan.FromMinutes(OffsetMinutes));
        var (start, end) = TimeZoneHelper.GetOvernightWindow(NightOf, window.TimeZone, 22, 6);
        window.WindowStart.Should().Be(start);
        window.WindowEnd.Should().Be(end);
        window.BedtimeHour.Should().Be(22);
        window.WakeTimeHour.Should().Be(6);
    }

    [Fact]
    public async Task ResolveAsync_prefersTheSleepScheduleZoneOverTheProfileAndEntries()
    {
        EntriesCarryOffset(OffsetMinutes);
        _therapySettings
            .Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Australia/Sydney");

        var window = await Resolver().ResolveAsync(NightOf, Schedule("America/New_York"), CancellationToken.None);

        window.TimeZone.Id.Should().Be(TimeZoneHelper.GetTimeZoneInfoFromId("America/New_York").Id);
    }

    [Fact]
    public async Task ResolveAsync_prefersTheProfileZoneOverEntryOffsets()
    {
        EntriesCarryOffset(OffsetMinutes);
        _therapySettings
            .Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("America/New_York");

        var window = await Resolver().ResolveAsync(NightOf, Schedule(), CancellationToken.None);

        window.TimeZone.Id.Should().Be(TimeZoneHelper.GetTimeZoneInfoFromId("America/New_York").Id);
    }

    [Fact]
    public async Task ResolveAsync_fallsThroughAFailingProfileLookupToTheEntryOffsets()
    {
        EntriesCarryOffset(OffsetMinutes);
        _therapySettings
            .Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("profile store down"));

        var window = await Resolver().ResolveAsync(NightOf, Schedule(), CancellationToken.None);

        window.TimeZone.BaseUtcOffset.Should().Be(TimeSpan.FromMinutes(OffsetMinutes));
    }

    [Fact]
    public async Task ResolveAsync_fallsBackToUtcWhenNothingNamesAZone()
    {
        EntriesCarryOffset(null);

        var window = await Resolver().ResolveAsync(NightOf, Schedule("Not/AZone"), CancellationToken.None);

        window.TimeZone.Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public async Task ResolveAsync_fallsBackToUtcWhenTheEntryProbeFails()
    {
        _entries
            .Setup(e => e.GetEntriesAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("entry store down"));

        var window = await Resolver().ResolveAsync(NightOf, Schedule(), CancellationToken.None);

        window.TimeZone.Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public async Task ResolveAsync_keepsAnExplicitUtcZoneOverEntryOffsets()
    {
        EntriesCarryOffset(OffsetMinutes);

        var window = await Resolver().ResolveAsync(NightOf, Schedule("UTC"), CancellationToken.None);

        window.TimeZone.Should().Be(TimeZoneInfo.Utc);
        _entries.Verify(
            e => e.GetEntriesAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_doesNotSwallowCancellation()
    {
        _therapySettings
            .Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Resolver().Invoking(r => r.ResolveAsync(NightOf, Schedule(), CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DetectionAndTheSuggestionDetailCutTheSameWindowWhenNoZoneIsConfigured()
    {
        EntriesCarryOffset(OffsetMinutes);
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(a => a.TenantId).Returns(Guid.NewGuid());
        var settings = new Mock<IUISettingsService>();
        settings
            .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UISettingsConfiguration
            {
                DataQuality = new DataQualitySettings { SleepSchedule = Schedule() },
            });
        var repository = new Mock<ICompressionLowRepository>();
        var suggestion = new CompressionLowSuggestion { Id = Guid.NewGuid(), NightOf = NightOf };
        repository
            .Setup(r => r.GetByIdAsync(suggestion.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(suggestion);
        var treatments = new Mock<ITreatmentService>();
        treatments
            .Setup(t => t.GetTreatmentsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Treatment>());

        var services = new ServiceCollection();
        services.AddScoped(_ => settings.Object);
        services.AddScoped(_ => repository.Object);
        services.AddScoped(_ => _entries.Object);
        services.AddScoped(_ => _therapySettings.Object);
        services.AddScoped<ICompressionLowNightWindowResolver, CompressionLowNightWindowResolver>();
        services.AddScoped(_ => Mock.Of<IIobCobReader>());
        services.AddScoped(_ => Mock.Of<ISensitivityResolver>());
        services.AddScoped(_ => Mock.Of<IInAppNotificationService>());
        services.AddScoped(_ => Mock.Of<ITenantOwnerResolver>());
        services.AddScoped(_ => tenantAccessor.Object);
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var detector = new CompressionLowDetectionService(TimeProvider.System, NullLogger<CompressionLowDetectionService>.Instance);
        await detector.DetectForNightAsync(NightOf, scope.ServiceProvider, CancellationToken.None);
        var detectionQuery = _windowQueries.Should().ContainSingle().Subject;

        _windowQueries.Clear();
        var detail = new CompressionLowService(
            repository.Object,
            Mock.Of<IStateSpanService>(),
            _entries.Object,
            treatments.Object,
            Mock.Of<IInAppNotificationService>(),
            scope.ServiceProvider.GetRequiredService<ICompressionLowNightWindowResolver>(),
            settings.Object,
            Mock.Of<ITenantOwnerResolver>(),
            tenantAccessor.Object,
            TimeProvider.System,
            NullLogger<CompressionLowService>.Instance);
        await detail.GetSuggestionWithEntriesAsync(suggestion.Id);

        _windowQueries.Should().ContainSingle().Which.Should().Be(detectionQuery);
        var offsetZone = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromMinutes(OffsetMinutes), "t", "t");
        var (offsetStart, _) = TimeZoneHelper.GetOvernightWindow(NightOf, offsetZone, 22, 6);
        detectionQuery.Should().Contain(offsetStart.ToString());
    }
}
