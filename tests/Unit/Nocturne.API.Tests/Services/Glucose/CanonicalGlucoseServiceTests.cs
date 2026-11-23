using FluentAssertions;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Glucose;

[Trait("Category", "Unit")]
public class CanonicalGlucoseServiceTests
{
    private readonly Mock<ISensorGlucoseRepository> _sgRepo = new();
    private readonly Mock<IDemoModeService> _demoMode = new();

    private static readonly DateTime Now = DateTime.UtcNow;

    private CanonicalGlucoseService CreateSut(bool demoMode = false)
    {
        _demoMode.Setup(d => d.IsEnabled).Returns(demoMode);
        return new CanonicalGlucoseService(_sgRepo.Object, _demoMode.Object);
    }

    private static SensorGlucose Reading(double minutesAgo, double mgdl = 120) => new()
    {
        Id = Guid.NewGuid(),
        Mgdl = mgdl,
        Timestamp = Now.AddMinutes(-minutesAgo),
    };

    private void Serve(string? source, GlucoseStreams streams, params SensorGlucose[] readings) =>
        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), null, null, source,
                It.IsAny<int>(), 0, true, null, null, It.IsAny<CancellationToken>(), null, streams))
            .ReturnsAsync(readings);

    [Fact]
    public async Task GetLatestAsync_ReadsTheNewestCanonicalRealReading()
    {
        var latest = Reading(0, mgdl: 110);
        Serve(null, GlucoseStreams.CanonicalExcludingEphemeral, latest);

        (await CreateSut().GetLatestAsync()).Should().BeSameAs(latest);
        _sgRepo.Verify(r => r.GetAsync(
            null, null, null, null, 1, 0, true, null, null, It.IsAny<CancellationToken>(), null,
            GlucoseStreams.CanonicalExcludingEphemeral), Times.Once);
    }

    [Fact]
    public async Task GetLatestAsync_InDemoMode_ReadsOnlyTheDemoSource()
    {
        var demo = Reading(0, mgdl: 140);
        Serve(DataSources.DemoService, GlucoseStreams.Canonical, demo);

        (await CreateSut(demoMode: true).GetLatestAsync()).Should().BeSameAs(demo);
    }

    [Fact]
    public async Task GetLatestAsync_NoReadings_ReturnsNull()
    {
        Serve(null, GlucoseStreams.CanonicalExcludingEphemeral);

        (await CreateSut().GetLatestAsync()).Should().BeNull();
    }

    [Fact]
    public async Task GetRecentAsync_ReadsTheCanonicalRealReadingsFromSince()
    {
        var since = Now.AddMinutes(-25);
        var recent = new[] { Reading(1, mgdl: 90), Reading(6, mgdl: 95) };
        Serve(null, GlucoseStreams.CanonicalExcludingEphemeral, recent);

        (await CreateSut().GetRecentAsync(since)).Should().Equal(recent);
        _sgRepo.Verify(r => r.GetAsync(
            since, null, null, null, It.IsAny<int>(), 0, true, null, null, It.IsAny<CancellationToken>(), null,
            GlucoseStreams.CanonicalExcludingEphemeral), Times.Once);
    }

    [Theory]
    [InlineData(1440)]
    [InlineData(2000)]
    [InlineData(3001)]
    public async Task GetWindowAsync_ReturnsEveryReadingOfTheWindowNewestFirstPastAnyPageSize(int count)
    {
        var all = Enumerable.Range(0, count).Select(i => Reading(i)).ToList();
        ServePaged(null, GlucoseStreams.CanonicalExcludingEphemeral, all);

        var window = await CreateSut().GetWindowAsync(Now.AddHours(-24), Now);

        window.Should().Equal(all);
    }

    [Fact]
    public async Task GetWindowAsync_InDemoModeReadsOnlyTheDemoSource()
    {
        var all = Enumerable.Range(0, 1440).Select(i => Reading(i)).ToList();
        ServePaged(DataSources.DemoService, GlucoseStreams.Canonical, all);

        var window = await CreateSut(demoMode: true).GetWindowAsync(Now.AddHours(-24), Now);

        window.Should().HaveCount(1440);
    }

    [Fact]
    public async Task GetWindowAsync_WithNoReadingsIsEmpty()
    {
        ServePaged(null, GlucoseStreams.CanonicalExcludingEphemeral, []);

        (await CreateSut().GetWindowAsync(Now.AddHours(-24), Now)).Should().BeEmpty();
    }

    private void ServePaged(string? source, GlucoseStreams streams, List<SensorGlucose> all) =>
        _sgRepo.Setup(r => r.GetAsync(
                Now.AddHours(-24), Now, null, source,
                It.IsAny<int>(), 0, true, It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), null, streams))
            .ReturnsAsync((DateTime? _, DateTime? _, string? _, string? _, int limit, int _, bool _,
                DateTime? afterTimestamp, Guid? afterId, CancellationToken _, Guid? _, GlucoseStreams _) =>
                all.Where(r => afterTimestamp is null
                        || r.Timestamp < afterTimestamp
                        || (r.Timestamp == afterTimestamp && r.Id.CompareTo(afterId!.Value) < 0))
                    .OrderByDescending(r => r.Timestamp).ThenByDescending(r => r.Id)
                    .Take(limit).ToList());
}
