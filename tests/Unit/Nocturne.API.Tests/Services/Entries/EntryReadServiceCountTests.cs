using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Entries;

public class EntryReadServiceCountTests
{
    private readonly Mock<ISensorGlucoseRepository> _sgRepo = new();
    private readonly Mock<IMeterGlucoseRepository> _mgRepo = new();
    private readonly Mock<ICalibrationRepository> _calRepo = new();
    private readonly Mock<IDemoModeService> _demoMode = new();
    private readonly EntryReadService _sut;

    public EntryReadServiceCountTests()
    {
        _demoMode.Setup(d => d.IsEnabled).Returns(false);
        _sut = new EntryReadService(
            _sgRepo.Object,
            _mgRepo.Object,
            _calRepo.Object,
            _demoMode.Object,
            Mock.Of<ILogger<EntryReadService>>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountAsync_WithNoFilters_SumsAllRepos()
    {
        _sgRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(10);
        _mgRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(5);
        _calRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(3);

        var result = await _sut.CountAsync();

        Assert.Equal(18, result);
        _sgRepo.Verify(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()), Times.Once);
        _mgRepo.Verify(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()), Times.Once);
        _calRepo.Verify(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountAsync_WithSgvType_OnlyCountsSensorGlucose()
    {
        _sgRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(42);

        var result = await _sut.CountAsync(type: "sgv");

        Assert.Equal(42, result);
        _sgRepo.Verify(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()), Times.Once);
        _mgRepo.Verify(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _calRepo.Verify(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountAsync_WithTimeRangeInFind_PassesTimeBoundsToRepos()
    {
        // MongoDB-style find query with date.$gte and date.$lte in epoch millis
        var fromMs = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(2025, 1, 16, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var find = $"{{\"date\":{{\"$gte\":{fromMs},\"$lte\":{toMs}}}}}";

        var expectedFrom = DateTimeOffset.FromUnixTimeMilliseconds(fromMs).UtcDateTime;
        var expectedTo = DateTimeOffset.FromUnixTimeMilliseconds(toMs).UtcDateTime;

        _sgRepo.Setup(r => r.CountAsync(expectedFrom, expectedTo, It.IsAny<CancellationToken>())).ReturnsAsync(7);
        _mgRepo.Setup(r => r.CountAsync(expectedFrom, expectedTo, It.IsAny<CancellationToken>())).ReturnsAsync(2);
        _calRepo.Setup(r => r.CountAsync(expectedFrom, expectedTo, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await _sut.CountAsync(find: find);

        Assert.Equal(10, result);
        _sgRepo.Verify(r => r.CountAsync(expectedFrom, expectedTo, It.IsAny<CancellationToken>()), Times.Once);
        _mgRepo.Verify(r => r.CountAsync(expectedFrom, expectedTo, It.IsAny<CancellationToken>()), Times.Once);
        _calRepo.Verify(r => r.CountAsync(expectedFrom, expectedTo, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountAsync_WithMbgType_OnlyCountsMeterGlucose()
    {
        _mgRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(15);

        var result = await _sut.CountAsync(type: "mbg");

        Assert.Equal(15, result);
        _mgRepo.Verify(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()), Times.Once);
        _sgRepo.Verify(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _calRepo.Verify(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountAsync_WithCalType_OnlyCountsCalibrations()
    {
        _calRepo.Setup(r => r.CountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(8);

        var result = await _sut.CountAsync(type: "cal");

        Assert.Equal(8, result);
        _calRepo.Verify(r => r.CountAsync(null, null, It.IsAny<CancellationToken>()), Times.Once);
        _sgRepo.Verify(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _mgRepo.Verify(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
