using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Platform.Releases;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform.Releases;

public class ReleaseStatusServiceTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<IReleaseSource> _releases = new();
    private readonly Mock<IAutoUpdateProbe> _probe = new();

    public ReleaseStatusServiceTests()
    {
        _probe.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AutoUpdateStatusDto { Mode = AutoUpdateMode.Automatic });
    }

    [Fact]
    public async Task ANewerReleaseIsAnUpdate()
    {
        _releases.Setup(r => r.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Release("0.2.8"));
        _releases.Setup(r => r.GetByVersionAsync(It.IsAny<ReleaseVersion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Release("0.2.7"));

        var status = await CreateService("0.2.7").GetStatusAsync(CancellationToken.None);

        status.UpdateAvailable.Should().BeTrue();
        status.Latest!.Version.Should().Be("0.2.8");
        status.Current!.Version.Should().Be("0.2.7");
        status.AutoUpdate.Mode.Should().Be(AutoUpdateMode.Automatic);
    }

    [Fact]
    public async Task TheLatestReleaseRunningIsNoUpdateAndIsNotReadTwice()
    {
        _releases.Setup(r => r.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Release("0.2.8"));

        var status = await CreateService("0.2.8").GetStatusAsync(CancellationToken.None);

        status.UpdateAvailable.Should().BeFalse();
        status.Latest.Should().BeNull();
        status.Current!.Version.Should().Be("0.2.8");
        _releases.Verify(r => r.GetByVersionAsync(It.IsAny<ReleaseVersion>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ABuildThatIsNotAReleaseReadsNothing()
    {
        var status = await CreateService("1.0.0", isRelease: false).GetStatusAsync(CancellationToken.None);

        status.IsReleaseBuild.Should().BeFalse();
        status.UpdateAvailable.Should().BeFalse();
        _releases.VerifyNoOtherCalls();
        _probe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReleasesAreReadOncePerCheckInterval()
    {
        _releases.Setup(r => r.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Release("0.2.7"));
        var service = CreateService("0.2.7");

        await service.GetStatusAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(5));
        await service.GetStatusAsync(CancellationToken.None);
        _releases.Verify(r => r.GetLatestAsync(It.IsAny<CancellationToken>()), Times.Once);

        _clock.Advance(TimeSpan.FromHours(1));
        await service.GetStatusAsync(CancellationToken.None);
        _releases.Verify(r => r.GetLatestAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AFailedReadKeepsTheLastGoodOneAndRetriesLater()
    {
        _releases.SetupSequence(r => r.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Release("0.2.8"))
            .ThrowsAsync(new HttpRequestException("offline"))
            .ReturnsAsync(Release("0.2.9"));
        var service = CreateService("0.2.7");

        await service.GetStatusAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(6));
        var stale = await service.GetStatusAsync(CancellationToken.None);
        stale.Latest!.Version.Should().Be("0.2.8");

        _clock.Advance(ReleaseStatusService.RetryAfterFailure - TimeSpan.FromMinutes(1));
        await service.GetStatusAsync(CancellationToken.None);
        _releases.Verify(r => r.GetLatestAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));

        _clock.Advance(TimeSpan.FromMinutes(1));
        var fresh = await service.GetStatusAsync(CancellationToken.None);
        fresh.Latest!.Version.Should().Be("0.2.9");
    }

    private ReleaseStatusService CreateService(string version, bool isRelease = true) => new(
        _releases.Object,
        _probe.Object,
        new RunningRelease(version, isRelease),
        Options.Create(new ReleaseCheckOptions { CheckInterval = TimeSpan.FromHours(6) }),
        _clock,
        NullLogger<ReleaseStatusService>.Instance);

    internal static ReleaseNotesDto Release(string version) => new()
    {
        Version = version,
        Url = $"https://github.com/nightscout/nocturne/releases/tag/v{version}",
        NotesHtml = $"<p>{version}</p>",
    };
}
