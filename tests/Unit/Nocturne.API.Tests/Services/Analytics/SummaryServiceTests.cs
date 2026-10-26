using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Tests.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Unit tests for SummaryService
/// Tests the 1:1 compatibility with legacy JavaScript implementation
/// </summary>
public class SummaryServiceTests
{
    private readonly Mock<IDDataService> _mockDDataService;
    private readonly Mock<IPropertiesService> _mockPropertiesService;
    private readonly Mock<IIobCobReader> _mockIobCobReader = new();
    private readonly Mock<IActiveProfileResolver> _mockActiveProfileResolver;
    private readonly Mock<ITherapySettingsRepository> _mockTherapySettings;
    private readonly Mock<IBasalScheduleRepository> _mockBasal = new();
    private readonly Mock<ICarbRatioScheduleRepository> _mockCarbRatio = new();
    private readonly Mock<ISensitivityScheduleRepository> _mockSensitivity = new();
    private readonly Mock<ITargetRangeScheduleRepository> _mockTargetRange = new();
    private readonly Mock<ILogger<SummaryService>> _mockLogger;
    private readonly SummaryService _service;

    public SummaryServiceTests()
    {
        _mockDDataService = new Mock<IDDataService>();
        _mockPropertiesService = new Mock<IPropertiesService>();
        _mockActiveProfileResolver = ActiveProfileResolverMock.WithDefault();
        _mockTherapySettings = new Mock<ITherapySettingsRepository>();
        _mockTherapySettings
            .Setup(x => x.GetDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockLogger = new Mock<ILogger<SummaryService>>();
        Reading(NoInputs(), new IobResult(), new CobResult());
        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, object>());
        _service = new SummaryService(
            _mockDDataService.Object,
            _mockPropertiesService.Object,
            _mockIobCobReader.Object,
            _mockActiveProfileResolver.Object,
            _mockTherapySettings.Object,
            new ProfileProjectionService(
                _mockTherapySettings.Object,
                _mockBasal.Object,
                _mockCarbRatio.Object,
                _mockSensitivity.Object,
                _mockTargetRange.Object
            ),
            _mockLogger.Object
        );
    }

