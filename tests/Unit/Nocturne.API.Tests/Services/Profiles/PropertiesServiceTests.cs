using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
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
    private readonly PropertiesService _service;

    public PropertiesServiceTests()
    {
        _mockDDataService = new Mock<IDDataService>();
        _mockLogger = new Mock<ILogger<PropertiesService>>();
        var mockIobCalculator = new Mock<IIobCalculator>();
        var mockCobCalculator = new Mock<ICobCalculator>();
        var mockBolusRepo = new Mock<IBolusRepository>();
        var mockCarbIntakeRepo = new Mock<ICarbIntakeRepository>();
        var mockTempBasalRepo = new Mock<ITempBasalRepository>();
        var mockAr2Service = new Mock<IAr2Service>();

        _mockDeviceAgeService = new Mock<IDeviceAgeService>();
        _mockDeviceAgeService
            .Setup(x => x.GetCannulaAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceAgeInfo { Found = true, Age = 10, Days = 0, Hours = 10 });
        _mockDeviceAgeService
            .Setup(x => x.GetSensorAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorAgeInfo());
        _mockDeviceAgeService
            .Setup(x => x.GetInsulinAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceAgeInfo { Found = true, Age = 30, Days = 1, Hours = 6 });
        _mockDeviceAgeService
            .Setup(x => x.GetBatteryAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceAgeInfo());

        _service = new PropertiesService(
            _mockDDataService.Object,
            _mockLogger.Object,
            mockIobCalculator.Object,
            mockCobCalculator.Object,
            mockBolusRepo.Object,
            mockCarbIntakeRepo.Object,
            mockTempBasalRepo.Object,
            mockAr2Service.Object,
            _mockDeviceAgeService.Object
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
            .Setup(x => x.GetCannulaAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceAgeInfo { Found = false, Age = 0, Days = 0, Hours = 0, Level = 0 });
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestDData());

        var result = await _service.GetAllPropertiesAsync(CancellationToken.None);

        var json = JsonSerializer.Serialize(result, NightscoutJsonOptions.Create());
        using var doc = JsonDocument.Parse(json);
        var cage = doc.RootElement.GetProperty("cage");

        Assert.False(cage.GetProperty("found").GetBoolean());
        Assert.Equal(0, cage.GetProperty("age").GetInt32());
        Assert.Equal(0, cage.GetProperty("days").GetInt32());
        Assert.Equal(0, cage.GetProperty("level").GetInt32());
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
            x => x.GetCannulaAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await _service.GetPropertiesAsync(new[] { "cage" }, CancellationToken.None);

        _mockDeviceAgeService.Verify(
            x => x.GetCannulaAgeAsync(It.IsAny<DeviceAgePreferences>(), It.IsAny<CancellationToken>()),
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
        Assert.Equal(72, upbat["level"]);

        var devices = Assert.IsType<List<Dictionary<string, object?>>>(upbat["devices"]);
        Assert.Single(devices);
        Assert.Equal("phone-a", devices[0]["device"]);
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

    private DData CreateTestDDataWithBasalProfile()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Ensure TimeAsSeconds is calculated for basal schedule
        var basalSchedule = new List<TimeValue>
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
