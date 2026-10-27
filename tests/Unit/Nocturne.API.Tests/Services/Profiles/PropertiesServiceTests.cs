using Nocturne.API.Tests.Infrastructure;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

/// <summary>
/// Unit tests for PropertiesService
/// Tests the 1:1 compatibility with legacy JavaScript implementation
/// </summary>
public class PropertiesServiceTests
{
    private readonly Mock<IDDataService> _mockDDataService;
    private readonly Mock<ILogger<PropertiesService>> _mockLogger;
    private readonly Mock<IDeviceAgeService> _mockDeviceAgeService;
    private readonly Mock<ITherapySettingsResolver> _therapySettings = new();
    private readonly PropertiesService _service;

    public PropertiesServiceTests()
    {
        _mockDDataService = new Mock<IDDataService>();
        _mockLogger = new Mock<ILogger<PropertiesService>>();
        var mockAr2Service = new Mock<IAr2Service>();

        _mockDeviceAgeService = new Mock<IDeviceAgeService>();
        _mockDeviceAgeService
            .Setup(x => x.GetCannulaAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceAgeInfo { Found = true, Age = 10, Days = 0, Hours = 10 });
        _mockDeviceAgeService
            .Setup(x => x.GetSensorAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorAgeInfo());
        _mockDeviceAgeService
            .Setup(x => x.GetInsulinAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceAgeInfo { Found = true, Age = 30, Days = 1, Hours = 6 });
        _mockDeviceAgeService
            .Setup(x => x.GetBatteryAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceAgeInfo());

        _service = new PropertiesService(
            _mockDDataService.Object,
            _mockLogger.Object,
            TestIobCobReader.Empty(),
            mockAr2Service.Object,
            _mockDeviceAgeService.Object,
            _therapySettings.Object,
            TimeProvider.System,
            Options.Create(new DbSizeOptions())
        );
    }

    [Fact]
    public async Task GetAllPropertiesAsync_ReturnsFilteredProperties()
    {
        // Arrange
        var testDData = CreateTestDData();
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(testDData);

        // Act
        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Count > 0);