    [Fact]
    public async Task GetSummaryAsync_WithValidData_ReturnsProcessedSummary()
    {
        // Arrange
        var testDData = CreateTestDData();
        var testProperties = CreateTestProperties();

        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(testDData);

        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(testProperties);

        // Act
        var result = await _service.GetSummaryAsync(6, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Sgvs.Should().HaveCount(2); // Should filter entries within time window
        result.Treatments.Should().NotBeNull();
        result.Profile.Should().NotBeNull();
        result.State.Should().NotBeNull();
    }

    [Fact]
    public async Task GetSummaryAsync_ProfileWritesTheUnsetGiFieldsAsNull()
    {
        var summary = await SummaryJsonAsync(NightAndDay(nightFirst: false), null);

        var profile = summary["profile"]!.AsObject();
        foreach (var field in new[]
        {
            "perGIvalues", "carbs_hr_high", "carbs_hr_medium", "carbs_hr_low",
            "delay_medium", "delay_low",
        })
        {
            profile.ContainsKey(field).Should().BeTrue(field);
            profile[field].Should().BeNull(field);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetSummaryAsync_ProfileIsTheDefaultStoreEntryWithItsOwnKeys(bool nightFirst)
    {
        var summary = await SummaryJsonAsync(NightAndDay(nightFirst), null);

        var profile = summary["profile"]!.AsObject();
        profile["dia"]!.GetValue<double>().Should().Be(5);
        profile["carbs_hr"]!.GetValue<int>().Should().Be(30);
        profile["delay"]!.GetValue<int>().Should().Be(15);
        profile["delay_high"]!.GetValue<int>().Should().Be(45);
        profile["timezone"]!.GetValue<string>().Should().Be("Australia/Sydney");
        profile["units"]!.GetValue<string>().Should().Be("mg/dL");
        profile["carbratio"]![0]!["value"]!.GetValue<double>().Should().Be(12);
        profile["target_low"]![0]!["value"]!.GetValue<double>().Should().Be(90);
        profile["target_high"]![0]!["value"]!.GetValue<double>().Should().Be(110);
        profile["sens"]![0]!["value"]!.GetValue<double>().Should().Be(40);
        profile["basal"]![0]!["value"]!.GetValue<double>().Should().Be(0.9);
        profile["basal"]![0]!["time"]!.GetValue<string>().Should().Be("00:00");
        profile
            .Select(p => p.Key)
            .Should()
            .NotContain(["carbsHr", "carbRatio", "targetLow", "targetHigh", "delayHigh", "delayLow"]);
    }

    [Fact]
    public async Task GetSummaryAsync_ProfileHasNoTimeAsSecondsAtAnyDepth()
    {
        var summary = await SummaryJsonAsync(NightAndDay(nightFirst: true), null);

        summary["profile"]!.ToJsonString().Should().NotContain("timeAsSeconds");
    }

    [Fact]
    public async Task GetSummaryAsync_ActiveProfileSwitchNamesTheStoreEntry()
    {
        var summary = await SummaryJsonAsync(NightAndDay(nightFirst: false), "Night");

        var profile = summary["profile"]!.AsObject();
        profile["carbratio"]![0]!["value"]!.GetValue<double>().Should().Be(8);
        profile["target_low"]![0]!["value"]!.GetValue<double>().Should().Be(100);
    }

    [Fact]
    public async Task GetSummaryAsync_SwitchNamingNoStoredEntryFallsBackToTheDefault()
    {
        var summary = await SummaryJsonAsync(NightAndDay(nightFirst: true), "Exercise");

        summary["profile"]!["carbratio"]![0]!["value"]!.GetValue<double>().Should().Be(12);
    }

    [Fact]
    public async Task GetSummaryAsync_ProfileStartingInTheFutureStillResolves()
    {
        var rows = new List<TherapySettings> { StoredRow("Day", true, 12, 0.9, Minutes(5)) };

        var summary = await SummaryJsonAsync(rows, null);

        summary["profile"]!["carbratio"]![0]!["value"]!.GetValue<double>().Should().Be(12);
    }

    [Fact]
    public async Task GetSummaryAsync_TempBasalsGapFillFromTheResolvedProfile()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ddata = CreateTestDData();
        ddata.Treatments =
        [
            new Treatment
            {
                Mills = now - 3 * 3_600_000,
                EventType = "Temp Basal",
                Duration = 30,
                Absolute = 2.0,
                Created_at = DateTimeOffset
                    .FromUnixTimeMilliseconds(now - 3 * 3_600_000)
                    .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            },
        ];

        var summary = await SummaryJsonAsync(ddata, NightAndDay(nightFirst: true), null);

        var gapFill = summary["treatments"]!["tempBasals"]!
            .AsArray()
            .Where(t => t!["profile"] is not null)
            .Select(t => t!["absolute"]!.GetValue<double>())
            .ToList();
        gapFill.Should().NotBeEmpty().And.OnlyContain(rate => rate == 0.9);
    }

    [Fact]
    public async Task GetSummaryAsync_WithNoProfile_ReturnsAnEmptyObject()
    {
        var summary = await SummaryJsonAsync([], null);

        summary["profile"].Should().BeOfType<JsonObject>();
        summary["profile"]!.AsObject().Count.Should().Be(0);
    }

    private Task<JsonNode> SummaryJsonAsync(List<TherapySettings> rows, string? switchedProfile) =>
        SummaryJsonAsync(CreateTestDData(), rows, switchedProfile);

    private async Task<JsonNode> SummaryJsonAsync(
        DData ddata,
        List<TherapySettings> rows,
        string? switchedProfile
    )
    {
        ddata.Profiles = [];
        ConfigureStore(rows);
        _mockDDataService
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ddata);
        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestProperties());
        _mockActiveProfileResolver
            .Setup(x => x.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(switchedProfile);
        _mockTherapySettings
            .Setup(x => x.GetDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows.Where(r => r.IsDefault).ToList());

        var result = await _service.GetSummaryAsync(6, CancellationToken.None);

        return JsonNode.Parse(JsonSerializer.Serialize(result, NightscoutJsonOptions.Create()))!;
    }

    private static DateTime Minutes(int offset) => DateTime.UtcNow.AddMinutes(offset);

    // A Nightscout document {"Night", "Day"} as decomposition stores it: one row per store, one timestamp.
    private List<TherapySettings> NightAndDay(bool nightFirst)
    {
        var timestamp = Minutes(-1);
        var night = StoredRow("Night", false, 8, 0.7, timestamp, targetLow: 100, targetHigh: 120);
        var day = StoredRow("Day", true, 12, 0.9, timestamp);
        return nightFirst ? [night, day] : [day, night];
    }

    private void ConfigureStore(List<TherapySettings> rows)
    {
        _mockTherapySettings
            .Setup(r => r.GetByProfileNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) => rows.Where(r => r.ProfileName == name).ToList());
        _mockTherapySettings
            .Setup(r => r.GetDocumentRowsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string documentId, CancellationToken _) => rows
                .Where(r => TherapySettings.DocumentIdOf(r) == documentId)
                .ToList());

