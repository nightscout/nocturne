using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Health;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Health;

/// <summary>
/// Pins how <see cref="ActivityService.GetActivitiesAsync"/> reads a legacy find: time bounds narrow
/// every source's read, and any other condition is evaluated per merged record.
/// </summary>
[Trait("Category", "Unit")]
public class ActivityServiceFindTests
{
    private static readonly DateTime Day1 = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IStateSpanService> _stateSpans = new();
    private readonly Mock<ISleepService> _sleep = new();
    private readonly Mock<IHeartRateService> _heartRates = new();
    private readonly Mock<IStepCountService> _stepCounts = new();
    private readonly ActivityService _service;

    private List<Activity> _stored = [];
    private List<HeartRate> _storedHeartRates = [];
    private List<StepCount> _storedStepCounts = [];
    private List<SleepSession> _storedSleep = [];
    private int _stateSpanFetchCount;

    public ActivityServiceFindTests()
    {
        _stateSpans
            .Setup(s => s.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int count, int _, DateTime? from, DateTime? to, CancellationToken _) =>
            {
                _stateSpanFetchCount = count;
                return (IEnumerable<Activity>)_stored
                    .Where(a => (from is null || Utc(a) >= from) && (to is null || Utc(a) <= to))
                    .OrderByDescending(a => a.Mills)
                    .Take(count)
                    .ToList();
            });
        _heartRates
            .Setup(s => s.GetHeartRatesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, int _, DateTime? from, DateTime? to, CancellationToken _) =>
                (IEnumerable<HeartRate>)_storedHeartRates
                    .Where(h => (from is null || h.Timestamp >= from) && (to is null || h.Timestamp <= to))
                    .ToList());
        _stepCounts
            .Setup(s => s.GetStepCountsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, int _, DateTime? from, DateTime? to, CancellationToken _) =>
                (IEnumerable<StepCount>)_storedStepCounts
                    .Where(c => (from is null || c.Timestamp >= from) && (to is null || c.Timestamp <= to))
                    .ToList());
        _sleep
            .Setup(s => s.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                    DateTime? from, DateTime? to, SleepSessionType? _, SleepSource? _, int _, int _, bool _,
                    bool _, CancellationToken _) =>
                (IEnumerable<SleepSession>)_storedSleep
                    .Where(x => (from is null || x.EndTime >= from) && (to is null || x.StartTime <= to))
                    .ToList());

        _service = new ActivityService(
            _stateSpans.Object,
            _sleep.Object,
            Mock.Of<IDocumentProcessingService>(),
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IDataEventSink<Activity>>(),
            Mock.Of<IActivityDecomposer>(),
            _heartRates.Object,
            _stepCounts.Object,
            Mock.Of<ILogger<ActivityService>>());
    }

    [Fact]
    public async Task CreatedAtBounds_NarrowEverySourceAndTheResult()
    {
        _stored = [Run("before", Day1.AddDays(-2)), Run("d1", Day1), Run("d2", Day2)];
        var from = Day1;
        var to = Day1.AddHours(12);
        var find = $"find[created_at][$gte]={from:O}&find[created_at][$lte]={to:O}";

        var result = await _service.GetActivitiesAsync(find: find, count: 10);

        result.Select(a => a.Id).Should().Equal("d1");
        _stateSpans.Verify(s => s.GetActivitiesAsync(
            It.IsAny<int>(), 0, from, to, It.IsAny<CancellationToken>()), Times.Once);
        _heartRates.Verify(s => s.GetHeartRatesAsync(
            It.IsAny<int>(), 0, from, to, It.IsAny<CancellationToken>()), Times.Once);
        _stepCounts.Verify(s => s.GetStepCountsAsync(
            It.IsAny<int>(), 0, from, to, It.IsAny<CancellationToken>()), Times.Once);
        _sleep.Verify(s => s.GetSessionsAsync(
            from, to, It.IsAny<SleepSessionType?>(), It.IsAny<SleepSource?>(), It.IsAny<int>(), 0,
            true, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FieldFilter_NarrowsTheMergedResultBeyondThePage()
    {
        _stored = Enumerable.Range(0, 30)
            .Select(i => Run($"run{i}", Day2.AddMinutes(-i), type: "Run"))
            .Concat(Enumerable.Range(0, 3)
                .Select(i => Run($"swim{i}", Day1.AddMinutes(-i), type: "Swim")))
            .ToList();

        var result = await _service.GetActivitiesAsync(find: "find[type][$eq]=Swim", count: 2);

        result.Select(a => a.Id).Should().Equal("swim0", "swim1");
        _stateSpanFetchCount.Should().BeGreaterThan(32, "the filter runs after the fetch");
    }

    [Fact]
    public async Task FieldFilterOnATimeBoundedFind_ReturnsTheBoundedSetNarrowedByTheField()
    {
        _stored =
        [
            Run("run", Day1, type: "Run", createdAt: Day2.AddDays(30)),
            Run("swim", Day1.AddHours(1), type: "Swim", createdAt: Day2.AddDays(30)),
            Run("other-day", Day2, type: "Swim", createdAt: Day2.AddDays(30)),
        ];
        var bounds = $"find[created_at][$gte]={Day1:O}&find[created_at][$lte]={Day1.AddHours(12):O}";

        var bounded = await _service.GetActivitiesAsync(find: bounds, count: 10);
        var narrowed = await _service.GetActivitiesAsync(find: bounds + "&find[type][$eq]=Swim", count: 10);

        bounded.Select(a => a.Id).Should().BeEquivalentTo("run", "swim");
        narrowed.Select(a => a.Id).Should().Equal("swim");
    }

    [Fact]
    public async Task SleepOverlappingTheLowerBound_IsBoundedByItsStartLikeEverySource()
    {
        var from = Day1;
        var started = new SleepSession
        {
            Id = Guid.NewGuid().ToString(),
            StartTime = from.AddHours(-3),
            EndTime = from.AddHours(5),
        };
        _sleep
            .Setup(s => s.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([started]);

        var result = await _service.GetActivitiesAsync(
            find: $"find[created_at][$gte]={from:O}", count: 10);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SleepStartingAfterTheUpperBound_IsExcluded()
    {
        var to = Day1.AddHours(12);
        SleepSession Session(string id, DateTime start) => new()
        {
            Id = id,
            StartTime = start,
            EndTime = start.AddHours(8),
        };
        _sleep
            .Setup(s => s.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Session("inside", to.AddHours(-2)), Session("after", to.AddHours(2))]);

        var result = await _service.GetActivitiesAsync(
            find: $"find[created_at][$gte]={Day1:O}&find[created_at][$lte]={to:O}", count: 10);

        result.Select(a => a.Id).Should().Equal("inside");
    }

    [Fact]
    public async Task NoFieldFilter_PushesThePageDownToTheSources()
    {
        _stored = [Run("d1", Day1)];

        await _service.GetActivitiesAsync(
            find: $"find[created_at][$gte]={Day1:O}", count: 5, skip: 3);

        _stateSpanFetchCount.Should().Be(8);
    }

    [Fact]
    public async Task ExplicitWindow_ReturnsOnlyRecordsStartingInsideItFromEverySource()
    {
        var from = Day1;
        var to = Day1.AddHours(12);
        var inside = Day1.AddHours(6);
        var outside = Day1.AddDays(-2);
        _stored = [Run("span-in", inside), Run("span-out", outside)];
        _storedHeartRates =
        [
            new HeartRate { Id = "hr-in", Timestamp = inside },
            new HeartRate { Id = "hr-out", Timestamp = outside },
        ];
        _storedStepCounts =
        [
            new StepCount { Id = "steps-in", Timestamp = inside },
            new StepCount { Id = "steps-out", Timestamp = outside },
        ];
        _storedSleep =
        [
            new SleepSession { Id = "sleep-in", StartTime = inside, EndTime = inside.AddHours(2) },
            new SleepSession { Id = "sleep-out", StartTime = outside, EndTime = outside.AddHours(8) },
            new SleepSession { Id = "sleep-overlaps", StartTime = from.AddHours(-3), EndTime = from.AddHours(5) },
        ];

        var result = await _service.GetActivitiesAsync(count: 20, from: from, to: to);

        result.Select(a => a.Id).Should().BeEquivalentTo("span-in", "hr-in", "steps-in", "sleep-in");
        _stateSpans.Verify(s => s.GetActivitiesAsync(
            It.IsAny<int>(), 0, from, to, It.IsAny<CancellationToken>()), Times.Once);
        _heartRates.Verify(s => s.GetHeartRatesAsync(
            It.IsAny<int>(), 0, from, to, It.IsAny<CancellationToken>()), Times.Once);
        _stepCounts.Verify(s => s.GetStepCountsAsync(
            It.IsAny<int>(), 0, from, to, It.IsAny<CancellationToken>()), Times.Once);
        _sleep.Verify(s => s.GetSessionsAsync(
            from, to, It.IsAny<SleepSessionType?>(), It.IsAny<SleepSource?>(), It.IsAny<int>(), 0,
            true, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A client that has read records to the millisecond steps its upper bound to the end of the
    /// oldest one's millisecond, so the bound has to admit a record stored later in that millisecond,
    /// and the one at the bound itself.
    /// </summary>
    [Fact]
    public async Task ExplicitWindow_UpperBoundIsInclusiveToTheMicrosecond()
    {
        var to = Day1.AddTicks(9_990);
        _storedSleep =
        [
            new SleepSession { Id = "later-in-the-millisecond", StartTime = Day1.AddTicks(7_000), EndTime = Day1.AddHours(2) },
            new SleepSession { Id = "at-the-bound", StartTime = to, EndTime = Day1.AddHours(2) },
            new SleepSession { Id = "next-millisecond", StartTime = Day1.AddMilliseconds(1), EndTime = Day1.AddHours(2) },
        ];

        var result = await _service.GetActivitiesAsync(count: 10, from: Day1.AddHours(-1), to: to);

        result.Select(a => a.Id).Should().BeEquivalentTo("later-in-the-millisecond", "at-the-bound");
    }

    [Fact]
    public async Task ExplicitWindow_NarrowsAFindsOwnBoundsOnEachSide()
    {
        _stored =
        [
            Run("before-both", Day1.AddHours(-5)),
            Run("between", Day1.AddHours(2)),
            Run("inside", Day1.AddHours(6)),
            Run("after-both", Day1.AddHours(20)),
        ];
        var find = $"find[created_at][$gte]={Day1.AddHours(1):O}&find[created_at][$lte]={Day1.AddHours(10):O}";

        var result = await _service.GetActivitiesAsync(
            find: find, count: 10, from: Day1.AddHours(4), to: Day1.AddHours(18));

        result.Select(a => a.Id).Should().Equal("inside");
        _heartRates.Verify(s => s.GetHeartRatesAsync(
            It.IsAny<int>(), 0, Day1.AddHours(4), Day1.AddHours(10), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExplicitWindow_CountsOnlyRecordsInsideItInEverySource()
    {
        var inside = Day1.AddHours(6);
        var outside = Day1.AddDays(-2);
        _stored = [Run("span-in", inside), Run("span-out", outside)];
        _storedHeartRates = [new HeartRate { Timestamp = inside }, new HeartRate { Timestamp = outside }];
        _storedStepCounts = [new StepCount { Timestamp = inside }, new StepCount { Timestamp = outside }];
        _storedSleep =
        [
            new SleepSession { Id = "sleep-in", StartTime = inside, EndTime = inside.AddHours(2) },
            new SleepSession { Id = "sleep-out", StartTime = outside, EndTime = outside.AddHours(8) },
        ];
        var categories = new HashSet<string>
        {
            Nocturne.Core.Models.Authorization.Scope.TreatmentsRead,
            Nocturne.Core.Models.Authorization.Scope.HeartRateRead,
            Nocturne.Core.Models.Authorization.Scope.StepCountRead,
            Nocturne.Core.Models.Authorization.Scope.SleepRead,
        };

        var counts = await _service.CountActivitiesByCategoryAsync(
            categories, from: Day1, to: Day1.AddHours(12));

        counts.Values.Should().OnlyContain(c => c == 1);
        counts.Should().HaveCount(4);
    }

    private static DateTime Utc(Activity a) =>
        DateTimeOffset.FromUnixTimeMilliseconds(a.Mills).UtcDateTime;

    private static Activity Run(string id, DateTime at, string type = "Run", DateTime? createdAt = null) => new()
    {
        Id = id,
        Type = type,
        Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
        CreatedAt = (createdAt ?? at).ToString("O"),
    };
}
