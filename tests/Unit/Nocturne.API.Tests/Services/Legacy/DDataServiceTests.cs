using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Legacy;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Legacy;

/// <summary>
/// Tests for DDataService with 1:1 legacy compatibility
/// </summary>
[Parity("ddata.test.js")]
public class DDataServiceTests
{
    private readonly Mock<IEntryService> _mockEntryService;
    private readonly Mock<ITreatmentService> _mockTreatmentService;
    private readonly Mock<IProfileProjectionService> _mockProfileProjectionService;
    private readonly Mock<IDeviceStatusService> _mockDeviceStatusService;
    private readonly Mock<IFoodRepository> _mockFoodRepository;
    private readonly Mock<IActivityService> _mockActivityService;
    private readonly Mock<IStoreSizeReader> _mockStoreSize;
    private readonly Mock<ILogger<DDataService>> _mockLogger;
    private readonly DDataService _ddataService;

    public DDataServiceTests()
    {
        _mockEntryService = new Mock<IEntryService>();
        _mockTreatmentService = new Mock<ITreatmentService>();
        _mockProfileProjectionService = new Mock<IProfileProjectionService>();
        _mockFoodRepository = new Mock<IFoodRepository>();
        _mockActivityService = new Mock<IActivityService>();
        _mockStoreSize = new Mock<IStoreSizeReader>();
        _mockStoreSize.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new DbStats());
        _mockLogger = new Mock<ILogger<DDataService>>();

        _mockDeviceStatusService = new Mock<IDeviceStatusService>();
        _mockDeviceStatusService
            .Setup(x => x.GetDeviceStatusesAsync(
                It.IsAny<FindQuery>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>(), It.IsAny<ReadSortField>()))
            .ReturnsAsync([]);

