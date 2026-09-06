using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Entries;

/// <summary>
/// Tests for <see cref="EntryReadService"/> find-query filtering: a find[type]=x equality routes
/// to the single matching repository, other field filters ($ne, sgv/device conditions) are
/// applied over the projected legacy shape, and time bounds are pushed down.
/// </summary>
public class EntryReadServiceFindFilterTests
{
    private readonly Mock<ISensorGlucoseRepository> _sgRepo = new();
    private readonly Mock<IMeterGlucoseRepository> _mgRepo = new();
    private readonly Mock<ICalibrationRepository> _calRepo = new();
    private readonly Mock<IDemoModeService> _demoMode = new();
    private readonly EntryReadService _sut;

    private static readonly DateTime Now = new(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    public EntryReadServiceFindFilterTests()
    {
        _demoMode.Setup(d => d.IsEnabled).Returns(false);
        _sut = new EntryReadService(
            _sgRepo.Object,
            _mgRepo.Object,
            _calRepo.Object,
            TestDoubles.CanonicalGlucosePassThrough.Create(),
            _demoMode.Object,
            Mock.Of<ILogger<EntryReadService>>());

        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(),
                It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());
        _mgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MeterGlucose>());
        _calRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Calibration>());
    }

    private void SetupSg(params SensorGlucose[] readings)
    {
        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(),
                It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(readings);
    }

    [Fact]
    public async Task QueryAsync_FindTypeEquality_RoutesToSingleRepo()
    {
        // find[type]=sgv must not fan out to meter/calibration repos
        SetupSg(MakeSg(Now, 120));

        var result = await _sut.QueryAsync(new EntryQuery { Find = "find[type]=sgv", Count = 10 });

        result.Should().ContainSingle().Which.Type.Should().Be("sgv");
        _mgRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _calRepo.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueryAsync_TypeNeCal_ExcludesCalibrations()
    {
        // LoopFollow entries poll: find[type][$ne]=cal
        SetupSg(MakeSg(Now, 120));
        _calRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCal(Now.AddMinutes(-1)) });

        var result = await _sut.QueryAsync(new EntryQuery { Find = "find[type][$ne]=cal", Count = 10 });

        result.Should().ContainSingle().Which.Type.Should().Be("sgv");
    }

    [Fact]
    public async Task QueryAsync_SgvThresholdFilter_MatchesNumerically()
    {
        SetupSg(
            MakeSg(Now, 200),
            MakeSg(Now.AddMinutes(-5), 150),
            MakeSg(Now.AddMinutes(-10), 190));

        var result = await _sut.QueryAsync(new EntryQuery
        {
            Find = "find[type]=sgv&find[sgv][$gte]=180",
            Count = 10,
        });

        result.Should().HaveCount(2);
        result.Should().OnlyContain(e => e.Sgv >= 180);
    }

    [Fact]
    public async Task QueryAsync_DeviceNeFilter_ExcludesDevice()
    {
        var other = MakeSg(Now, 120);
        other.Device = "share2";

        SetupSg(other, MakeSg(Now.AddMinutes(-5), 130));

        var result = await _sut.QueryAsync(new EntryQuery
        {
            Find = "find[type]=sgv&find[device][$ne]=share2",
            Count = 10,
        });

        result.Should().ContainSingle().Which.Device.Should().Be("test-device");
    }

    [Fact]
    public async Task CountAsync_FindTypeEquality_CountsSingleRepo()
    {
        _sgRepo.Setup(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);

        var count = await _sut.CountAsync(find: "find[type]=sgv");

        count.Should().Be(7);
        _mgRepo.Verify(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static SensorGlucose MakeSg(DateTime ts, double mgdl) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = ts,
        Mgdl = mgdl,
        Device = "test-device",
        CreatedAt = ts,
        ModifiedAt = ts,
    };

    private static Calibration MakeCal(DateTime ts) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = ts,
        Slope = 1000,
        Intercept = 25000,
        Scale = 1,
        Device = "test-cal",
        CreatedAt = ts,
        ModifiedAt = ts,
    };
}
