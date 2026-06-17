using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Glucose;

public class ProfileSnapshotServiceTests
{
    private const long Hour = 3_600_000L;
    private const long Day = 24 * Hour;

    private static readonly DateTimeOffset Midnight = new(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<ITherapyTimelineResolver> _timeline = new();
    private readonly Mock<ITargetRangeResolver> _targetRange = new();
    private readonly Mock<ITherapySettingsResolver> _therapySettings = new();
    private readonly Mock<IPatientInsulinRepository> _insulins = new();
    private readonly FakeTimeProvider _clock = new(Midnight);
    private readonly ProfileSnapshotService _sut;

    private readonly long _now = Midnight.ToUnixTimeMilliseconds();

    public ProfileSnapshotServiceTests()
    {
        _therapySettings.Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _targetRange.Setup(r => r.GetLowBGTargetAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(100.0);
        _targetRange.Setup(r => r.GetHighBGTargetAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(120.0);
        _insulins.Setup(r => r.GetPrimaryBolusInsulinAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PatientInsulin { Peak = 60, Curve = "ultra-rapid", Dia = 5.0 });

        _sut = new ProfileSnapshotService(
            _timeline.Object,
            _targetRange.Object,
            _therapySettings.Object,
            _insulins.Object,
            _clock,
            NullLogger<ProfileSnapshotService>.Instance);
    }

    private static ScheduleEntry Entry(int seconds, double value) => new() { TimeAsSeconds = seconds, Value = value };

    private static TherapySnapshot Snapshot(
        double dia = 5.0,
        double? ccpPercentage = null,
        long ccpTimeshiftMs = 0,
        TimeZoneInfo? timezone = null,
        IEnumerable<ScheduleEntry>? sens = null,
        IEnumerable<ScheduleEntry>? carb = null,
        IEnumerable<ScheduleEntry>? basal = null) =>
        new(dia, 75, 30.0, timezone, ccpPercentage, ccpTimeshiftMs,
            sens ?? new[] { Entry(0, 50.0) },
            carb ?? new[] { Entry(0, 10.0) },
            basal ?? new[] { Entry(0, 1.0) });

    private void GivenTimeline(params TherapySegment[] segments) =>
        _timeline.Setup(r => r.BuildAsync(_now, _now + Day, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TherapyTimeline(segments));

    [Fact]
    public async Task FlatProfile_EmitsSingleSegmentSpanningTheWindow()
    {
        GivenTimeline(new TherapySegment(_now, _now + Day, Snapshot()));

        var result = await _sut.BuildAsync(null);

        result.FetchedAtMills.Should().Be(_now);
        result.Segments.Should().ContainSingle();
        var seg = result.Segments[0];
        seg.StartMills.Should().Be(_now);
        seg.EndMills.Should().Be(_now + Day);
        seg.Dia.Should().Be(5.0);
        seg.Sens.Should().Be(50.0);
        seg.CarbRatio.Should().Be(10.0);
        seg.Basal.Should().Be(1.0);
        seg.MinBg.Should().Be(100.0);
        seg.MaxBg.Should().Be(120.0);
        seg.Peak.Should().Be(60);
        seg.Curve.Should().Be("ultra-rapid");
    }

    [Fact]
    public async Task IntraDayBasalChange_CoalescesToDistinctStatesNotEveryMinute()
    {
        var basal = new[] { Entry(0, 0.8), Entry(6 * 3600, 1.0) };
        GivenTimeline(new TherapySegment(_now, _now + Day, Snapshot(basal: basal)));

        var result = await _sut.BuildAsync(null);

        result.Segments.Should().HaveCount(2);
        result.Segments[0].StartMills.Should().Be(_now);
        result.Segments[0].EndMills.Should().Be(_now + 6 * Hour);
        result.Segments[0].Basal.Should().Be(0.8);
        result.Segments[1].StartMills.Should().Be(_now + 6 * Hour);
        result.Segments[1].EndMills.Should().Be(_now + Day);
        result.Segments[1].Basal.Should().Be(1.0);
    }

    [Fact]
    public async Task SegmentsAreContiguousAndCoverTheFullWindow()
    {
        var basal = new[] { Entry(0, 0.8), Entry(6 * 3600, 1.0), Entry(18 * 3600, 0.6) };
        var sens = new[] { Entry(0, 45.0), Entry(12 * 3600, 55.0) };
        GivenTimeline(new TherapySegment(_now, _now + Day, Snapshot(sens: sens, basal: basal)));

        var result = await _sut.BuildAsync(null);

        result.Segments[0].StartMills.Should().Be(_now);
        result.Segments[^1].EndMills.Should().Be(_now + Day);
        for (var i = 1; i < result.Segments.Count; i++)
            result.Segments[i].StartMills.Should().Be(result.Segments[i - 1].EndMills);
    }

    [Fact]
    public async Task CcpActive_ScalesSensCarbInverseAndBasalForward_ButNotTargets()
    {
        GivenTimeline(new TherapySegment(_now, _now + Day, Snapshot(ccpPercentage: 200)));

        var result = await _sut.BuildAsync(null);

        var seg = result.Segments.Should().ContainSingle().Subject;
        seg.Sens.Should().Be(25.0);
        seg.CarbRatio.Should().Be(5.0);
        seg.Basal.Should().Be(2.0);
        seg.MinBg.Should().Be(100.0);
        seg.MaxBg.Should().Be(120.0);
    }

    [Fact]
    public async Task ProfileSwitchInWindow_ProducesGroupsWithDistinctTargetsPerSide()
    {
        var split = _now + 8 * Hour;
        _timeline.Setup(r => r.BuildAsync(_now, _now + Day, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TherapyTimeline(new[]
            {
                new TherapySegment(_now, split, Snapshot()),
                new TherapySegment(split, _now + Day, Snapshot()),
            }));
        _targetRange.Setup(r => r.GetLowBGTargetAsync(It.Is<long>(t => t >= split), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(110.0);

        var result = await _sut.BuildAsync(null);

        result.Segments.Should().HaveCount(2);
        result.Segments[0].MinBg.Should().Be(100.0);
        result.Segments[1].MinBg.Should().Be(110.0);
        result.Segments[1].StartMills.Should().Be(split);
    }

    [Fact]
    public async Task NoData_EmitsSingleDefaultSegmentMatchingPredictionServiceDefaults()
    {
        _therapySettings.Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _insulins.Setup(r => r.GetPrimaryBolusInsulinAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientInsulin?)null);

        var result = await _sut.BuildAsync(null);

        var seg = result.Segments.Should().ContainSingle().Subject;
        seg.StartMills.Should().Be(_now);
        seg.EndMills.Should().Be(_now + Day);
        seg.Dia.Should().Be(3.0);
        seg.Basal.Should().Be(1.0);
        seg.Sens.Should().Be(50.0);
        seg.CarbRatio.Should().Be(10.0);
        seg.MinBg.Should().Be(100.0);
        seg.MaxBg.Should().Be(120.0);
        seg.Peak.Should().Be(75);
        seg.Curve.Should().Be("rapid-acting");
        _timeline.Verify(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoData_StillTakesDiaFromPrimaryBolusInsulinWhenPresent()
    {
        _therapySettings.Setup(r => r.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _sut.BuildAsync(null);

        result.Segments.Should().ContainSingle().Which.Dia.Should().Be(5.0);
    }

    [Fact]
    public async Task ResolverFault_PropagatesAndDoesNotEmitDefaults()
    {
        _timeline.Setup(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => _sut.BuildAsync(null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void ResponseSerializesSnakeCaseAndEmitsZeroScalars()
    {
        var response = new ProfileSnapshotResponse
        {
            FetchedAtMills = _now,
            Segments = { new ProfileSnapshotSegment { MinBg = 0.0, Curve = "rapid-acting" } },
        };

        var json = JsonSerializer.Serialize(response);

        json.Should().Contain("\"fetched_at_mills\"");
        json.Should().Contain("\"segments\"");
        json.Should().Contain("\"start_mills\"");
        json.Should().Contain("\"end_mills\"");
        json.Should().Contain("\"carb_ratio\"");
        json.Should().Contain("\"min_bg\":0");
        json.Should().Contain("\"max_bg\":0");
        json.Should().Contain("\"peak\":0");
    }
}