        _ddataService = new DDataService(
            _mockEntryService.Object,
            _mockTreatmentService.Object,
            _mockProfileProjectionService.Object,
            _mockDeviceStatusService.Object,
            _mockFoodRepository.Object,
            _mockActivityService.Object,
            _mockStoreSize.Object,
            TimeProvider.System, _mockLogger.Object
        );
    }

    [Fact]
    public async Task GetDDataAsync_ReportsTheStoreSizeReadersFigure()
    {
        var stats = new DbStats { DataSize = 7_000, IndexSize = 3_000, Collections = 4, Indexes = 9 };
        _mockStoreSize.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(stats);

        var result = await _ddataService.GetDDataAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), CancellationToken.None);

        Assert.Same(stats, result.DbStats);
    }

    [Fact]
    public async Task GetCurrentDDataAsync_ShouldReturnDDataStructure()
    { // Arrange
        _mockEntryService
            .Setup(x =>
                x.QueryEntriesAsync(
                    It.IsAny<EntryQuery>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Array.Empty<Entry>());
        _mockTreatmentService
            .Setup(x =>
                x.GetTreatmentsAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Array.Empty<Treatment>());
        _mockProfileProjectionService
            .Setup(x =>
                x.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(Array.Empty<Profile>());
        _mockFoodRepository
            .Setup(x => x.GetFoodAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Food>());
        _mockActivityService
            .Setup(x =>
                x.GetActivitiesAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Array.Empty<Activity>());

        // Act
        var result = await _ddataService.GetCurrentDDataAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Sgvs);
        Assert.NotNull(result.Treatments);
        Assert.NotNull(result.Mbgs);
        Assert.NotNull(result.Cals);
        Assert.NotNull(result.Profiles);
        Assert.NotNull(result.DeviceStatus);
        Assert.NotNull(result.Food);
        Assert.NotNull(result.Activity);
        Assert.NotNull(result.DbStats);
        Assert.True(result.LastUpdated > 0);
    }

    [Fact]
    public async Task GetDDataAsync_DeviceStatusIsReadFromTheDayEndingAtTheTimestamp()
    {
        var at = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

        await _ddataService.GetDDataAsync(at.ToUnixTimeMilliseconds(), CancellationToken.None);

        _mockDeviceStatusService.Verify(x => x.GetDeviceStatusesAsync(
            It.Is<FindQuery>(q => q.FromMills == at.AddDays(-1).ToUnixTimeMilliseconds() && q.ToMills == at.ToUnixTimeMilliseconds()),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), true, It.IsAny<ReadSortField>()));
    }

    [Fact]
    public async Task GetDDataFrameAsync_ServesSgvsOldestFirstWhereGetDDataAsyncKeepsTheStoreOrder()
    {
        _mockEntryService
            .Setup(x => x.QueryEntriesAsync(It.Is<EntryQuery>(q => q.Type == "sgv"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entry>
            {
                new() { Type = "sgv", Mills = 3000 },
                new() { Type = "sgv", Mills = 1000 },
                new() { Type = "sgv", Mills = 2000 },
            });

        var frame = await _ddataService.GetDDataFrameAsync(5000, CancellationToken.None);
        var ddata = await _ddataService.GetDDataAsync(5000, CancellationToken.None);

        Assert.Equal([1000L, 2000L, 3000L], frame.Sgvs.Select(e => e.Mills));
        Assert.Equal([3000L, 1000L, 2000L], ddata.Sgvs.Select(e => e.Mills));
    }

    [Fact]
    public async Task GetDDataFrameAsync_AnOlderTimestampPagesFromIt()
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();

        var frame = await _ddataService.GetDDataFrameAsync(at, CancellationToken.None);

        Assert.True(frame.Page!.Frame);
        Assert.Equal(at, frame.Page.After);
        Assert.Equal(at, frame.LastUpdated);
        Assert.Empty(frame.Entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(4)]
    public async Task GetDDataFrameAsync_ATimestampWithinFiveMinutesOfNowCarriesNoPage(int minutesFromNow)
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(minutesFromNow).ToUnixTimeMilliseconds();

        var frame = await _ddataService.GetDDataFrameAsync(at, CancellationToken.None);

        Assert.Null(frame.Page);
        Assert.DoesNotContain("\"page\"", JsonSerializer.Serialize(frame, NightscoutJsonOptions.Create()));
    }

    private const long Day = 24L * 60 * 60 * 1000;
    private const long Minute = 60_000;

    private void SeedStores(string type, IReadOnlyList<long> mills)
    {
        _mockEntryService
            .Setup(x => x.QueryEntriesAsync(It.Is<EntryQuery>(q => q.Type == type), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EntryQuery q, CancellationToken _) => mills
                .Where(m => m >= (q.FromMills ?? long.MinValue) && m <= (q.ToMills ?? long.MaxValue))
                .OrderByDescending(m => m)
                .Take(q.Count)
                .Select(m => new Entry { Type = type, Mills = m })
                .ToList());
    }

    private void SeedTreatments(params Treatment[] stored)
    {
        _mockTreatmentService
            .Setup(x => x.GetTreatmentsAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? find, int? count, int? _, CancellationToken _) =>
            {
                var bounds = FindQuery.Parse(find);
                return stored
                    .Where(t => t.Mills >= (bounds.FromMills ?? long.MinValue) && t.Mills <= (bounds.ToMills ?? long.MaxValue))
                    .OrderByDescending(t => t.Mills)
                    .Take(count ?? 10)
                    .ToList();
            });
        _mockTreatmentService
            .Setup(x => x.GetTreatmentsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int count, int _, CancellationToken _) => stored
                .OrderByDescending(t => t.Mills)
                .Take(count)
                .ToList());
        _mockTreatmentService
            .Setup(x => x.GetLatestTreatmentOfTypeAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string eventType, long from, long to, CancellationToken _) => stored
                .Where(t => t.EventType == eventType && t.Mills >= from && t.Mills <= to)
                .Where(t => eventType != "Profile Switch" || t.Duration == 0)
                .OrderByDescending(t => t.Mills)
                .FirstOrDefault());
    }

    private static Treatment Bolus(long mills) =>
        new() { Id = $"bolus-{mills}", EventType = "Correction Bolus", Mills = mills };

    [Fact]
    public async Task GetDDataFrameAsync_APastTimeServesTheReadingsOfItsTwoDayWindow()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var at = now - 5 * Day;
        long[] readings =
        [
            at - 2 * Day - Minute,
            at - 9 * Day / 4,
            at - 2 * Day + Minute,
            at - Day / 2,
            at + Minute,
            now - Day,
        ];
        SeedStores("sgv", readings);
        SeedStores("mbg", readings);
        SeedStores("cal", readings);

        var frame = await _ddataService.GetDDataFrameAsync(at, CancellationToken.None);

        long[] served = [at - 2 * Day + Minute, at - Day / 2];
        Assert.Equal(served, frame.Sgvs.Select(e => e.Mills));
        Assert.Equal(served, frame.Mbgs.Select(e => e.Mills).OrderBy(m => m));
        Assert.Equal(served, frame.Cals.Select(e => e.Mills).OrderBy(m => m));
    }

    [Fact]
    public async Task GetDDataFrameAsync_APastTimeServesTheTreatmentsOfItsTwoAndAHalfDayWindow()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var at = now - 5 * Day;
        long[] inside = [at - 5 * Day / 2 + Minute, at - 9 * Day / 4, at - Minute];
        SeedTreatments(
            Bolus(at - 5 * Day / 2 - Minute),
            Bolus(inside[0]),
            Bolus(inside[1]),
            Bolus(inside[2]),
            Bolus(at + Minute),
            Bolus(now - Day));

        var frame = await _ddataService.GetDDataFrameAsync(at, CancellationToken.None);

        Assert.Equal(inside.OrderByDescending(m => m), frame.Treatments.Select(t => t.Mills));
    }

    [Fact]
    public async Task GetDDataFrameAsync_ALiveTimeServesTheNewestReadingsAndTreatments()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SeedStores("sgv", [now - 2 * Day - Minute, now - 2 * Day + Minute, now - 60_000]);
        SeedTreatments(Bolus(now - 5 * Day / 2 - Minute), Bolus(now - 5 * Day / 2 + Minute));

        var frame = await _ddataService.GetDDataFrameAsync(now, CancellationToken.None);

        Assert.Equal([now - 2 * Day + Minute, now - 60_000], frame.Sgvs.Select(e => e.Mills));
        Assert.Equal([now - 5 * Day / 2 + Minute], frame.Treatments.Select(t => t.Mills));
    }

    [Fact]
    public async Task GetDDataFrameAsync_CarriesTheLatestProfileSwitchAndEventsOlderThanTheTreatmentWindow()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var at = now - 5 * Day;
        var profileSwitch = new Treatment
        {
            Id = "switch", EventType = "Profile Switch", Mills = at - 90 * Day, Duration = 0, Profile = "Weekday",
        };
        var olderSwitch = new Treatment
        {
            Id = "older-switch", EventType = "Profile Switch", Mills = at - 100 * Day, Duration = 0, Profile = "Old",
        };
        var siteChange = new Treatment { Id = "site", EventType = "Site Change", Mills = at - 20 * Day };
        var olderSiteChange = new Treatment { Id = "older-site", EventType = "Site Change", Mills = at - 25 * Day };
        var afterAt = new Treatment { Id = "after", EventType = "Site Change", Mills = at + Minute };
        SeedTreatments(profileSwitch, olderSwitch, siteChange, olderSiteChange, afterAt, Bolus(at - Day));

        var frame = await _ddataService.GetDDataFrameAsync(at, CancellationToken.None);

        Assert.Equal(["bolus-" + (at - Day), "site", "switch"], frame.Treatments.Select(t => t.Id));
        Assert.Equal([siteChange.Id], frame.SiteChangeTreatments.Select(t => t.Id));
        Assert.Equal("Weekday", frame.LastProfileFromSwitch);
    }

    [Theory]
    [InlineData("Sensor Start", -1, true)]
    [InlineData("Sensor Start", 1, false)]
    [InlineData("Sensor Change", -1, true)]
    [InlineData("Sensor Change", 1, false)]
    [InlineData("Sensor Stop", -1, true)]
    [InlineData("Sensor Stop", 1, false)]
    [InlineData("Site Change", -1, true)]
    [InlineData("Site Change", 1, false)]
    [InlineData("Insulin Change", -1, true)]
    [InlineData("Insulin Change", 1, false)]
    [InlineData("Pump Battery Change", -1, true)]
    [InlineData("Pump Battery Change", 1, false)]
    public async Task GetDDataFrameAsync_ALatestEventIsServedFromTheLastSixtyTwoDays(
        string eventType, int minutesPastTheBound, bool served)
    {
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5 * Day;
        SeedTreatments(new Treatment { Id = "event", EventType = eventType, Mills = at - 62 * Day - minutesPastTheBound * Minute });

        var frame = await _ddataService.GetDDataFrameAsync(at, CancellationToken.None);

        Assert.Equal(served, frame.Treatments.Any(t => t.Id == "event"));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public async Task GetDDataFrameAsync_AProfileSwitchIsServedFromTheLastThreeHundredAndSeventyTwoDays(
        int minutesPastTheBound, bool served)
    {
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5 * Day;
        SeedTreatments(new Treatment
        {
            Id = "switch",
            EventType = "Profile Switch",
            Mills = at - 372 * Day - minutesPastTheBound * Minute,
            Duration = 0,
            Profile = "Weekday",
        });

        var frame = await _ddataService.GetDDataFrameAsync(at, CancellationToken.None);

        Assert.Equal(served, frame.Treatments.Any(t => t.Id == "switch"));
        Assert.Equal(served ? "Weekday" : null, frame.LastProfileFromSwitch);
    }

    [Fact]
    public async Task GetDDataFrameAsync_ATimedProfileSwitchIsNotTheLatestSwitch()
    {
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5 * Day;
        var timed = new Treatment { Id = "timed", EventType = "Profile Switch", Mills = at - 10 * Day, Duration = 30 };
        SeedTreatments(timed);

        var frame = await _ddataService.GetDDataFrameAsync(at, CancellationToken.None);

        Assert.Empty(frame.Treatments);
    }

    [Fact]
    public async Task GetDDataFrameAsync_AFailingLatestReadLeavesTheWindowedTreatments()
    {
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5 * Day;
        SeedTreatments(Bolus(at - Day), new Treatment { Id = "site", EventType = "Site Change", Mills = at - 20 * Day });
        _mockTreatmentService
            .Setup(x => x.GetLatestTreatmentOfTypeAsync("Sensor Start", It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException());

        var frame = await _ddataService.GetDDataFrameAsync(at, CancellationToken.None);

        Assert.Equal(["bolus-" + (at - Day), "site"], frame.Treatments.Select(t => t.Id));
    }

    [Fact]
    public async Task GetDDataAsync_ReadsTheNewestRowsWhateverTheTimestamp()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long[] readings = [now - 6 * Day, now - Day];
        SeedStores("sgv", readings);
        SeedTreatments(readings.Select(Bolus).ToArray());

        var ddata = await _ddataService.GetDDataAsync(now - 5 * Day, CancellationToken.None);

        Assert.Equal(readings.OrderByDescending(m => m), ddata.Sgvs.Select(e => e.Mills));
        Assert.Equal(readings.OrderByDescending(m => m), ddata.Treatments.Select(t => t.Mills));
    }

    [Fact]
    public async Task GetDDataFrameAsync_SerialisesTheFrameFieldsNightscoutSends()
    {
        var at = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        var frame = await _ddataService.GetDDataFrameAsync(at, CancellationToken.None);

        var json = JsonSerializer.SerializeToElement(frame, NightscoutJsonOptions.Create());

        Assert.Equal(JsonValueKind.Array, json.GetProperty("entries").ValueKind);
        Assert.Equal(0, json.GetProperty("entries").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("lastProfileFromSwitch").ValueKind);
        Assert.Equal(0, json.GetProperty("dbstats").GetProperty("dataSize").GetInt64());
        Assert.Equal(0, json.GetProperty("dbstats").GetProperty("indexSize").GetInt64());
        Assert.True(json.GetProperty("page").GetProperty("frame").GetBoolean());
        Assert.Equal(at, json.GetProperty("page").GetProperty("after").GetInt64());
    }

    [Fact]
    public void ProcessDurations_ShouldRemoveDuplicatesByMills()
    {
        // Arrange
        var treatments = new List<Treatment>
        {
            new() { Mills = 1000, Duration = 30 },
            new() { Mills = 1000, Duration = 45 }, // Duplicate mills
            new() { Mills = 2000, Duration = 60 },
        };

        // Act
        var result = _ddataService.ProcessDurations(treatments, true);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(1000, result[0].Mills);
        Assert.Equal(2000, result[1].Mills);
    }

    [Fact]
    public void ProcessDurations_ShouldCutOverlappingDurations()
    {
        // Arrange
        var treatments = new List<Treatment>
        {
            new() { Mills = 1000, Duration = 60 }, // 1000-61000 (60 minutes)
            new() { Mills = 30000, Duration = 0 }, // End event at 30000
        };

        // Act
        var result = _ddataService.ProcessDurations(treatments, false);

        // Assert
        var baseTreatment = result.FirstOrDefault(t => t.Mills == 1000);
        Assert.NotNull(baseTreatment);
        Assert.True(baseTreatment.Duration < 60); // Should be cut by end event
    }

    [Fact]
    public void ConvertTempTargetUnits_ShouldConvertMmolToMgdl()
    {
        // Arrange
        var treatments = new List<Treatment>
        {
            new()
            {
                TargetTop = 10.0,
                TargetBottom = 5.0,
                Units = "mmol",
            },
            new() { TargetTop = 15.0, TargetBottom = 8.0 }, // Below 20, should be converted
        };

        // Act
        var result = _ddataService.ConvertTempTargetUnits(treatments);

        // Assert
        Assert.All(
            result,
            t =>
            {
                Assert.True(t.TargetTop > 20); // Should be converted to mg/dl
                Assert.True(t.TargetBottom > 20);
                Assert.Equal("mg/dl", t.Units);
            }
        );
    }

    [Fact]
    public void GetRecentDeviceStatus_ShouldReturnRecentStatuses()
    {
        // Arrange
        var deviceStatuses = new List<DeviceStatus>
        {
            new()
            {
                Id = "1",
                Device = "test",
                Mills = 1000,
                Pump = new PumpStatus(),
            },
            new()
            {
                Id = "2",
                Device = "test",
                Mills = 2000,
                Pump = new PumpStatus(),
            },
            new()
            {
                Id = "3",
                Device = "test",
                Mills = 3000,
                Uploader = new UploaderStatus(),
            },
        };

        // Act
        var result = _ddataService.GetRecentDeviceStatus(deviceStatuses, 2500);

        // Assert
        Assert.NotEmpty(result);
        Assert.All(result, ds => Assert.True(ds.Mills <= 2500));
    }

    [Fact]
    public void ProcessTreatments_ShouldPopulateLastProfileFromSwitch()
    {
        // Arrange - lastProfileFromSwitch must be computed from profile treatments
        // cgm-remote-monitor finds the latest zero-duration Profile Switch before current time
        var treatments = new List<Treatment>
        {
            new()
            {
                EventType = "Profile Switch",
                Mills = 1000,
                Duration = 0,
                Profile = "OldProfile",
            },
            new()
            {
                EventType = "Profile Switch",
                Mills = 3000,
                Duration = 0,
                Profile = "CurrentProfile",
            },
            new()
            {
                EventType = "Profile Switch",
                Mills = 2000,
                Duration = 60,
                Profile = "TimedProfile",
            },
            new()
            {
                EventType = "Meal Bolus",
                Mills = 2500,
                Duration = 0,
            },
        };

        // Act
        var result = _ddataService.ProcessTreatments(treatments, false);

        // Assert - should pick the latest zero-duration Profile Switch
        Assert.Equal("CurrentProfile", result.LastProfileFromSwitch);
    }

    [Fact]
    public void ProcessTreatments_ShouldReturnNullLastProfileFromSwitch_WhenNoProfileSwitches()
    {
        // Arrange
        var treatments = new List<Treatment>
        {
            new()
            {
                EventType = "Meal Bolus",
                Mills = 1000,
            },
        };

        // Act
        var result = _ddataService.ProcessTreatments(treatments, false);

        // Assert
        Assert.Null(result.LastProfileFromSwitch);
    }

    /// <summary>
    /// The copies <c>preserveOriginalTreatments</c> works on are JSON clones, and the serialized
    /// form carries a uuid id only as its 24-hex wire prefix. The copies must keep the full stored
    /// id, or nothing downstream can match them back to their records.
    /// </summary>
    [Fact]
    public void ProcessTreatments_PreservingOriginals_CopiesKeepTheStoredIdAndLeaveOriginalsUntouched()
    {
        var profileId = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f";
        var tempBasalId = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e60";
        var tempTargetId = "0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e61";
        var tempTarget = new Treatment
        {
            Id = tempTargetId,
            EventType = "Temporary Target",
            Mills = 3000,
            Duration = 30,
            Units = "mmol",
            TargetTop = 8,
            TargetBottom = 6,
        };
        var treatments = new List<Treatment>
        {
            new() { Id = profileId, EventType = "Profile Switch", Mills = 1000, Duration = 0, Profile = "Day" },
            new() { Id = tempBasalId, EventType = "Temp Basal", Mills = 2000, Duration = 30, Absolute = 0.4 },
            tempTarget,
        };

        var result = _ddataService.ProcessTreatments(treatments, true);

        Assert.Equal(profileId, Assert.Single(result.ProfileTreatments).Id);
        Assert.Equal(tempBasalId, Assert.Single(result.TempBasalTreatments).Id);
        var convertedTarget = Assert.Single(result.TempTargetTreatments);
        Assert.Equal(tempTargetId, convertedTarget.Id);
        Assert.Equal("mg/dl", convertedTarget.Units);

        Assert.All(
            result.ProfileTreatments.Concat(result.TempBasalTreatments).Concat(result.TempTargetTreatments),
            copy => Assert.DoesNotContain(treatments, original => ReferenceEquals(original, copy)));
        Assert.Equal("mmol", tempTarget.Units);
        Assert.Equal(8, tempTarget.TargetTop);
    }

    [Fact]
    public void IdMergePreferNew_ShouldPreferNewDataWhenCollisionFound()
    {
        // Arrange
        var oldData = new List<TestDataWithId>
        {
            new() { Id = "1", Value = "old1" },
            new() { Id = "2", Value = "old2" },
        };

        var newData = new List<TestDataWithId>
        {
            new() { Id = "1", Value = "new1" }, // Collision
            new() { Id = "3", Value = "new3" },
        };

        // Act
        var result = _ddataService.IdMergePreferNew(oldData, newData);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal("new1", result.First(x => x.Id == "1").Value); // Should prefer new
        Assert.Equal("old2", result.First(x => x.Id == "2").Value); // Should keep old
        Assert.Equal("new3", result.First(x => x.Id == "3").Value); // Should include new
    }

    private class TestDataWithId
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