        // Should contain expected properties
        Assert.True(result.ContainsKey("bgnow"));
        Assert.True(result.ContainsKey("delta"));
        Assert.True(result.ContainsKey("direction"));
    }

    [Theory]
    [InlineData(59, "ok")]
    [InlineData(60, "warn")]
    [InlineData(74, "warn")]
    [InlineData(75, "urgent")]
    public async Task DbSize_StatusFollowsTheConfiguredBudget(int percent, string status)
    {
        const long mib = 1024 * 1024;
        var service = ServiceWithBudget(maxMiB: 100);
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DData { DbStats = new DbStats { DataSize = percent * mib / 2, IndexSize = percent * mib / 2 } });

        var result = await service.GetPropertiesAsync(new[] { "dbsize" }, CancellationToken.None);

        var dbsize = (Dictionary<string, object>)result["dbsize"];
        dbsize["size"].Should().Be(percent * mib, "the size is the data and the indexes together");
        dbsize["percentage"].Should().Be(percent);
        dbsize["status"].Should().Be(status);
    }

    [Fact]
    public async Task DbSize_DefaultsToABudgetOf2048MiB()
    {
        var service = ServiceWithBudget(new DbSizeOptions());
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DData { DbStats = new DbStats { DataSize = 1024L * 1024 * 1024 } });

        var result = await service.GetPropertiesAsync(new[] { "dbsize" }, CancellationToken.None);

        ((Dictionary<string, object>)result["dbsize"])["percentage"].Should().Be(50);
    }

    private PropertiesService ServiceWithBudget(int maxMiB) =>
        ServiceWithBudget(new DbSizeOptions { MaxMiB = maxMiB });

    private PropertiesService ServiceWithBudget(DbSizeOptions budget) =>
        new(
            _mockDDataService.Object,
            _mockLogger.Object,
            TestIobCobReader.Empty(),
            Mock.Of<IAr2Service>(),
            _mockDeviceAgeService.Object,
            _therapySettings.Object,
            TimeProvider.System,
            Options.Create(budget)
        );

    [Fact]
    public async Task GetPropertiesAsync_WithSpecificNames_ReturnsOnlyRequestedProperties()
    {
        // Arrange
        var testDData = CreateTestDData();
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(testDData);

        var requestedProperties = new[] { "bgnow", "delta" };

        // Act
        var result = await _service.GetPropertiesAsync(requestedProperties, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("bgnow"));
        Assert.True(result.ContainsKey("delta"));
        Assert.False(result.ContainsKey("direction"));
    }

    [Fact]
    public async Task GetPropertiesAsync_BgNow_IsTheNewestFiveMinuteBucket()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = new DData
        {
            Sgvs = new List<Entry>
            {
                new() { Type = "sgv", Mills = now - 5 * 60 * 1000, Mgdl = 90 },
                new() { Type = "sgv", Mills = now - 2 * 60 * 1000, Mgdl = 100 },
                new() { Type = "sgv", Mills = now - 60 * 1000, Mgdl = 110 },
                new() { Type = "sgv", Mills = now, Mgdl = 120 },
            },
        };
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetPropertiesAsync(new[] { "bgnow" }, CancellationToken.None);

        var json = JsonSerializer.Serialize(result, NightscoutJsonOptions.Create());
        using var doc = JsonDocument.Parse(json);
        var bgnow = doc.RootElement.GetProperty("bgnow");

        Assert.Equal(110, bgnow.GetProperty("mean").GetDouble());
        Assert.Equal(120, bgnow.GetProperty("last").GetDouble());
        Assert.Equal(now, bgnow.GetProperty("mills").GetInt64());
        Assert.Equal(
            new[] { 100.0, 110.0, 120.0 },
            bgnow.GetProperty("sgvs").EnumerateArray().Select(s => s.GetProperty("mgdl").GetDouble())
        );
        Assert.False(bgnow.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task GetPropertiesAsync_BgNow_LeavesSensorErrorCodesOutOfTheMean()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = new DData
        {
            Sgvs = new List<Entry>
            {
                new() { Type = "sgv", Mills = now - 60 * 1000, Mgdl = 100 },
                new() { Type = "sgv", Mills = now, Mgdl = 5 },
            },
        };
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetPropertiesAsync(new[] { "bgnow" }, CancellationToken.None);

        var json = JsonSerializer.Serialize(result, NightscoutJsonOptions.Create());
        using var doc = JsonDocument.Parse(json);
        var bgnow = doc.RootElement.GetProperty("bgnow");

        Assert.Equal(100, bgnow.GetProperty("mean").GetDouble());
        Assert.Equal(100, bgnow.GetProperty("last").GetDouble());
        Assert.Equal(now - 60 * 1000, bgnow.GetProperty("mills").GetInt64());
        Assert.Equal(5, bgnow.GetProperty("errors")[0].GetProperty("mgdl").GetDouble());
    }

    [Fact]
    public async Task GetPropertiesAsync_Delta_FromOneMinuteReadings_IsTheChangeBetweenBucketMeans()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var readings = Enumerable
            .Range(0, 20)
            .Select(m => new Entry { Type = "sgv", Mills = now - m * 60 * 1000, Mgdl = 200 - 2 * m })
            .ToList();

        var delta = await GetDeltaAsync(readings);

        // Buckets {200,198,196} and {194,192,190,188,186}: means 198 and 190, newest valid 3 minutes apart.
        Assert.Equal(8, delta.GetProperty("mgdl").GetDouble());
        Assert.Equal(190, delta.GetProperty("mean5MinsAgo").GetDouble());
        Assert.Equal(8, delta.GetProperty("absolute").GetDouble());
        Assert.Equal(3, delta.GetProperty("elapsedMins").GetDouble());
        Assert.False(delta.GetProperty("interpolated").GetBoolean());
        Assert.Equal("+8", delta.GetProperty("display").GetString());
    }

    [Fact]
    public async Task GetPropertiesAsync_Delta_FromFiveMinuteReadings_IsTheChangeBetweenReadings()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var readings = new List<Entry>
        {
            new() { Type = "sgv", Mills = now - 10 * 60 * 1000, Mgdl = 90 },
            new() { Type = "sgv", Mills = now - 5 * 60 * 1000, Mgdl = 100 },
            new() { Type = "sgv", Mills = now, Mgdl = 105 },
        };

        var delta = await GetDeltaAsync(readings);

        Assert.Equal(5, delta.GetProperty("mgdl").GetDouble());
        Assert.Equal(100, delta.GetProperty("mean5MinsAgo").GetDouble());
        Assert.Equal(5, delta.GetProperty("elapsedMins").GetDouble());
    }

    [Fact]
    public async Task GetPropertiesAsync_Delta_SkipsAnEmptyBucketAndInterpolatesOverTheGap()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var readings = new List<Entry>
        {
            new() { Type = "sgv", Mills = now, Mgdl = 120 },
            new() { Type = "sgv", Mills = now - 10 * 60 * 1000, Mgdl = 100 },
            new() { Type = "sgv", Mills = now - 11 * 60 * 1000, Mgdl = 96 },
        };

        var delta = await GetDeltaAsync(readings);

        // Previous bucket mean is 98, 10 minutes back: 120 - (120 - 98) / 10 * 5 = 109.
        Assert.Equal(10, delta.GetProperty("elapsedMins").GetDouble());
        Assert.True(delta.GetProperty("interpolated").GetBoolean());
        Assert.Equal(109, delta.GetProperty("mean5MinsAgo").GetDouble());
        Assert.Equal(11, delta.GetProperty("mgdl").GetDouble());
        Assert.Equal(22, delta.GetProperty("absolute").GetDouble());
    }

    [Fact]
    public async Task GetPropertiesAsync_Delta_LeavesSensorErrorCodesOutOfTheBucketMeans()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var readings = new List<Entry>
        {
            new() { Type = "sgv", Mills = now, Mgdl = 120 },
            new() { Type = "sgv", Mills = now - 60 * 1000, Mgdl = 39 },
            new() { Type = "sgv", Mills = now - 4 * 60 * 1000, Mgdl = 20 },
            new() { Type = "sgv", Mills = now - 5 * 60 * 1000, Mgdl = 100 },
        };

        var delta = await GetDeltaAsync(readings);

        Assert.Equal(20, delta.GetProperty("absolute").GetDouble());
        Assert.Equal(5, delta.GetProperty("elapsedMins").GetDouble());
    }

    [Fact]
    public async Task GetPropertiesAsync_Delta_IsAbsentWithoutASecondPopulatedBucket()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var readings = new List<Entry>
        {
            new() { Type = "sgv", Mills = now, Mgdl = 120 },
            new() { Type = "sgv", Mills = now - 60 * 1000, Mgdl = 118 },
            new() { Type = "sgv", Mills = now - 5 * 60 * 1000, Mgdl = 5 },
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DData { Sgvs = readings });

        var result = await _service.GetPropertiesAsync(new[] { "delta" }, CancellationToken.None);

        Assert.False(result.ContainsKey("delta"));
    }

    [Theory]
    [InlineData(111, 110, 11, "+11")]
    [InlineData(89, 90, -10, "-10")]
    [InlineData(88, 89, -11, "-11")]
    public async Task GetPropertiesAsync_Delta_RoundsHalvesTowardsPositiveInfinityLikeJavaScript(
        double newest,
        double second,
        int expectedMgdl,
        string expectedDisplay
    )
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var readings = new List<Entry>
        {
            new() { Type = "sgv", Mills = now, Mgdl = newest },
            new() { Type = "sgv", Mills = now - 2 * 60 * 1000, Mgdl = second },
            new() { Type = "sgv", Mills = now - 4 * 60 * 1000, Mgdl = 100 },
            new() { Type = "sgv", Mills = now - 6 * 60 * 1000, Mgdl = 100 },
        };

        var delta = await GetDeltaAsync(readings);

        Assert.Equal(expectedMgdl, delta.GetProperty("mgdl").GetDouble());
        Assert.Equal(expectedDisplay, delta.GetProperty("display").GetString());
    }

    [Fact]
    public async Task GetPropertiesAsync_Delta_PublishesThePreviousBucket()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var readings = new List<Entry>
        {
            new() { Type = "sgv", Mills = now, Mgdl = 120 },
            new() { Type = "sgv", Mills = now - 4 * 60 * 1000, Mgdl = 100 },
            new() { Type = "sgv", Mills = now - 5 * 60 * 1000, Mgdl = 20 },
            new() { Type = "sgv", Mills = now - 6 * 60 * 1000, Mgdl = 90 },
        };

        var delta = await GetDeltaAsync(readings);

        var previous = delta.GetProperty("previous");
        Assert.Equal(95, previous.GetProperty("mean").GetDouble());
        Assert.Equal(100, previous.GetProperty("last").GetDouble());
        Assert.Equal(now - 4 * 60 * 1000, previous.GetProperty("mills").GetInt64());
        Assert.Equal(3, previous.GetProperty("sgvs").GetArrayLength());
        Assert.Equal(20, previous.GetProperty("errors")[0].GetProperty("mgdl").GetDouble());
        Assert.False(delta.TryGetProperty("current", out _));
    }

    [Theory]
    [InlineData(39)]
    [InlineData(10)]
    public async Task GetPropertiesAsync_NewestBucketOfOnlyErrorCodes_HasNoMeanDeltaOrForecast(double code)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var readings = new List<Entry>
        {
            new() { Type = "sgv", Mills = now, Mgdl = code },
            new() { Type = "sgv", Mills = now - 5 * 60 * 1000, Mgdl = 60 },
            new() { Type = "sgv", Mills = now - 10 * 60 * 1000, Mgdl = 80 },
        };
        var ar2Service = new Mock<IAr2Service>();
        var service = new PropertiesService(
            _mockDDataService.Object,
            _mockLogger.Object,
            TestIobCobReader.Empty(),
            ar2Service.Object,
            _mockDeviceAgeService.Object,
            _therapySettings.Object,
            TimeProvider.System,
            Options.Create(new DbSizeOptions())
        );
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DData { Sgvs = readings });

        var result = await service.GetPropertiesAsync(new[] { "bgnow", "delta", "ar2" }, CancellationToken.None);

        var json = JsonSerializer.Serialize(result, NightscoutJsonOptions.Create());
        using var doc = JsonDocument.Parse(json);
        var bgnow = doc.RootElement.GetProperty("bgnow");
        Assert.Equal(code, bgnow.GetProperty("mgdl").GetDouble());
        Assert.False(bgnow.TryGetProperty("mean", out _));
        Assert.False(bgnow.TryGetProperty("mills", out _));
        Assert.False(doc.RootElement.TryGetProperty("delta", out _));
        Assert.False(doc.RootElement.TryGetProperty("ar2", out _));
        ar2Service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetPropertiesAsync_BgNow_ListsOnlyReadingsBelowThirtyNineAsErrors()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var readings = new List<Entry>
        {
            new() { Type = "sgv", Mills = now, Mgdl = 100 },
            new() { Type = "sgv", Mills = now - 60 * 1000, Mgdl = 39 },
            new() { Type = "sgv", Mills = now - 2 * 60 * 1000, Mgdl = 38 },
        };
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DData { Sgvs = readings });

        var result = await _service.GetPropertiesAsync(new[] { "bgnow" }, CancellationToken.None);

        var json = JsonSerializer.Serialize(result, NightscoutJsonOptions.Create());
        using var doc = JsonDocument.Parse(json);
        var bgnow = doc.RootElement.GetProperty("bgnow");
        Assert.Equal(100, bgnow.GetProperty("mean").GetDouble());
        Assert.Equal(38, Assert.Single(bgnow.GetProperty("errors").EnumerateArray()).GetProperty("mgdl").GetDouble());
        Assert.Equal(3, bgnow.GetProperty("sgvs").GetArrayLength());
    }

    private async Task<JsonElement> GetDeltaAsync(List<Entry> sgvs)
    {
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DData { Sgvs = sgvs });

        var result = await _service.GetPropertiesAsync(new[] { "delta" }, CancellationToken.None);

        var json = JsonSerializer.Serialize(result, NightscoutJsonOptions.Create());
        return JsonDocument.Parse(json).RootElement.GetProperty("delta").Clone();
    }

    [Fact]
    public async Task GetAllPropertiesAsync_EmitsIobAndCobAtZero_WithDisplayFields()
    {
        // Legacy clients (LoopFollow, Trio) treat a missing `iob`/`cob` as "unsupported"
        // rather than "zero", so both must be present even with no insulin/carbs on board.
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestDData());

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var iob = Assert.IsType<Dictionary<string, object?>>(result["iob"]);
        Assert.Equal(0d, iob["iob"]);
        Assert.Equal("0", iob["display"]);
        Assert.Equal("IOB: 0U", iob["displayLine"]);

        var cob = Assert.IsType<Dictionary<string, object?>>(result["cob"]);
        Assert.Equal(0d, cob["cob"]);
        Assert.Equal("0", cob["display"]);
        Assert.Equal("COB: 0g", cob["displayLine"]);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_EmitsDeviceAgeProperties()
    {
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestDData());

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        Assert.True(result.ContainsKey("cage"));
        Assert.True(result.ContainsKey("sage"));
        Assert.True(result.ContainsKey("iage"));
        Assert.True(result.ContainsKey("bage"));

        var cage = Assert.IsType<Dictionary<string, object?>>(result["cage"]);
        Assert.Equal(true, cage["found"]);
        Assert.Equal(10, cage["age"]);

        var sage = Assert.IsType<Dictionary<string, object?>>(result["sage"]);
        Assert.True(sage.ContainsKey("Sensor Start"));
        Assert.True(sage.ContainsKey("Sensor Change"));
        Assert.Equal("Sensor Start", sage["min"]);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_DeviceAgeFields_SurviveNightscoutSerialization()
    {
        // The Nightscout response filter serializes with WhenWritingDefault, which strips
        // zero/false members from typed objects. Age fields are legitimately zero right
        // after a site change, so they must be projected as dictionaries to reach clients.
        _mockDeviceAgeService
            .Setup(x => x.GetCannulaAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceAgeInfo { Found = true, Age = 0, Days = 0, Hours = 0, TreatmentDate = 1000 });
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestDData());

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var cage = SerializeProperty(result, "cage");

        Assert.True(cage.GetProperty("found").GetBoolean());
        Assert.Equal(0, cage.GetProperty("age").GetInt32());
        Assert.Equal(0, cage.GetProperty("days").GetInt32());
        Assert.Equal(DeviceAgeLevel.None, cage.GetProperty("level").GetInt32());
    }

    [Fact]
    public async Task GetAllPropertiesAsync_CageWithNoSiteChange_HasNightscoutShape()
    {
        _mockDeviceAgeService
            .Setup(x => x.GetCannulaAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceAgeInfo());
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestDData());

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var cage = SerializeProperty(result, "cage");

        Assert.False(cage.GetProperty("found").GetBoolean());
        Assert.Equal(-3, cage.GetProperty("level").GetInt32());
        Assert.Equal("n/a ", cage.GetProperty("display").GetString());
        Assert.Equal(JsonValueKind.Null, cage.GetProperty("treatmentDate").ValueKind);
        Assert.False(cage.TryGetProperty("days", out _));
    }

    [Fact]
    public async Task GetAllPropertiesAsync_SageWithNoSensorEvents_HasNightscoutShape()
    {
        _mockDeviceAgeService
            .Setup(x => x.GetSensorAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorAgeInfo());
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestDData());

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var sage = SerializeProperty(result, "sage");
        var start = sage.GetProperty("Sensor Start");
        var change = sage.GetProperty("Sensor Change");

        Assert.Equal("Sensor Start", sage.GetProperty("min").GetString());
        Assert.False(start.GetProperty("found").GetBoolean());
        Assert.Equal(-3, start.GetProperty("level").GetInt32());
        Assert.False(change.GetProperty("found").GetBoolean());
        Assert.False(change.TryGetProperty("level", out _));
    }

    [Fact]
    public async Task GetAllPropertiesAsync_SageWithSensorStart_SetsLevelOnlyOnMin()
    {
        _mockDeviceAgeService
            .Setup(x => x.GetSensorAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorAgeInfo
            {
                SensorStart = new DeviceAgeInfo { Found = true, Age = 120, Days = 5, Hours = 0, TreatmentDate = 1000, Display = "5d0h" },
            });
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestDData());

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var sage = SerializeProperty(result, "sage");
        var start = sage.GetProperty("Sensor Start");

        Assert.True(start.GetProperty("found").GetBoolean());
        Assert.Equal(-3, start.GetProperty("level").GetInt32());
        Assert.Equal("5 days 0 hours", start.GetProperty("displayLong").GetString());
        Assert.False(sage.GetProperty("Sensor Change").TryGetProperty("level", out _));
    }

    [Fact]
    public async Task GetAllPropertiesAsync_AsksForSageWithDaysDisplay()
    {
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestDData());

        await _service.GetAllPropertiesAsync(CancellationToken.None);

        _mockDeviceAgeService.Verify(
            x => x.GetSensorAgeAsync(
                It.Is<DeviceAgePreferences>(p => p.Display == "days"),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static JsonElement SerializeProperty(Dictionary<string, object> properties, string name)
    {
        var json = JsonSerializer.Serialize(properties, NightscoutJsonOptions.Create());
        return JsonDocument.Parse(json).RootElement.GetProperty(name).Clone();
    }

    [Fact]
    public async Task GetPropertiesAsync_WithoutAgeNames_SkipsDeviceAgeLookups()
    {
        // Clients poll single properties (e.g. /api/v2/properties/iob); those requests
        // shouldn't pay for the device-age queries.
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestDData());

        await _service.GetPropertiesAsync(new[] { "iob" }, CancellationToken.None);

        _mockDeviceAgeService.Verify(
            x => x.GetCannulaAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await _service.GetPropertiesAsync(new[] { "cage" }, CancellationToken.None);

        _mockDeviceAgeService.Verify(
            x => x.GetCannulaAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_EmitsPerDeviceUploaderBatteries()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = CreateTestDData();
        ddata.DeviceStatus = new List<DeviceStatus>
        {
            // Two reports from the same phone: only the newest should be listed.
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now - 60_000,
                Uploader = new UploaderStatus { Battery = 90, IsCharging = false },
            },
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now,
                Uploader = new UploaderStatus { Battery = 85, IsCharging = true, BatteryVoltage = 4.1 },
            },
            new DeviceStatus
            {
                Device = "phone-b",
                Mills = now - 30_000,
                Uploader = new UploaderStatus { Battery = 40, IsCharging = false },
            },
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var upbat = Assert.IsType<Dictionary<string, object>>(result["upbat"]);
        var devices = Assert.IsType<List<Dictionary<string, object?>>>(upbat["devices"]);

        Assert.Equal(2, devices.Count);

        // Weakest battery first.
        Assert.Equal("phone-b", devices[0]["device"]);
        Assert.Equal(40, devices[0]["value"]);

        Assert.Equal("phone-a", devices[1]["device"]);
        Assert.Equal(85, devices[1]["value"]);
        Assert.Equal(true, devices[1]["isCharging"]);
        Assert.Equal(4.1, devices[1]["voltage"]);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_EmitsUpbat_WhenNewestStatusHasNoUploaderBattery()
    {
        // AID systems interleave pump-only device statuses; the newest record often has
        // no uploader battery, which must not suppress the whole property.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = CreateTestDData();
        ddata.DeviceStatus = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now - 60_000,
                Uploader = new UploaderStatus { Battery = 72 },
            },
            new DeviceStatus
            {
                Device = "pump",
                Mills = now,
                Pump = new PumpStatus { Reservoir = 120 },
            },
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var upbat = Assert.IsType<Dictionary<string, object>>(result["upbat"]);
        Assert.Equal(75, upbat["level"]);
        Assert.Equal("72%", upbat["display"]);

        var devices = Assert.IsType<List<Dictionary<string, object?>>>(upbat["devices"]);
        Assert.Single(devices);
        Assert.Equal("phone-a", devices[0]["device"]);
    }

    [Theory]
    [InlineData(95, 100)]
    [InlineData(94, 75)]
    [InlineData(80, 75)]
    [InlineData(55, 75)]
    [InlineData(54, 50)]
    [InlineData(30, 50)]
    [InlineData(29, 25)]
    public async Task GetAllPropertiesAsync_UpbatLevel_IsBucketedLikeNightscout(int battery, int expectedLevel)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = CreateTestDData();
        ddata.DeviceStatus = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now - 60_000,
                Uploader = new UploaderStatus { Battery = battery },
            },
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var upbat = Assert.IsType<Dictionary<string, object>>(result["upbat"]);
        Assert.Equal(expectedLevel, upbat["level"]);
        Assert.Equal($"{battery}%", upbat["display"]);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_UpbatLevel_IsWeakestRecentUploaderNotNewest()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = CreateTestDData();
        ddata.DeviceStatus = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "openaps://TestPump",
                Mills = now - 5 * 60_000,
                Uploader = new UploaderStatus { Battery = 80 },
            },
            new DeviceStatus
            {
                Device = "loop://TestLoop",
                Mills = now - 10 * 60_000,
                Uploader = new UploaderStatus { Battery = 40 },
            },
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var upbat = Assert.IsType<Dictionary<string, object>>(result["upbat"]);
        Assert.Equal(50, upbat["level"]);
        Assert.Equal("40%", upbat["display"]);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_UpbatLevel_IgnoresDeviceReadingsOlderThanTenMinutesBeforeItsNewest()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = CreateTestDData();
        ddata.DeviceStatus = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now - 25 * 60_000,
                Uploader = new UploaderStatus { Battery = 20 },
            },
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now - 60_000,
                Uploader = new UploaderStatus { Battery = 90 },
            },
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var upbat = Assert.IsType<Dictionary<string, object>>(result["upbat"]);
        Assert.Equal(75, upbat["level"]);
        Assert.Equal("90%", upbat["display"]);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_UpbatLevel_IsWeakestOfEachDevicesRecentLowest()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = CreateTestDData();
        ddata.DeviceStatus = new List<DeviceStatus>
        {
            // phone-a recovered: its 10% reading is stale relative to its own newest.
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now - 20 * 60_000,
                Uploader = new UploaderStatus { Battery = 10 },
            },
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now - 60_000,
                Uploader = new UploaderStatus { Battery = 85 },
            },
            new DeviceStatus
            {
                Device = "phone-b",
                Mills = now - 2 * 60_000,
                Uploader = new UploaderStatus { Battery = 60 },
            },
            new DeviceStatus
            {
                Device = "phone-b",
                Mills = now - 4 * 60_000,
                Uploader = new UploaderStatus { Battery = 45 },
            },
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var upbat = Assert.IsType<Dictionary<string, object>>(result["upbat"]);
        Assert.Equal(50, upbat["level"]);
        Assert.Equal("45%", upbat["display"]);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_UpbatOmitsLevel_ForZeroPercentWithNoVoltage()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = CreateTestDData();
        ddata.DeviceStatus = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now - 60_000,
                Uploader = new UploaderStatus { Battery = 0 },
            },
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var upbat = Assert.IsType<Dictionary<string, object>>(result["upbat"]);
        Assert.False(upbat.ContainsKey("level"));
        Assert.False(upbat.ContainsKey("display"));
        Assert.False(upbat.ContainsKey("displayLine"));
    }

    [Theory]
    [InlineData(3.7, 25)]
    [InlineData(3700.0, 25)]
    [InlineData(0.0, null)]
    public async Task GetAllPropertiesAsync_UpbatLevel_ForZeroPercentFollowsVoltage(double voltage, int? expectedLevel)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = CreateTestDData();
        ddata.DeviceStatus = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now - 60_000,
                Uploader = new UploaderStatus { Battery = 0, BatteryVoltage = voltage },
            },
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var upbat = Assert.IsType<Dictionary<string, object>>(result["upbat"]);
        if (expectedLevel is { } level)
            Assert.Equal(level, upbat["level"]);
        else
            Assert.False(upbat.ContainsKey("level"));
    }

    [Theory]
    [InlineData(3.7, "3.700v")]
    [InlineData(3700.0, "3.700v")]
    public async Task GetAllPropertiesAsync_UpbatDisplay_ForZeroPercentWithVoltageIsTheVoltage(double voltage, string expected)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = CreateTestDData();
        ddata.DeviceStatus = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now - 60_000,
                Uploader = new UploaderStatus { Battery = 0, BatteryVoltage = voltage },
            },
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var upbat = Assert.IsType<Dictionary<string, object>>(result["upbat"]);
        Assert.Equal(expected, upbat["display"]);
        Assert.Equal($"Battery: {expected}", upbat["displayLine"]);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_UpbatDisplay_ForNonZeroPercentWithVoltageStaysPercent()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = CreateTestDData();
        ddata.DeviceStatus = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now - 60_000,
                Uploader = new UploaderStatus { Battery = 5, BatteryVoltage = 3.7 },
            },
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var upbat = Assert.IsType<Dictionary<string, object>>(result["upbat"]);
        Assert.Equal("5%", upbat["display"]);
        Assert.Equal("Battery: 5%", upbat["displayLine"]);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_OmitsUpbat_WhenNoUploaderStatusWithinThirtyMinutes()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = CreateTestDData();
        ddata.DeviceStatus = new List<DeviceStatus>
        {
            new DeviceStatus
            {
                Device = "phone-a",
                Mills = now - 31 * 60_000,
                Uploader = new UploaderStatus { Battery = 80 },
            },
        };

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        Assert.False(result.ContainsKey("upbat"));
    }

    [Fact]
    public void ApplySecurityFiltering_RemovesSecureProperties()
    {
        // Arrange
        var properties = new Dictionary<string, object>
        {
            ["userName"] = "testuser",
            ["password"] = "secret",
            ["bgnow"] = new { mgdl = 120 },
            ["apnsKey"] = "secretkey",
        };

        // Act
        var result = _service.ApplySecurityFiltering(properties);

        // Assert
        Assert.False(result.ContainsKey("userName"));
        Assert.False(result.ContainsKey("password"));
        Assert.False(result.ContainsKey("apnsKey"));
        Assert.True(result.ContainsKey("bgnow"));
    }

    [Fact]
    public async Task GetAllPropertiesAsync_WithEmptyDData_ReturnsEmptyResult()
    {
        // Arrange
        var emptyDData = new DData();
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyDData);

        // Act
        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        // Should still have runtime state even with empty data
        Assert.True(result.ContainsKey("runtimestate"));
    }

    [Fact]
    public async Task GetAllPropertiesAsync_WithBasalProfile_ReturnsBasalProperties()
    {
        // Arrange
        var testDData = CreateTestDDataWithBasalProfile();
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(testDData);

        // Act
        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.ContainsKey("basal"));

        var basal = result["basal"] as Dictionary<string, object>;
        Assert.NotNull(basal);
        Assert.True(basal.ContainsKey("display"));
        Assert.True(basal.ContainsKey("current"));

        var display = basal["display"] as string;
        Assert.NotNull(display);
        Assert.EndsWith("U", display); // Should end with 'U' for units

        var current = basal["current"] as Dictionary<string, object>;
        Assert.NotNull(current);
        Assert.True(current.ContainsKey("basal"));
        Assert.True(current.ContainsKey("totalbasal"));
    }

    [Fact]
    public async Task GetAllPropertiesAsync_BasalEntriesCarryingOnlyTime_ResolveAsTheSameEntriesWithTimeAsSeconds()
    {
        var hour = DateTimeOffset.UtcNow.Hour;
        var upcoming = $"{(hour + 1) % 24:00}:00";
        var current = $"{hour:00}:00";

        List<TimeValue> Schedule(bool withSeconds) =>
        [
            new() { Time = upcoming, Value = 9.0, TimeAsSeconds = withSeconds ? ((hour + 1) % 24) * 3600 : null },
            new() { Time = current, Value = 5.0, TimeAsSeconds = withSeconds ? hour * 3600 : null },
        ];

        async Task<double> CurrentBasal(List<TimeValue> schedule)
        {
            _mockDDataService
                .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateTestDDataWithBasalProfile(schedule));
            var result = await _service.GetAllPropertiesAsync(CancellationToken.None);
            var basal = (Dictionary<string, object>)result["basal"];
            return Convert.ToDouble(((Dictionary<string, object>)basal["current"])["basal"]);
        }

        Assert.Equal(5.0, await CurrentBasal(Schedule(withSeconds: true)));
        Assert.Equal(5.0, await CurrentBasal(Schedule(withSeconds: false)));
    }

    private PropertiesService ServiceAt(DateTimeOffset now) =>
        new(
            _mockDDataService.Object,
            _mockLogger.Object,
            TestIobCobReader.Empty(),
            Mock.Of<IAr2Service>(),
            _mockDeviceAgeService.Object,
            _therapySettings.Object,
            new FakeTimeProvider(now),
            Options.Create(new DbSizeOptions())
        );

    private async Task<double> CurrentBasalAt(DateTimeOffset now, List<TimeValue> schedule)
    {
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestDDataWithBasalProfile(schedule));
        var result = await ServiceAt(now).GetAllPropertiesAsync(CancellationToken.None);
        var basal = (Dictionary<string, object>)result["basal"];
        return Convert.ToDouble(((Dictionary<string, object>)basal["current"])["basal"]);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_BasalIsTheRateAtThePatientsLocalTimeOfDay()
    {
        // 03:00 UTC is 16:00 in Auckland (UTC+13, daylight time).
        var now = new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero);
        _therapySettings
            .Setup(x => x.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Pacific/Auckland");

        var rate = await CurrentBasalAt(now,
        [
            new() { Time = "03:00", Value = 9.0 },
            new() { Time = "16:00", Value = 5.0 },
            new() { Time = "22:00", Value = 7.0 },
        ]);

        rate.Should().Be(5.0);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_BasalWithNoKnownZone_ReadsTheScheduleInUtc()
    {
        var now = new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero);

        var rate = await CurrentBasalAt(now,
        [
            new() { Time = "03:00", Value = 9.0 },
            new() { Time = "16:00", Value = 5.0 },
        ]);

        rate.Should().Be(9.0);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_BasalBeforeTheFirstEntry_TakesTheEarliestEntryByTime()
    {
        var now = new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero);

        var rate = await CurrentBasalAt(now,
        [
            new() { Time = "08:00", Value = 3.0 },
            new() { Time = "06:00", Value = 2.0 },
        ]);

        rate.Should().Be(2.0);
    }

    [Fact]
    public async Task GetPropertiesAsync_WithoutBasalRequested_DoesNotResolveTheTimezone()
    {
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestDDataWithBasalProfile());

        await _service.GetPropertiesAsync(new[] { "iob" }, CancellationToken.None);

        _therapySettings.Verify(
            x => x.GetTimezoneAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_WithTempBasal_ReturnsBasalPropertiesWithTempMarker()
    {
        // Arrange
        var testDData = CreateTestDDataWithTempBasal();
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(testDData);

        // Act
        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);

        // Check if we have any properties at all
        Assert.True(result.Count > 0, "No properties returned at all");

        // Debug - let's see what we actually got
        foreach (var kvp in result)
        {
            System.Diagnostics.Debug.WriteLine($"Key: {kvp.Key}, Value: {kvp.Value}");
        }

        Assert.True(
            result.ContainsKey("basal"),
            $"Expected 'basal' key but got keys: {string.Join(", ", result.Keys)}"
        );

        var basal = result["basal"] as Dictionary<string, object>;
        Assert.NotNull(basal);

        var display = basal["display"] as string;
        Assert.NotNull(display);
        Assert.StartsWith("T: ", display); // Should start with 'T: ' for temp basal

        var current = basal["current"] as Dictionary<string, object>;
        Assert.NotNull(current);
        Assert.NotNull(current["treatment"]); // Should have treatment

        // Total basal should be different from base basal due to temp adjustment
        var totalBasal = Convert.ToDouble(current["totalbasal"]);
        var baseBasal = Convert.ToDouble(current["basal"]);
        Assert.NotEqual(baseBasal, totalBasal);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_WithComboBolus_ReturnsBasalPropertiesWithComboMarker()
    {
        // Arrange
        var testDData = CreateTestDDataWithComboBolus();
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(testDData);

        // Act
        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.ContainsKey("basal"));

        var basal = result["basal"] as Dictionary<string, object>;
        Assert.NotNull(basal);

        var display = basal["display"] as string;
        Assert.NotNull(display);
        Assert.StartsWith("C: ", display); // Should start with 'C: ' for combo bolus

        var current = basal["current"] as Dictionary<string, object>;
        Assert.NotNull(current);
        Assert.NotNull(current["combobolustreatment"]); // Should have combo bolus treatment
    }

    [Fact]
    public async Task GetAllPropertiesAsync_WithTempBasalAndComboBolus_ReturnsBasalPropertiesWithBothMarkers()
    {
        // Arrange
        var testDData = CreateTestDDataWithTempBasalAndComboBolus();
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(testDData);

        // Act
        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.ContainsKey("basal"));

        var basal = result["basal"] as Dictionary<string, object>;
        Assert.NotNull(basal);

        var display = basal["display"] as string;
        Assert.NotNull(display);
        Assert.StartsWith("TC: ", display); // Should start with 'TC: ' for both temp and combo

        var current = basal["current"] as Dictionary<string, object>;
        Assert.NotNull(current);
        Assert.NotNull(current["treatment"]); // Should have temp basal treatment
        Assert.NotNull(current["combobolustreatment"]); // Should have combo bolus treatment
    }

    [Theory]
    [InlineData(-50, 0.5)]
    [InlineData(50, 1.5)]
    [InlineData(150, 2.5)]
    public async Task GetAllPropertiesAsync_PercentTempBasal_ChangesScheduledBasalByPercent(
        double percent, double factor)
    {
        var testDData = CreateTestDDataWithActiveTempBasal(new Treatment { Percent = percent });
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(testDData);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var current = (Dictionary<string, object>)((Dictionary<string, object>)result["basal"])["current"];
        Convert.ToDouble(current["totalbasal"])
            .Should().BeApproximately(Convert.ToDouble(current["basal"]) * factor, 1e-9);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_AbsoluteBesidePercent_TakesAbsolute()
    {
        var testDData = CreateTestDDataWithActiveTempBasal(new Treatment { Percent = -50, Absolute = 2.0 });
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(testDData);

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var current = (Dictionary<string, object>)((Dictionary<string, object>)result["basal"])["current"];
        Convert.ToDouble(current["totalbasal"]).Should().Be(2.0);
    }

    [Fact]
    public async Task GetAllPropertiesAsync_WithExpiredTempBasal_ReturnsBasalPropertiesWithoutTempMarker()
    {
        // Arrange
        var testDData = CreateTestDDataWithExpiredTempBasal();
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(testDData);

        // Act
        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.ContainsKey("basal"));

        var basal = result["basal"] as Dictionary<string, object>;
        Assert.NotNull(basal);

        var display = basal["display"] as string;
        Assert.NotNull(display);
        Assert.DoesNotContain("T: ", display); // Should not have temp marker for expired temp basal

        var current = basal["current"] as Dictionary<string, object>;
        Assert.NotNull(current);
        Assert.Null(current["treatment"]); // Should not have treatment for expired temp basal
    }

    private DData CreateTestDData()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return new DData
        {
            LastUpdated = now,
            Sgvs = new List<Entry>
            {
                new Entry
                {
                    Type = "sgv",
                    Mgdl = 120,
                    Mills = now,
                    Direction = "Flat",
                    Scaled = 120,
                },
                new Entry
                {
                    Type = "sgv",
                    Mgdl = 118,
                    Mills = now - 5 * 60 * 1000, // 5 minutes ago
                    Direction = "Flat",
                    Scaled = 118,
                },
            },
            Treatments = new List<Treatment>
            {
                new Treatment
                {
                    EventType = "Meal Bolus",
                    Insulin = 2.5,
                    Carbs = 30,
                    Mills = now - 2 * 60 * 60 * 1000, // 2 hours ago
                },
            },
            DeviceStatus = new List<DeviceStatus>
            {
                new DeviceStatus
                {
                    Device = "uploader",
                    Mills = now,
                    Uploader = new UploaderStatus { Battery = 85 },
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                },
            },
            Profiles = new List<Profile>
            {
                new Profile
                {
                    DefaultProfile = "Default",
                    Mills = now,
                    Store = new Dictionary<string, ProfileData>
                    {
                        ["Default"] = new ProfileData
                        {
                            Basal = new List<TimeValue>
                            {
                                new TimeValue
                                {
                                    Time = "00:00",
                                    Value = 1.0,
                                    TimeAsSeconds = 0,
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    private DData CreateTestDDataWithBasalProfile(List<TimeValue>? schedule = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var basalSchedule = schedule ?? new List<TimeValue>
        {
            new TimeValue
            {
                Time = "00:00",
                Value = 1.0,
                TimeAsSeconds = 0,
            },
            new TimeValue
            {
                Time = "06:00",
                Value = 1.2,
                TimeAsSeconds = 6 * 3600,
            },
            new TimeValue
            {
                Time = "12:00",
                Value = 0.8,
                TimeAsSeconds = 12 * 3600,
            },
            new TimeValue
            {
                Time = "18:00",
                Value = 1.1,
                TimeAsSeconds = 18 * 3600,
            },
        };

        return new DData
        {
            LastUpdated = now,
            Sgvs = new List<Entry>
            {
                new Entry
                {
                    Type = "sgv",
                    Mgdl = 120,
                    Mills = now,
                    Direction = "Flat",
                    Scaled = 120,
                },
            },
            Treatments = new List<Treatment>(),
            Profiles = new List<Profile>
            {
                new Profile
                {
                    DefaultProfile = "Default",
                    Mills = now,
                    Store = new Dictionary<string, ProfileData>
                    {
                        ["Default"] = new ProfileData
                        {
                            Basal = basalSchedule,
                            Dia = 4.0,
                            Units = "mg/dl",
                        },
                    },
                },
            },
        };
    }

    private DData CreateTestDDataWithTempBasal()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var basalData = CreateTestDDataWithBasalProfile();

        // Add active temp basal treatment (percentage-based)
        basalData.Treatments = new List<Treatment>
        {
            new Treatment
            {
                EventType = "Temp Basal",
                Percent = 150, // 150% of base rate
                Duration = 60, // 60 minutes
                Mills = now - 10 * 60 * 1000, // Started 10 minutes ago
                CreatedAt = DateTimeOffset
                    .FromUnixTimeMilliseconds(now - 10 * 60 * 1000)
                    .ToString("O"),
            },
        };

        return basalData;
    }

    private DData CreateTestDDataWithActiveTempBasal(Treatment tempBasal)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var basalData = CreateTestDDataWithBasalProfile();

        tempBasal.EventType = "Temp Basal";
        tempBasal.Duration = 60;
        tempBasal.Mills = now - 10 * 60 * 1000;
        basalData.Treatments = new List<Treatment> { tempBasal };

        return basalData;
    }

    private DData CreateTestDDataWithComboBolus()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var basalData = CreateTestDDataWithBasalProfile();

        // Add active combo bolus treatment
        basalData.Treatments = new List<Treatment>
        {
            new Treatment
            {
                EventType = "Combo Bolus",
                Relative = 0.5, // Additional 0.5 U/h basal
                Duration = 120, // 120 minutes
                Mills = now - 30 * 60 * 1000, // Started 30 minutes ago
                CreatedAt = DateTimeOffset
                    .FromUnixTimeMilliseconds(now - 30 * 60 * 1000)
                    .ToString("O"),
            },
        };

        return basalData;
    }

    private DData CreateTestDDataWithTempBasalAndComboBolus()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var basalData = CreateTestDDataWithBasalProfile();

        // Add both temp basal and combo bolus treatments
        basalData.Treatments = new List<Treatment>
        {
            new Treatment
            {
                EventType = "Temp Basal",
                Absolute = 1.5, // Absolute 1.5 U/h
                Duration = 90, // 90 minutes
                Mills = now - 15 * 60 * 1000, // Started 15 minutes ago
                CreatedAt = DateTimeOffset
                    .FromUnixTimeMilliseconds(now - 15 * 60 * 1000)
                    .ToString("O"),
            },
            new Treatment
            {
                EventType = "Combo Bolus",
                Relative = 0.3, // Additional 0.3 U/h basal
                Duration = 180, // 180 minutes
                Mills = now - 45 * 60 * 1000, // Started 45 minutes ago
                CreatedAt = DateTimeOffset
                    .FromUnixTimeMilliseconds(now - 45 * 60 * 1000)
                    .ToString("O"),
            },
        };

        return basalData;
    }

    private DData CreateTestDDataWithExpiredTempBasal()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var basalData = CreateTestDDataWithBasalProfile();

        // Add expired temp basal treatment
        basalData.Treatments = new List<Treatment>
        {
            new Treatment
            {
                EventType = "Temp Basal",
                Percent = 200, // 200% of base rate
                Duration = 30, // 30 minutes
                Mills = now - 60 * 60 * 1000, // Started 60 minutes ago (expired)
                CreatedAt = DateTimeOffset
                    .FromUnixTimeMilliseconds(now - 60 * 60 * 1000)
                    .ToString("O"),
            },
        };

        return basalData;
    }
}