        foreach (var row in rows)
        {
            var (basalValue, carbRatioValue, low, high) = _storedValues[row.Id];
            var correlationId = row.CorrelationId!.Value;
            _mockTherapySettings
                .Setup(r => r.GetByIdAsync(row.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(row);
            _mockBasal
                .Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new BasalSchedule { ProfileName = row.ProfileName, Entries = [Schedule(basalValue)] }]);
            _mockCarbRatio
                .Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                    [new CarbRatioSchedule { ProfileName = row.ProfileName, Entries = [Schedule(carbRatioValue)] }]
                );
            _mockSensitivity
                .Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                    [new SensitivitySchedule { ProfileName = row.ProfileName, Entries = [Schedule(40)] }]
                );
            _mockTargetRange
                .Setup(r => r.GetByCorrelationIdAsync(correlationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                    [
                        new TargetRangeSchedule
                        {
                            ProfileName = row.ProfileName,
                            Entries = [new TargetRangeEntry { Time = "00:00", Low = low, High = high, TimeAsSeconds = 0 }],
                        },
                    ]
                );
        }
    }

    private static ScheduleEntry Schedule(double value) =>
        new() { Time = "00:00", Value = value, TimeAsSeconds = 0 };

    private readonly Dictionary<Guid, (double Basal, double CarbRatio, double Low, double High)> _storedValues = new();

    private TherapySettings StoredRow(
        string name,
        bool isDefault,
        double carbRatio,
        double basal,
        DateTime timestamp,
        double targetLow = 90,
        double targetHigh = 110
    )
    {
        var row = new TherapySettings
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            ProfileName = name,
            LegacyId = $"doc1:{name}",
            IsDefault = isDefault,
            Timestamp = timestamp,
            CreatedAt = timestamp,
            ModifiedAt = timestamp,
            Timezone = "Australia/Sydney",
            Units = "mg/dL",
            Dia = 5,
            CarbsHr = 30,
            Delay = 15,
            DelayHigh = 45,
        };
        _storedValues[row.Id] = (basal, carbRatio, targetLow, targetHigh);
        return row;
    }

    [Fact]
    public void ProcessSgvs_WithValidEntries_FiltersAndFormatsCorrectly()
    {
        // Arrange
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sgvs = new List<Entry>
        {
            new Entry
            {
                Mills = currentTime - 1000,
                Mgdl = 120,
                Noise = 1,
            },
            new Entry
            {
                Mills = currentTime - 2 * 60 * 60 * 1000,
                Mgdl = 110,
                Noise = 2,
            }, // 2 hours ago
            new Entry { Mills = currentTime - 8 * 60 * 60 * 1000, Mgdl = 100 }, // 8 hours ago, should be filtered out
        };

        // Act
        var result = _service.ProcessSgvs(sgvs, 6);

        // Assert
        result.Should().HaveCount(2);
        result[0].Sgv.Should().Be(110);
        result[0].Noise.Should().Be(2); // Noise != 1 should be included
        result[1].Sgv.Should().Be(120);
        result[1].Noise.Should().BeNull(); // Noise of 1 should be excluded
    }

    [Fact]
    public void ProcessSgvs_GivenNewestFirst_ListsOldestFirst()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sgvs = new List<Entry>
        {
            new() { Mills = now - 1000, Mgdl = 120 },
            new() { Mills = now - 2000, Mgdl = 118 },
            new() { Mills = now - 3000, Mgdl = 116 },
        };

        var result = _service.ProcessSgvs(sgvs, 6);

        result.Select(r => r.Mills).Should().BeInAscendingOrder();
        result.Select(r => r.Sgv).Should().Equal(116, 118, 120);
    }

    [Fact]
    public void ProcessTreatments_WithInsulinAndCarbs_ExtractsCorrectly()
    {
        // Arrange
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new Treatment
            {
                Mills = currentTime - 1000,
                EventType = "Meal Bolus",
                Insulin = 5.5,
                Carbs = 30,
            },
            new Treatment
            {
                Mills = currentTime - 2000,
                EventType = "Correction Bolus",
                Insulin = 2.0,
            },
            new Treatment
            {
                Mills = currentTime - 8 * 60 * 60 * 1000, // 8 hours ago
                EventType = "Meal Bolus",
                Insulin = 3.0,
            },
        };

        // Act
        var result = _service.ProcessTreatments(treatments, null, 6);

        // Assert
        result.Treatments.Should().HaveCount(2); // Should filter out 8-hour old treatment
        result.Treatments[0].Insulin.Should().Be(2.0);
        result.Treatments[0].Carbs.Should().BeNull();
        result.Treatments[1].Insulin.Should().Be(5.5);
        result.Treatments[1].Carbs.Should().Be(30);
    }

    [Fact]
    public void ProcessTreatments_GivenNewestFirst_ListsOldestFirst()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Mills = now - 1000, EventType = "Meal Bolus", Insulin = 5.0 },
            new() { Mills = now - 2000, EventType = "Correction Bolus", Insulin = 2.0 },
        };

        var result = _service.ProcessTreatments(treatments, null, 6);

        result.Treatments.Select(t => t.Mills).Should().BeInAscendingOrder();
        result.Treatments.Select(t => t.Insulin).Should().Equal(2.0, 5.0);
    }

    [Fact]
    public void ProcessTreatments_WithTempBasals_ProcessesCorrectly()
    {
        // Arrange
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new Treatment
            {
                Mills = currentTime - 1000,
                EventType = "Temp Basal",
                Duration = 60, // 60 minutes
                Absolute = 1.5,
                Created_at = DateTimeOffset
                    .FromUnixTimeMilliseconds(currentTime - 1000)
                    .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            },
        };
        var profileData = new ProfileData
        {
            Basal = new List<TimeValue>
            {
                new TimeValue { Time = "00:00", Value = 1.0 },
                new TimeValue { Time = "06:00", Value = 1.2 },
            },
        };

        // Act
        var result = _service.ProcessTreatments(treatments, profileData, 6);

        // Assert
        result.TempBasals.Should().NotBeEmpty();
    }

    [Fact]
    public void ProcessTreatments_WithTemporaryTargets_ExtractsCorrectly()
    {
        // Arrange
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new Treatment
            {
                Mills = currentTime - 1000,
                EventType = "Temporary Target",
                TargetTop = 180.5,
                TargetBottom = 100.3,
                Duration = 120, // 120 minutes
            },
        };

        // Act
        var result = _service.ProcessTreatments(treatments, null, 6);

        // Assert
        result.Targets.Should().HaveCount(1);
        result.Targets[0].TargetTop.Should().Be(181); // Should be rounded
        result.Targets[0].TargetBottom.Should().Be(100); // Should be rounded
        result.Targets[0].Duration.Should().Be(7200); // Should be converted to seconds (120 * 60)
    }

    [Fact]
    public async Task ConstructStateAsync_WithValidProperties_ReturnsCorrectState()
    {
        // Arrange
        var properties = CreateTestProperties();
        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(properties);

        Reading(
            new IobCobInputs([new Bolus { Timestamp = DateTime.UtcNow, Insulin = 1 }], [], [new CarbIntake { Timestamp = DateTime.UtcNow, Carbs = 10 }]),
            new IobResult { Iob = 2.5 },
            new CobResult { Cob = 25 }
        );

        // Act
        var result = await _service.ConstructStateAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Iob.Should().Be(2.5);
        result.Cob.Should().Be(25);
        result.Bwp.Should().Be(1.23);
        result.Cage.Should().Be(48);
        result.Sage.Should().Be(72);
        result.Battery.Should().Be(85);
    }

    [Fact]
    public async Task ConstructStateAsync_WithNoIobCobData_WritesNullIobNullCobAndZeroBwp()
    {
        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, object>());

        var json = await StateJsonAsync();

        json.ContainsKey("iob").Should().BeTrue();
        json["iob"].Should().BeNull();
        json.ContainsKey("cob").Should().BeTrue();
        json["cob"].Should().BeNull();
        json["bwp"]!.GetValue<double>().Should().Be(0);
    }

    [Fact]
    public async Task ConstructStateAsync_WithZeroIobAndCobFromADevice_WritesZeroNotNull()
    {
        Reading(
            NoInputs(),
            new IobResult { Iob = 0, ReportedByDevice = true },
            new CobResult { Cob = 0, ReportedByDevice = true }
        );

        var json = await StateJsonAsync();

        json["iob"]!.GetValue<double>().Should().Be(0);
        json["cob"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task ConstructStateAsync_WithTreatmentsThatLeaveNothingOnBoard_WritesZeroNotNull()
    {
        Reading(
            new IobCobInputs(
                [new Bolus { Timestamp = DateTime.UtcNow, Insulin = 1 }],
                [],
                [new CarbIntake { Timestamp = DateTime.UtcNow, Carbs = 10 }]
            ),
            new IobResult { Iob = 0 },
            new CobResult { Cob = 0 }
        );

        var json = await StateJsonAsync();

        json["iob"]!.GetValue<double>().Should().Be(0);
        json["cob"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task ConstructStateAsync_WithTempBasalsButNoCarbs_WritesIobAndNullCob()
    {
        Reading(
            new IobCobInputs([], [new TempBasal { StartTimestamp = DateTime.UtcNow, Rate = 1 }], []),
            new IobResult { Iob = -0.4 },
            new CobResult()
        );

        var json = await StateJsonAsync();

        json["iob"]!.GetValue<double>().Should().Be(-0.4);
        json["cob"].Should().BeNull();
    }

    [Fact]
    public async Task ConstructStateAsync_WithAFailedCobCalculation_WritesNullCob()
    {
        Reading(
            new IobCobInputs([new Bolus { Timestamp = DateTime.UtcNow, Insulin = 1 }], [], [new CarbIntake { Timestamp = DateTime.UtcNow, Carbs = 10 }]),
            new IobResult { Iob = 1 },
            null
        );

        var json = await StateJsonAsync();

        json["iob"]!.GetValue<double>().Should().Be(1);
        json["cob"].Should().BeNull();
    }

    [Theory]
    [InlineData(2.3456, 24.6, 2.35, 25)]
    [InlineData(0.125, 24.5, 0.13, 25)]
    [InlineData(-0.125, 0.5, -0.12, 1)]
    [InlineData(2.344, 24.4, 2.34, 24)]
    public async Task ConstructStateAsync_RoundsIobAndCobHalfUpAsJavaScriptDoes(
        double iob,
        double cob,
        double expectedIob,
        int expectedCob
    )
    {
        Reading(
            new IobCobInputs(
                [new Bolus { Timestamp = DateTime.UtcNow, Insulin = 1 }],
                [],
                [new CarbIntake { Timestamp = DateTime.UtcNow, Carbs = 10 }]
            ),
            new IobResult { Iob = iob },
            new CobResult { Cob = cob }
        );

        var json = await StateJsonAsync();

        json["iob"]!.GetValue<double>().Should().Be(expectedIob);
        json["cob"]!.GetValue<int>().Should().Be(expectedCob);
    }

    private static IobCobInputs NoInputs() => new([], [], []);

    private void Reading(IobCobInputs inputs, IobResult iob, CobResult? cob) =>
        _mockIobCobReader
            .Setup(r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobCobReading(iob, cob, inputs)
            {
                InsulinInWindow = inputs.Boluses.Count > 0 || inputs.TempBasals.Count > 0,
            });

    [Fact]
    public async Task ConstructStateAsync_WhenTheReaderThrows_WritesNullIobAndCobAndKeepsTheSummary()
    {
        _mockIobCobReader
            .Setup(r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("therapy settings unavailable"));

        var json = await StateJsonAsync();

        json.ContainsKey("iob").Should().BeTrue();
        json["iob"].Should().BeNull();
        json.ContainsKey("cob").Should().BeTrue();
        json["cob"].Should().BeNull();
    }

    private async Task<JsonObject> StateJsonAsync()
    {
        var state = await _service.ConstructStateAsync(CancellationToken.None);
        return JsonNode.Parse(JsonSerializer.Serialize(state, NightscoutJsonOptions.Create()))!.AsObject();
    }

    [Fact]
    public void ProcessTempBasals_PercentTemp_ReportsTheRateItResolvedTo()
    {
        var profile = new ProfileData
        {
            Basal = new List<TimeValue> { new() { Time = "00:00", Value = 0.6 } },
        };
        var start = DateTimeOffset.UtcNow.AddMinutes(-40).ToUnixTimeMilliseconds();
        var temps = new List<Treatment>
        {
            new() { EventType = "Temp Basal", Mills = start, Duration = 30, Percent = -50, DeliveredRate = 0.4 },
        };

        var result = _service.ProcessTempBasals(profile, temps, start - 1);

        result.Should().Contain(t => t.Start == start).Which.Absolute.Should().Be(0.4);
    }

    [Fact]
    public void ProcessTempBasals_PercentTempOfUnknownRate_LeavesTheProfileBasal()
    {
        var profile = new ProfileData
        {
            Basal = new List<TimeValue> { new() { Time = "00:00", Value = 0.6 } },
        };
        var start = DateTimeOffset.UtcNow.AddMinutes(-40).ToUnixTimeMilliseconds();
        var temps = new List<Treatment>
        {
            new() { EventType = "Temp Basal", Mills = start, Duration = 30, Percent = -50 },
        };

        var result = _service.ProcessTempBasals(profile, temps, start - 1);

        result.Should().NotContain(t => t.Absolute == 0);
    }

    [Fact]
    public void FilterSameAbsoluteTemps_WithIdenticalRates_MergesCorrectly()
    {
        // Arrange
        var tempBasals = new List<SummaryTempBasal>
        {
            new SummaryTempBasal
            {
                Start = 1000,
                Duration = 3600,
                Absolute = 1.5,
            }, // 1 hour
            new SummaryTempBasal
            {
                Start = 4600,
                Duration = 1800,
                Absolute = 1.5,
            }, // 30 minutes, same rate
            new SummaryTempBasal
            {
                Start = 6400,
                Duration = 1800,
                Absolute = 2.0,
            }, // 30 minutes, different rate
        };

        // Act
        var result = _service.FilterSameAbsoluteTemps(tempBasals);

        // Assert
        result.Should().HaveCount(2); // First two should be merged
        result[0].Duration.Should().Be(5400); // 3600 + 1800 = 5400
        result[0].Absolute.Should().Be(1.5);
        result[1].Absolute.Should().Be(2.0);
    }

    [Fact]
    public void GetProfileBasalsInWindow_WithValidProfile_ReturnsCorrectBasals()
    {
        // Arrange
        var basals = new List<TimeValue>
        {
            new TimeValue { Time = "00:00", Value = 1.0 },
            new TimeValue { Time = "06:00", Value = 1.2 },
            new TimeValue { Time = "12:00", Value = 1.5 },
            new TimeValue { Time = "18:00", Value = 1.3 },
        };

        var start = DateTimeOffset.Parse("2023-01-01T08:00:00Z").ToUnixTimeMilliseconds();
        var end = DateTimeOffset.Parse("2023-01-01T14:00:00Z").ToUnixTimeMilliseconds();

        // Act
        var result = _service.GetProfileBasalsInWindow(basals, start, end);

        // Assert
        result.Should().NotBeEmpty();
        result.All(b => b.Profile == 1).Should().BeTrue(); // All should be marked as profile basals
        result.Should().Contain(b => b.Absolute == 1.2); // Should include 06:00 rate
        result.Should().Contain(b => b.Absolute == 1.5); // Should include 12:00 rate
    }

    private DData CreateTestDData()
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return new DData
        {
            Sgvs = new List<Entry>
            {
                new Entry
                {
                    Mills = currentTime - 1000,
                    Mgdl = 120,
                    Noise = 1,
                },
                new Entry
                {
                    Mills = currentTime - 2 * 60 * 60 * 1000,
                    Mgdl = 110,
                    Noise = 2,
                },
                new Entry { Mills = currentTime - 8 * 60 * 60 * 1000, Mgdl = 100 },
            },
            Treatments = new List<Treatment>
            {
                new Treatment
                {
                    Mills = currentTime - 1000,
                    EventType = "Meal Bolus",
                    Insulin = 5.5,
                    Carbs = 30,
                },
            },
            Profiles = new List<Profile>
            {
                new Profile
                {
                    Mills = currentTime,
                    DefaultProfile = "Default",
                    Store = new Dictionary<string, ProfileData>
                    {
                        ["Default"] = new ProfileData
                        {
                            Units = "mg/dl",
                            Basal = new List<TimeValue>
                            {
                                new TimeValue { Time = "00:00", Value = 1.0 },
                            },
                        },
                    },
                },
            },
        };
    }

    private Dictionary<string, object> CreateTestProperties()
    {
        return new Dictionary<string, object>
        {
            ["bwp"] = new Dictionary<string, object> { ["bolusEstimate"] = 1.234 },
            ["cage"] = new Dictionary<string, object> { ["age"] = 48 },
            ["sage"] = new Dictionary<string, object> { ["age"] = 72 },
            ["iage"] = new Dictionary<string, object> { ["age"] = 96 },
            ["bage"] = new Dictionary<string, object> { ["age"] = 24 },
            ["upbat"] = new Dictionary<string, object> { ["level"] = 85 },
        };
    }

    [Fact]
    public async Task ConstructStateAsync_WithCalibrationAge_IncludesCalibrationAge()
    {
        // Arrange
        var properties = new Dictionary<string, object>
        {
            ["calib"] = new Dictionary<string, object> { ["age"] = 12 }, // 12 hours since last calibration
        };

        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(properties);

        // Act
        var result = await _service.ConstructStateAsync(CancellationToken.None);

        // Assert
        result.CalibAge.Should().Be(12);
    }

    [Fact]
    public async Task ConstructStateAsync_WithoutCalibrationAge_CalibrationAgeIsNull()
    {
        // Arrange
        var properties = new Dictionary<string, object>();

        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(properties);

        // Act
        var result = await _service.ConstructStateAsync(CancellationToken.None);

        // Assert
        result.CalibAge.Should().BeNull();
    }

    [Fact]
    public async Task ConstructStateAsync_WithSensorAge_CalculatesSensorExpiration()
    {
        // Arrange
        var properties = new Dictionary<string, object>
        {
            ["sage"] = new Dictionary<string, object> { ["age"] = 120 }, // 120 hours (5 days) since sensor start
        };

        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(properties);

        // Act
        var result = await _service.ConstructStateAsync(CancellationToken.None);

        // Assert
        result.Sage.Should().Be(120);
        result.SensorExpiresIn.Should().Be(120); // 240 - 120 = 120 hours remaining
    }

    [Fact]
    public async Task ConstructStateAsync_WithOldSensorAge_SensorExpiresInIsZero()
    {
        // Arrange
        var properties = new Dictionary<string, object>
        {
            ["sage"] = new Dictionary<string, object> { ["age"] = 300 }, // 300 hours (12.5 days) - expired
        };

        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(properties);

        // Act
        var result = await _service.ConstructStateAsync(CancellationToken.None);

        // Assert
        result.Sage.Should().Be(300);
        result.SensorExpiresIn.Should().Be(0); // Sensor already expired
    }

    [Fact]
    public async Task ConstructStateAsync_WithoutSensorAge_SensorExpiresInIsNull()
    {
        // Arrange
        var properties = new Dictionary<string, object>();

        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(properties);

        // Act
        var result = await _service.ConstructStateAsync(CancellationToken.None);

        // Assert
        result.Sage.Should().BeNull();
        result.SensorExpiresIn.Should().BeNull();
    }

    [Fact]
    public async Task ConstructStateAsync_WithAllNewProperties_IncludesAllValues()
    {
        // Arrange
        var properties = new Dictionary<string, object>
        {
            ["cage"] = new Dictionary<string, object> { ["age"] = 48 },
            ["sage"] = new Dictionary<string, object> { ["age"] = 72 },
            ["iage"] = new Dictionary<string, object> { ["age"] = 96 },
            ["bage"] = new Dictionary<string, object> { ["age"] = 24 },
            ["calib"] = new Dictionary<string, object> { ["age"] = 6 },
            ["upbat"] = new Dictionary<string, object> { ["level"] = 85 },
        };

        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(properties);

        // Act
        var result = await _service.ConstructStateAsync(CancellationToken.None);

        // Assert
        result.Cage.Should().Be(48);
        result.Sage.Should().Be(72);
        result.Iage.Should().Be(96);
        result.Bage.Should().Be(24);
        result.CalibAge.Should().Be(6);
        result.SensorExpiresIn.Should().Be(168); // 240 - 72 = 168 hours remaining
        result.Battery.Should().Be(85);
    }

    [Fact]
    public async Task ConstructStateAsync_LeavesBatteryNull_WhenUpbatHasNoLevel()
    {
        var properties = new Dictionary<string, object>
        {
            ["upbat"] = new Dictionary<string, object> { ["devices"] = new List<object>() },
        };

        _mockPropertiesService
            .Setup(x => x.GetAllPropertiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(properties);

        var result = await _service.ConstructStateAsync(CancellationToken.None);

        result.Battery.Should().BeNull();
    }

    private Dictionary<string, object> CreateTestPropertiesWithCalibration()
    {
        return new Dictionary<string, object>
        {
            ["bwp"] = new Dictionary<string, object> { ["bolusEstimate"] = 1.234 },
            ["cage"] = new Dictionary<string, object> { ["age"] = 48 },
            ["sage"] = new Dictionary<string, object> { ["age"] = 72 },
            ["iage"] = new Dictionary<string, object> { ["age"] = 96 },
            ["bage"] = new Dictionary<string, object> { ["age"] = 24 },
            ["calib"] = new Dictionary<string, object> { ["age"] = 12 },
            ["upbat"] = new Dictionary<string, object> { ["level"] = 85 },
        };
    }
}
