using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.BolusWizard;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Legacy;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Tests.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

public class PropertiesDDataLoadingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IEntryService> _entries = new();
    private readonly Mock<ITreatmentService> _treatments = new();
    private readonly Mock<IProfileProjectionService> _profiles = new();
    private readonly Mock<IDeviceStatusService> _deviceStatuses = new();
    private readonly Mock<IFoodRepository> _food = new();
    private readonly Mock<IActivityService> _activities = new();
    private readonly Mock<IStoreSizeReader> _storeSize = new();
    private readonly Mock<ITherapySettingsResolver> _therapySettings = new();
    private readonly Mock<IIobCobReader> _iobCob = new();
    private readonly Mock<ITherapySettingsRepository> _therapySettingsRepository = new();
    private readonly Mock<IDeviceAgeService> _deviceAge = new();
    private readonly Mock<ISensitivityResolver> _sensitivity = new();
    private readonly Mock<ITargetRangeResolver> _targetRange = new();
    private readonly FakeTimeProvider _time = new(Now);

    public PropertiesDDataLoadingTests()
    {
        _therapySettings
            .Setup(t => t.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("UTC");
        _therapySettingsRepository
            .Setup(r => r.GetByProfileNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _therapySettingsRepository
            .Setup(r => r.GetDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _deviceAge
            .Setup(a => a.GetCannulaAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceAgeInfo { Found = true, Age = 10, Days = 0, Hours = 10 });
        _deviceAge
            .Setup(a => a.GetSensorAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorAgeInfo());
        _deviceAge
            .Setup(a => a.GetInsulinAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceAgeInfo { Found = true, Age = 30, Days = 1, Hours = 6 });
        _deviceAge
            .Setup(a => a.GetBatteryAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceAgeInfo());
        _therapySettings
            .Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _sensitivity
            .Setup(r => r.FindSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(50);
        _targetRange
            .Setup(r => r.FindBGTargetRangeAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((90, 110));
        _iobCob
            .Setup(r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Reading());
        StoreData();
    }

    [Fact]
    public async Task Summary_LoadsEachCollectionOnce()
    {
        var ddata = DDataService();
        var summary = new SummaryService(
            ddata,
            Properties(ddata),
            _iobCob.Object,
            ActiveProfileResolverMock.WithDefault().Object,
            _therapySettingsRepository.Object,
            Mock.Of<IProfileProjectionService>(),
            _time,
            NullLogger<SummaryService>.Instance);

        await summary.GetSummaryAsync(6);

        VerifyLoads(sgvs: 1, treatments: 1, mbgs: 1, cals: 1, profiles: 1, deviceStatus: 1, food: 1, activity: 1, dbStats: 1);
        _iobCob.Verify(
            r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task IobProperty_LoadsOnlyTheCollectionsIobReads()
    {
        await Properties(DDataService()).GetPropertiesAsync(["iob"]);

        VerifyLoads(sgvs: 1, treatments: 1, mbgs: 0, cals: 0, profiles: 0, deviceStatus: 0, food: 0, activity: 0, dbStats: 0);
    }

    [Fact]
    public async Task AnUnmappedProperty_LoadsEveryCollection()
    {
        await Properties(DDataService()).GetPropertiesAsync(["iob", "notAProperty"]);

        VerifyLoads(sgvs: 1, treatments: 1, mbgs: 1, cals: 1, profiles: 1, deviceStatus: 1, food: 1, activity: 1, dbStats: 1);
    }

    [Fact]
    public async Task EveryPropertyBuilt_HasASourceEntry()
    {
        var built = await Properties(DDataService()).GetAllPropertiesAsync();

        built.Keys.Should().NotBeEmpty();
        built.Keys
            .Where(name => PropertyDDataSources.For([name]) == DDataCollections.All)
            .Should().BeEmpty("a property with no entry loads every collection");
    }

    [Theory]
    [InlineData("bgnow")]
    [InlineData("delta")]
    [InlineData("direction")]
    [InlineData("rawbg")]
    [InlineData("ar2")]
    [InlineData("iob")]
    [InlineData("cob")]
    [InlineData("bwp")]
    [InlineData("basal")]
    [InlineData("profile")]
    [InlineData("devicestatus")]
    [InlineData("upbat")]
    [InlineData("pump")]
    [InlineData("loop")]
    [InlineData("openaps")]
    [InlineData("dbsize")]
    [InlineData("cage")]
    [InlineData("sage")]
    [InlineData("iage")]
    [InlineData("bage")]
    [InlineData("runtimestate")]
    public async Task AProperty_IsTheSameFromItsOwnCollectionsAsFromAllOfThem(string name)
    {
        var properties = Properties(DDataService());

        var full = await properties.GetAllPropertiesAsync();
        var restricted = await properties.GetPropertiesAsync([name]);

        full.Should().ContainKey(name);
        Json(restricted).Should().Be(Json(new Dictionary<string, object> { [name] = full[name] }));
    }

    [Fact]
    public async Task TheFixture_GivesEachCrossCollectionReadSomethingToChange()
    {
        var all = await Properties(DDataService()).GetAllPropertiesAsync();

        Json(new Dictionary<string, object> { ["bwp"] = all["bwp"] })
            .Should().Contain("recentCarbs").And.Contain("tempBasalAdjustment");
        Json(new Dictionary<string, object> { ["basal"] = all["basal"] })
            .Should().Contain("\"treatment\":{").And.Contain("\"combobolustreatment\":{");
        Json(new Dictionary<string, object> { ["pump"] = all["pump"] }).Should().Contain("Pump Reservoir");
    }

    private void VerifyLoads(
        int sgvs, int treatments, int mbgs, int cals, int profiles, int deviceStatus, int food, int activity, int dbStats)
    {
        EntryLoads("sgv").Should().Be(sgvs);
        EntryLoads("mbg").Should().Be(mbgs);
        EntryLoads("cal").Should().Be(cals);
        _treatments.Invocations.Count(i => i.Method.Name == nameof(ITreatmentService.GetTreatmentsAsync))
            .Should().Be(treatments);
        _profiles.Invocations.Count(i => i.Method.Name == nameof(IProfileProjectionService.GetProfilesAsync))
            .Should().Be(profiles);
        _deviceStatuses.Invocations.Count(i => i.Method.Name == nameof(IDeviceStatusService.GetDeviceStatusesAsync))
            .Should().Be(deviceStatus);
        _food.Invocations.Count(i => i.Method.Name == nameof(IFoodRepository.GetFoodAsync)).Should().Be(food);
        _activities.Invocations.Count(i => i.Method.Name == nameof(IActivityService.GetActivitiesAsync))
            .Should().Be(activity);
        _storeSize.Invocations.Count(i => i.Method.Name == nameof(IStoreSizeReader.ReadAsync)).Should().Be(dbStats);
    }

    private int EntryLoads(string type) =>
        _entries.Invocations.Count(i =>
            i.Method.Name == nameof(IEntryService.QueryEntriesAsync) && ((EntryQuery)i.Arguments[0]).Type == type);

    private IDDataService DDataService() =>
        new Nocturne.API.Services.Legacy.DDataService(
            _entries.Object,
            _treatments.Object,
            _profiles.Object,
            _deviceStatuses.Object,
            _food.Object,
            _activities.Object,
            _storeSize.Object,
            _time,
            NullLogger<Nocturne.API.Services.Legacy.DDataService>.Instance);

    private PropertiesService Properties(IDDataService ddata) =>
        new(
            ddata,
            NullLogger<PropertiesService>.Instance,
            _iobCob.Object,
            new Ar2Service(NullLogger<Ar2Service>.Instance),
            _deviceAge.Object,
            _therapySettings.Object,
            _sensitivity.Object,
            _targetRange.Object,
            new BolusWizardService(),
            new OpenApsService(),
            _time,
            Options.Create(new DbSizeOptions()));

    private static string Json(Dictionary<string, object> properties) => JsonSerializer.Serialize(properties);

    private void StoreData()
    {
        var mills = Now.ToUnixTimeMilliseconds();
        long MinutesAgo(int minutes) => mills - minutes * 60_000L;
        var entries = new Dictionary<string, List<Entry>>
        {
            ["sgv"] =
            [
                new() { Type = "sgv", Mgdl = 140, Scaled = 140, Mills = mills, Direction = "Flat", Noise = 1 },
                new() { Type = "sgv", Mgdl = 130, Scaled = 130, Mills = MinutesAgo(5), Direction = "Flat", Noise = 1 },
                new() { Type = "sgv", Mgdl = 118, Scaled = 118, Mills = MinutesAgo(180), Direction = "Flat" },
            ],
            ["mbg"] = [new() { Type = "mbg", Mbg = 120, Mills = MinutesAgo(30) }],
            ["cal"] = [new() { Type = "cal", Slope = 800, Intercept = 30000, Scale = 1, Mills = MinutesAgo(600) }],
        };
        _entries
            .Setup(e => e.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EntryQuery q, CancellationToken _) => entries[q.Type!]);
        _treatments
            .Setup(t => t.GetTreatmentsAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Treatment>
            {
                new() { EventType = "Meal Bolus", Insulin = 2.5, Carbs = 30, Mills = MinutesAgo(120) },
                new() { EventType = "Carb Correction", Carbs = 20, Mills = MinutesAgo(30) },
                new() { EventType = "Temp Basal", Absolute = 0.5, Duration = 30, Mills = MinutesAgo(10) },
                new() { EventType = "Combo Bolus", Relative = 0.3, Duration = 30, Mills = MinutesAgo(10) },
                new() { EventType = "OpenAPS Offline", Duration = 30, Mills = MinutesAgo(10) },
            });
        _profiles
            .Setup(p => p.GetProfilesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Profile>
            {
                new()
                {
                    DefaultProfile = "Default",
                    Mills = mills,
                    Store = new Dictionary<string, ProfileData>
                    {
                        ["Default"] = new() { Basal = [new() { Time = "00:00", Value = 1.0, TimeAsSeconds = 0 }] },
                    },
                },
            });
        _deviceStatuses
            .Setup(d => d.GetDeviceStatusesAsync(
                It.IsAny<FindQuery>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>(), It.IsAny<ReadSortField>()))
            .ReturnsAsync(new List<DeviceStatus>
            {
                new()
                {
                    Device = "uploader",
                    Mills = mills,
                    Uploader = new UploaderStatus { Battery = 85 },
                    Pump = new PumpStatus { Reservoir = 5, Clock = Now.ToString("O") },
                    Loop = new LoopStatus { Timestamp = Now.ToString("O") },
                    CreatedAt = Now.ToString("O"),
                },
            });
        _food
            .Setup(f => f.GetFoodAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Food> { new() { Name = "Apple", Carbs = 15 } });
        _activities
            .Setup(a => a.GetActivitiesAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Activity> { new() { Mills = MinutesAgo(60), Notes = "Walk" } });
        _storeSize
            .Setup(s => s.ReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbStats { DataSize = 1000, IndexSize = 500 });
    }

    private IobCobReading Reading()
    {
        var mills = Now.ToUnixTimeMilliseconds();
        var bolus = new Bolus { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills - 120 * 60_000L).UtcDateTime, Insulin = 2.5 };
        var carbs = new CarbIntake { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills - 30 * 60_000L).UtcDateTime, Carbs = 20 };
        return new IobCobReading(
            new IobResult { Iob = 1.2, LastBolus = bolus },
            new CobResult { Cob = 10, IsDecaying = 1, LastCarbs = carbs },
            new IobCobInputs([bolus], [], [carbs]));
    }
}
