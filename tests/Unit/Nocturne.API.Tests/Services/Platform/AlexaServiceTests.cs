using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

/// <summary>
/// Unit tests for AlexaService
/// Tests maintain 1:1 compatibility with legacy Alexa implementation
/// </summary>
[Parity("api.alexa.test.js")]
public class AlexaServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const string UnknownIntentText =
        "I'm sorry, I didn't understand that request. Please try again.";
    private const string UnknownValueText =
        "That value is unknown at the moment. Please see your Nightscout site for more details.";

    private readonly Mock<IEntryStore> _mockEntryStore = new();
    private readonly Mock<IPropertiesService> _mockProperties = new();
    private readonly Mock<IDDataService> _mockDData = new();
    private readonly Mock<IShareAppearanceReader> _mockAppearance = new();
    private readonly HttpContextAccessor _httpContextAccessor = new();
    private readonly AlexaService _alexaService;

    public AlexaServiceTests()
    {
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        Grant(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.TherapyRead);

        _mockDData
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DData());
        Units(null);
        _alexaService = new AlexaService(
            _mockEntryStore.Object,
            _mockProperties.Object,
            _mockDData.Object,
            _httpContextAccessor,
            _mockAppearance.Object,
            Mock.Of<ITenantAccessor>(),
            new FakeTimeProvider(Now),
            Mock.Of<ILogger<AlexaService>>()
        );
    }

    private void Units(string? glucoseUnits) =>
        _mockAppearance
            .Setup(x => x.GetSharedAppearanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences { GlucoseUnits = glucoseUnits });

    private void Grant(params string[] scopes) =>
        _httpContextAccessor.HttpContext!.SetGrantedScopes(new HashSet<string>(scopes));

    private void LatestEntry(double sgv, string direction, int minutesAgo) =>
        LatestEntry(sgv, direction, TimeSpan.FromMinutes(minutesAgo));

    private void LatestEntry(double sgv, string direction, TimeSpan ago) =>
        _mockEntryStore
            .Setup(x =>
                x.QueryAsync(
                    It.Is<EntryQuery>(q => q.Type == "sgv" && q.Count == 1),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new[]
                {
                    new Entry
                    {
                        Sgv = sgv,
                        Direction = direction,
                        Mills = (Now - ago).ToUnixTimeMilliseconds(),
                    },
                }
            );

    private void Properties(params (string Name, object Value)[] properties) =>
        _mockProperties
            .Setup(x =>
                x.GetPropertiesAsync(
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(properties.ToDictionary(p => p.Name, p => p.Value));

    private void DeviceStatuses(params DeviceStatus[] statuses) =>
        _mockDData
            .Setup(x => x.GetDDataAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DData { DeviceStatus = [.. statuses] });

    private static string Iso(int minutesAgo) =>
        Now.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static OpenApsEnacted Enacted(int minutesAgo, bool received = true) =>
        new() { Timestamp = Iso(minutesAgo), Received = received };

    private static long MillsAgo(int minutes) => Now.AddMinutes(-minutes).ToUnixTimeMilliseconds();

    private static Dictionary<string, object?> Reading(string key, double value) =>
        new() { [key] = value };

    private static Dictionary<string, object> Basal(double totalBasal, Treatment? tempBasal = null) =>
        new()
        {
            ["current"] = new Dictionary<string, object>
            {
                ["totalbasal"] = totalBasal,
                ["treatment"] = tempBasal!,
            },
        };

    private static Dictionary<string, object> Forecast(
        params (int MinutesAhead, double Mgdl)[] points
    ) =>
        new()
        {
            ["forecast"] = new Dictionary<string, object>
            {
                ["predicted"] = points
                    .Select(p => new
                    {
                        mills = Now.AddMinutes(p.MinutesAhead).ToUnixTimeMilliseconds(),
                        mgdl = p.Mgdl,
                        color = "red",
                    })
                    .ToArray(),
            },
        };

    private static AlexaSlot ResolvedMetric(
        string utterance,
        string canonical,
        string status = "ER_SUCCESS_MATCH"
    ) =>
        new()
        {
            Name = "metric",
            Value = utterance,
            Resolutions = new AlexaSlotResolutions
            {
                ResolutionsPerAuthority =
                [
                    new AlexaResolutionAuthority
                    {
                        Status = new AlexaResolutionStatus { Code = status },
                        Values =
                        [
                            new AlexaResolutionValue
                            {
                                Value = new AlexaResolvedValue { Name = canonical },
                            },
                        ],
                    },
                ],
            },
        };

    private async Task<AlexaResponse> MetricNow(AlexaSlot metric, AlexaSlot? pwd = null)
    {
        var intent = new AlexaIntent
        {
            Name = "MetricNow",
            Slots = new Dictionary<string, AlexaSlot> { ["metric"] = metric },
        };
        if (pwd != null)
        {
            intent.Slots["pwd"] = pwd;
        }

        return await Process(intent);
    }

    private async Task<AlexaResponse> Intent(string name) =>
        await Process(new AlexaIntent { Name = name });

    private async Task<AlexaResponse> Process(AlexaIntent intent) =>
        await _alexaService.ProcessRequestAsync(
            new AlexaRequest
            {
                Request = new AlexaRequestDetails
                {
                    Type = "IntentRequest",
                    Locale = "en-US",
                    Intent = intent,
                },
            },
            CancellationToken.None
        );

    private static string? Speech(AlexaResponse response) => response.Response.OutputSpeech?.Text;

    [Fact]
    public async Task ProcessRequestAsync_LaunchRequest_ReturnsSpeechletResponse()
    {
        var request = new AlexaRequest
        {
            Request = new AlexaRequestDetails { Type = "LaunchRequest", Locale = "en-US" },
        };

        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.False(result.Response.ShouldEndSession);
        Assert.Contains("Hello", result.Response.OutputSpeech?.Text ?? "");
    }

    [Fact]
    public async Task ProcessRequestAsync_SessionEndedRequest_ReturnsEmptyResponse()
    {
        var request = new AlexaRequest
        {
            Request = new AlexaRequestDetails { Type = "SessionEndedRequest" },
        };

        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.True(result.Response.ShouldEndSession);
        Assert.Null(result.Response.OutputSpeech);
    }

    [Fact]
    public async Task MetricNow_ResolvedToBg_SpeaksGlucoseDirectionAndAge()
    {
        LatestEntry(120, "Flat", minutesAgo: 5);

        var result = await MetricNow(ResolvedMetric("blood sugar", "bg"));

        Assert.Equal("120 and holding as of 5 minutes ago.", Speech(result));
        Assert.Equal("Current BG", result.Response.Card?.Title);
        Assert.True(result.Response.ShouldEndSession);
    }

    [Theory]
    [InlineData("blood glucose")]
    [InlineData("number")]
    public async Task MetricNow_BgAliases_SpeakGlucose(string canonical)
    {
        LatestEntry(95, "SingleDown", minutesAgo: 1);

        var result = await MetricNow(ResolvedMetric("my sugar", canonical));

        Assert.Equal("95 and dropping as of a minute ago.", Speech(result));
    }

    [Fact]
    public async Task MetricNow_UnresolvedSlot_FallsBackToTheRawValue()
    {
        LatestEntry(120, "FortyFiveUp", minutesAgo: 2);

        var result = await MetricNow(new AlexaSlot { Name = "metric", Value = "bg" });

        Assert.Equal("120 and slightly rising as of 2 minutes ago.", Speech(result));
    }

    [Fact]
    public async Task MetricNow_ResolutionWinsOverTheRawValue()
    {
        LatestEntry(120, "Flat", minutesAgo: 5);
        Properties(("iob", Reading("iob", 1.25)));

        var result = await MetricNow(ResolvedMetric("bg", "iob"));

        Assert.Equal("You have 1.25 units of insulin on board", Speech(result));
    }

    [Fact]
    public async Task MetricNow_Bg_WithoutReadings_SpeaksUnknownValue()
    {
        _mockEntryStore
            .Setup(x => x.QueryAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Entry>());

        var result = await MetricNow(ResolvedMetric("bg", "bg"));

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task MetricNow_Iob_SpeaksUnitsOnBoard()
    {
        Properties(("iob", Reading("iob", 1.25)));

        var result = await MetricNow(ResolvedMetric("insulin on board", "iob"));

        Assert.Equal("You have 1.25 units of insulin on board", Speech(result));
        Assert.Equal("Current IOB", result.Response.Card?.Title);
    }

    [Fact]
    public async Task MetricNow_Iob_WithNoInsulin_SpeaksNo()
    {
        Properties(("iob", Reading("iob", 0)));

        var result = await MetricNow(ResolvedMetric("iob", "iob"));

        Assert.Equal("You have no insulin on board", Speech(result));
    }

    [Fact]
    public async Task MetricNow_Cob_SpeaksCarbohydrates()
    {
        Properties(("cob", Reading("cob", 24)));

        var result = await MetricNow(ResolvedMetric("carbs on board", "cob"));

        Assert.Equal("You have 24 carbohydrates on board", Speech(result));
        Assert.Equal("Current COB", result.Response.Card?.Title);
    }

    [Fact]
    public async Task MetricNow_Cob_ForAnotherPerson_SpeaksThirdPerson()
    {
        Properties(("cob", Reading("cob", 24)));

        var result = await MetricNow(
            ResolvedMetric("cob", "cob"),
            new AlexaSlot { Name = "pwd", Value = "Sam's" }
        );

        Assert.Equal("Sam has 24 carbohydrates on board", Speech(result));
    }

    [Fact]
    public async Task MetricNow_Basal_SpeaksScheduledRate()
    {
        Properties(("basal", Basal(0.9)));

        var result = await MetricNow(ResolvedMetric("basal", "basal"));

        Assert.Equal("Your current basal is 0.9 units per hour", Speech(result));
        Assert.Equal("Current Basal", result.Response.Card?.Title);
    }

    [Fact]
    public async Task MetricNow_Basal_WithTempBasal_SpeaksWhenItEnds()
    {
        var tempBasal = new Treatment { Mills = MillsAgo(10), Duration = 30 };
        Properties(("basal", Basal(1.5, tempBasal)));

        var result = await MetricNow(ResolvedMetric("current basal", "current basal"));

        Assert.Equal(
            "Your temp basal of 1.5 units per hour will end in 20 minutes",
            Speech(result)
        );
    }

    [Fact]
    public async Task MetricNow_PumpReservoir_SpeaksUnitsRemaining()
    {
        DeviceStatuses(
            new DeviceStatus { Mills = MillsAgo(3), Pump = new PumpStatus { Reservoir = 150.5 } }
        );

        var result = await MetricNow(ResolvedMetric("pump reservoir", "pump reservoir"));

        Assert.Equal("You have 150.5 units remaining", Speech(result));
        Assert.Equal("Insulin Remaining", result.Response.Card?.Title);
    }

    [Fact]
    public async Task MetricNow_PumpReservoir_SkipsNewerStatusesWithoutAReading()
    {
        DeviceStatuses(
            new DeviceStatus { Mills = MillsAgo(1), Pump = new PumpStatus() },
            new DeviceStatus { Mills = MillsAgo(6), Pump = new PumpStatus { Reservoir = 80 } }
        );

        var result = await MetricNow(ResolvedMetric("pump reservoir", "pump reservoir"));

        Assert.Equal("You have 80 units remaining", Speech(result));
    }

    [Fact]
    public async Task MetricNow_PumpReservoir_WithoutPumpData_SpeaksUnknownValue()
    {
        var result = await MetricNow(ResolvedMetric("pump reservoir", "pump reservoir"));

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task MetricNow_PumpBattery_SpeaksPercent()
    {
        DeviceStatuses(
            new DeviceStatus
            {
                Mills = MillsAgo(3),
                Pump = new PumpStatus { Battery = new PumpBattery { Percent = 80 } },
            }
        );

        var result = await MetricNow(ResolvedMetric("pump battery", "pump battery"));

        Assert.Equal("Your pump battery is at 80 percent", Speech(result));
        Assert.Equal("Pump Battery", result.Response.Card?.Title);
    }

    [Fact]
    public async Task MetricNow_UploaderBattery_SpeaksDisplay()
    {
        Properties(("upbat", new Dictionary<string, object> { ["display"] = "85%" }));

        var result = await MetricNow(ResolvedMetric("uploader battery", "uploader battery"));

        Assert.Equal("Your uploader battery is at 85%", Speech(result));
        Assert.Equal("Uploader Battery", result.Response.Card?.Title);
    }

    [Fact]
    public async Task MetricNow_UploaderBattery_WithoutReading_SpeaksUnknownValue()
    {
        Properties();

        var result = await MetricNow(ResolvedMetric("uploader battery", "uploader battery"));

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task MetricNow_Ar2Forecast_SpeaksRange()
    {
        Properties(("ar2", Forecast((5, 110), (10, 130), (30, 125))));

        var result = await MetricNow(ResolvedMetric("forecast", "ar2 forecast"));

        Assert.Equal(
            "According to the AR2 forecast you are expected to be between 110 and 130 over the next in 30 minutes",
            Speech(result)
        );
        Assert.Equal("AR2 Forecast", result.Response.Card?.Title);
    }

    [Fact]
    public async Task MetricNow_Ar2Forecast_WithFlatForecast_SpeaksAround()
    {
        Properties(("ar2", Forecast((5, 118), (10, 118))));

        var result = await MetricNow(ResolvedMetric("forecast", "forecast"));

        Assert.Equal(
            "According to the AR2 forecast you are expected to be around 118 over the next in 10 minutes",
            Speech(result)
        );
    }

    [Fact]
    public async Task LastLoop_WithLoopStatus_SpeaksTheLoopTimestampNotTheUploadTime()
    {
        DeviceStatuses(
            new DeviceStatus
            {
                Mills = MillsAgo(1),
                Loop = new LoopStatus { Timestamp = Iso(7) },
            }
        );

        var result = await Intent("LastLoop");

        Assert.Equal("The last successful loop was 7 minutes ago", Speech(result));
        Assert.Equal("Last Loop", result.Response.Card?.Title);
    }

    [Fact]
    public async Task LastLoop_LoopStatusWithoutATimestamp_IsNotALoop()
    {
        DeviceStatuses(new DeviceStatus { Mills = MillsAgo(1), Loop = new LoopStatus() });

        var result = await Intent("LastLoop");

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task LastLoop_WithEnactedOpenApsStatus_SpeaksTheEnactedTime()
    {
        DeviceStatuses(
            new DeviceStatus
            {
                Mills = MillsAgo(1),
                OpenAps = new OpenApsStatus { Enacted = Enacted(3) },
            }
        );

        var result = await Intent("LastLoop");

        Assert.Equal("The last successful loop was 3 minutes ago", Speech(result));
    }

    [Fact]
    public async Task LastLoop_OpenApsMillsWinOverTheTimestamp()
    {
        var enacted = Enacted(30);
        enacted.Mills = MillsAgo(4);
        DeviceStatuses(
            new DeviceStatus
            {
                Mills = MillsAgo(1),
                OpenAps = new OpenApsStatus { Enacted = enacted },
            }
        );

        var result = await Intent("LastLoop");

        Assert.Equal("The last successful loop was 4 minutes ago", Speech(result));
    }

    [Fact]
    public async Task LastLoop_WithSuggestedOnly_SpeaksTheSuggestedTime()
    {
        DeviceStatuses(
            new DeviceStatus
            {
                Mills = MillsAgo(1),
                OpenAps = new OpenApsStatus
                {
                    Suggested = new OpenApsSuggested { Timestamp = Iso(9) },
                },
            }
        );

        var result = await Intent("LastLoop");

        Assert.Equal("The last successful loop was 9 minutes ago", Speech(result));
    }

    [Fact]
    public async Task LastLoop_OpenApsStatusWithoutEnactedOrSuggested_IsNotALoop()
    {
        DeviceStatuses(
            new DeviceStatus
            {
                Mills = MillsAgo(1),
                OpenAps = new OpenApsStatus { Iob = new OpenApsIobData() },
            }
        );

        var result = await Intent("LastLoop");

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task LastLoop_EnactedThatWasNotReceived_IsNotALoop()
    {
        DeviceStatuses(
            new DeviceStatus
            {
                Mills = MillsAgo(1),
                OpenAps = new OpenApsStatus { Enacted = Enacted(2, received: false) },
            }
        );

        var result = await Intent("LastLoop");

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task LastLoop_SkipsLoopStatusesThatFailed()
    {
        DeviceStatuses(
            new DeviceStatus
            {
                Mills = MillsAgo(2),
                Loop = new LoopStatus { Timestamp = Iso(2), FailureReason = "Pump unreachable" },
            },
            new DeviceStatus
            {
                Mills = MillsAgo(12),
                Loop = new LoopStatus { Timestamp = Iso(12) },
            }
        );

        var result = await Intent("LastLoop");

        Assert.Equal("The last successful loop was 12 minutes ago", Speech(result));
    }

    [Fact]
    public async Task LastLoop_SpeaksTheNewestOfLoopAndOpenAps()
    {
        DeviceStatuses(
            new DeviceStatus { Mills = MillsAgo(8), Loop = new LoopStatus { Timestamp = Iso(8) } },
            new DeviceStatus
            {
                Mills = MillsAgo(5),
                OpenAps = new OpenApsStatus { Enacted = Enacted(5) },
            }
        );

        var result = await Intent("LastLoop");

        Assert.Equal("The last successful loop was 5 minutes ago", Speech(result));
    }

    [Fact]
    public async Task LastLoop_IgnoresPumpOnlyAndStaleStatuses()
    {
        DeviceStatuses(
            new DeviceStatus { Mills = MillsAgo(1), Pump = new PumpStatus() },
            new DeviceStatus
            {
                Mills = MillsAgo(7 * 60),
                Loop = new LoopStatus { Timestamp = Iso(7 * 60) },
            }
        );

        var result = await Intent("LastLoop");

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task LastLoop_WithoutDevicesRead_SpeaksUnknownValue()
    {
        Grant(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.TherapyRead);
        DeviceStatuses(
            new DeviceStatus { Mills = MillsAgo(1), Loop = new LoopStatus { Timestamp = Iso(1) } }
        );

        var result = await Intent("LastLoop");

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task MetricNow_PumpBattery_WithoutDevicesRead_SpeaksUnknownValue()
    {
        Grant(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.TherapyRead);
        DeviceStatuses(
            new DeviceStatus
            {
                Mills = MillsAgo(3),
                Pump = new PumpStatus { Battery = new PumpBattery { Percent = 80 } },
            }
        );

        var result = await MetricNow(ResolvedMetric("pump battery", "pump battery"));

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task MetricNow_Basal_WithoutTherapyRead_SpeaksUnknownValue()
    {
        Grant(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead);
        Properties(("basal", Basal(0.9)));

        var result = await MetricNow(ResolvedMetric("basal", "basal"));

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task NSStatus_WithoutTherapyRead_LeavesOutBasal()
    {
        Grant(Scope.GlucoseRead, Scope.TreatmentsRead);
        LatestEntry(120, "Flat", minutesAgo: 5);
        Properties(("basal", Basal(0.9)), ("iob", Reading("iob", 1.25)));

        var result = await Intent("NSStatus");

        Assert.Equal(
            "120 and holding as of 5 minutes ago. and you have 1.25 units of insulin on board.",
            Speech(result)
        );
    }

    [Fact]
    public async Task MetricNow_Iob_WhenThePropertyIsMissing_SpeaksUnknownValueNotZero()
    {
        Properties();

        var result = await MetricNow(ResolvedMetric("iob", "iob"));

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task NSStatus_WhenIobIsMissing_LeavesItOut()
    {
        LatestEntry(120, "Flat", minutesAgo: 5);
        Properties(("basal", Basal(0.9)));

        var result = await Intent("NSStatus");

        Assert.Equal(
            "120 and holding as of 5 minutes ago. Your current basal is 0.9 units per hour",
            Speech(result)
        );
    }

    [Fact]
    public async Task MetricNow_PumpReservoir_PastTheRecencyWindow_SpeaksUnknownValue()
    {
        DeviceStatuses(
            new DeviceStatus { Mills = MillsAgo(121), Pump = new PumpStatus { Reservoir = 150.5 } }
        );

        var result = await MetricNow(ResolvedMetric("pump reservoir", "pump reservoir"));

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task MetricNow_PumpReservoir_InsideTheRecencyWindow_IsSpoken()
    {
        DeviceStatuses(
            new DeviceStatus { Mills = MillsAgo(119), Pump = new PumpStatus { Reservoir = 150.5 } }
        );

        var result = await MetricNow(ResolvedMetric("pump reservoir", "pump reservoir"));

        Assert.Equal("You have 150.5 units remaining", Speech(result));
    }

    [Fact]
    public async Task MetricNow_PumpBattery_PastTheRecencyWindow_SpeaksUnknownValue()
    {
        DeviceStatuses(
            new DeviceStatus
            {
                Mills = MillsAgo(121),
                Pump = new PumpStatus { Battery = new PumpBattery { Percent = 80 } },
            }
        );

        var result = await MetricNow(ResolvedMetric("pump battery", "pump battery"));

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task MetricNow_Bg_ForAMmolTenant_SpeaksMmol()
    {
        Units("mmol");
        LatestEntry(140, "Flat", minutesAgo: 5);

        var result = await MetricNow(ResolvedMetric("bg", "bg"));

        Assert.Equal("7.8 and holding as of 5 minutes ago.", Speech(result));
    }

    [Fact]
    public async Task MetricNow_Bg_ForAMmolTenant_DropsAWholeNumbersDecimal()
    {
        Units("mmol");
        LatestEntry(126, "Flat", minutesAgo: 5);

        var result = await MetricNow(ResolvedMetric("bg", "bg"));

        Assert.Equal("7 and holding as of 5 minutes ago.", Speech(result));
    }

    [Fact]
    public async Task NSStatus_ForAMmolTenant_SpeaksGlucoseInMmol()
    {
        Units("mmol");
        Grant(Scope.GlucoseRead);
        LatestEntry(140, "Flat", minutesAgo: 5);

        var result = await Intent("NSStatus");

        Assert.Equal("7.8 and holding as of 5 minutes ago.", Speech(result));
    }

    private static Dictionary<string, object> Delta(
        string display,
        double mgdl,
        bool interpolated = false
    ) =>
        new()
        {
            ["display"] = display,
            ["mgdl"] = mgdl,
            ["interpolated"] = interpolated,
            ["times"] = new Dictionary<string, long>
            {
                ["recent"] = MillsAgo(5),
                ["previous"] = MillsAgo(10),
            },
        };

    [Fact]
    public async Task MetricNow_Delta_SpeaksChangeBetweenReadings()
    {
        Properties(("delta", Delta("-4", -4)));

        var result = await MetricNow(ResolvedMetric("blood sugar delta", "delta"));

        Assert.Equal(
            "Your delta was -4 between 5 minutes ago and 10 minutes ago.",
            Speech(result)
        );
        Assert.Equal("Blood Glucose Delta", result.Response.Card?.Title);
    }

    [Fact]
    public async Task MetricNow_Delta_ThatIsInterpolated_SaysEstimated()
    {
        Properties(("delta", Delta("+3", 3, interpolated: true)));

        var result = await MetricNow(ResolvedMetric("delta", "delta"));

        Assert.Equal(
            "Your estimated delta was +3 between 5 minutes ago and 10 minutes ago.",
            Speech(result)
        );
    }

    [Fact]
    public async Task MetricNow_Delta_OfZero_SpeaksZero()
    {
        Properties(("delta", Delta("+0", 0)));

        var result = await MetricNow(ResolvedMetric("delta", "delta"));

        Assert.Equal(
            "Your delta was 0 between 5 minutes ago and 10 minutes ago.",
            Speech(result)
        );
    }

    [Fact]
    public async Task MetricNow_Delta_ForAMmolTenant_SpeaksMmol()
    {
        Units("mmol");
        Properties(("delta", Delta("+9", 9)));

        var result = await MetricNow(ResolvedMetric("delta", "delta"));

        Assert.Equal(
            "Your delta was +0.5 between 5 minutes ago and 10 minutes ago.",
            Speech(result)
        );
    }

    [Fact]
    public async Task MetricNow_Delta_WithoutTwoReadings_SpeaksUnknownValue()
    {
        Properties();

        var result = await MetricNow(ResolvedMetric("delta", "delta"));

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Theory]
    [InlineData(44, "a few seconds ago")]
    [InlineData(45, "a minute ago")]
    [InlineData(89.6, "a minute ago")]
    [InlineData(150, "3 minutes ago")]
    public async Task RelativeTime_RoundsEachUnitFromTheDurationLikeMoment(
        double secondsAgo,
        string expected
    )
    {
        LatestEntry(120, "Flat", TimeSpan.FromSeconds(secondsAgo));

        var result = await MetricNow(ResolvedMetric("bg", "bg"));

        Assert.Equal($"120 and holding as of {expected}.", Speech(result));
    }

    [Fact]
    public async Task RelativeTime_EightyNineMinutesFortySeconds_IsAnHourNotTwo()
    {
        LatestEntry(120, "Flat", TimeSpan.FromSeconds(89 * 60 + 40));

        var result = await MetricNow(ResolvedMetric("bg", "bg"));

        Assert.Equal("120 and holding as of an hour ago.", Speech(result));
    }

    [Fact]
    public async Task MetricNow_TheIssuesJsonRepro_SpeaksTheBgSentence()
    {
        const string body =
            """
            {"request":{"type":"IntentRequest","locale":"en-US","intent":{"name":"MetricNow","slots":{"metric":{"value":"blood sugar","resolutions":{"resolutionsPerAuthority":[{"status":{"code":"ER_SUCCESS_MATCH"},"values":[{"value":{"name":"bg"}}]}]}}}}}}
            """;
        var request = JsonSerializer.Deserialize<AlexaRequest>(
            body,
            NightscoutJsonOptions.Create()
        )!;
        LatestEntry(120, "Flat", minutesAgo: 5);

        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        Assert.Equal("120 and holding as of 5 minutes ago.", Speech(result));
    }

    [Fact]
    public async Task NSStatus_SpeaksGlucoseThenBasalThenIob()
    {
        LatestEntry(120, "Flat", minutesAgo: 5);
        Properties(("basal", Basal(0.9)), ("iob", Reading("iob", 1.25)));

        var result = await Intent("NSStatus");

        Assert.Equal(
            "120 and holding as of 5 minutes ago. Your current basal is 0.9 units per hour and you have 1.25 units of insulin on board.",
            Speech(result)
        );
        Assert.Equal("Full Status", result.Response.Card?.Title);
        Assert.True(result.Response.ShouldEndSession);
    }

    [Fact]
    public async Task NSStatus_WithNoInsulinOnBoard_SpeaksNo()
    {
        LatestEntry(120, "Flat", minutesAgo: 5);
        Properties(("basal", Basal(0.9)), ("iob", Reading("iob", 0)));

        var result = await Intent("NSStatus");

        Assert.Equal(
            "120 and holding as of 5 minutes ago. Your current basal is 0.9 units per hour and you have no insulin on board.",
            Speech(result)
        );
    }

    [Fact]
    public async Task NSStatus_ForAGlucoseOnlyCredential_SpeaksOnlyGlucose()
    {
        Grant(Scope.GlucoseRead);
        LatestEntry(120, "Flat", minutesAgo: 5);

        var result = await Intent("NSStatus");

        Assert.Equal("120 and holding as of 5 minutes ago.", Speech(result));
        _mockProperties.Verify(
            x =>
                x.GetPropertiesAsync(
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Theory]
    [InlineData("iob")]
    [InlineData("cob")]
    [InlineData("basal")]
    [InlineData("uploader battery")]
    [InlineData("pump reservoir")]
    public async Task MetricNow_ForAGlucoseOnlyCredential_DoesNotReadOtherCategories(string metric)
    {
        Grant(Scope.GlucoseRead);
        Properties(
            ("iob", Reading("iob", 1.25)),
            ("cob", Reading("cob", 24)),
            ("basal", Basal(0.9)),
            ("upbat", new Dictionary<string, object> { ["display"] = "85%" })
        );
        DeviceStatuses(
            new DeviceStatus { Mills = MillsAgo(0), Pump = new PumpStatus { Reservoir = 80 } }
        );

        var result = await MetricNow(ResolvedMetric(metric, metric));

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Fact]
    public async Task MetricNow_WithoutAnHttpContext_ReadsNothing()
    {
        _httpContextAccessor.HttpContext = null;
        LatestEntry(120, "Flat", minutesAgo: 5);

        var result = await MetricNow(ResolvedMetric("bg", "bg"));

        Assert.Equal(UnknownValueText, Speech(result));
    }

    [Theory]
    [InlineData("ER_NO_MATCH")]
    [InlineData("ER_SUCCESS_NO_MATCH")]
    public async Task MetricNow_UnmatchedMetric_SpeaksUnknownIntent(string status)
    {
        LatestEntry(120, "Flat", minutesAgo: 5);

        var result = await MetricNow(ResolvedMetric("the weather", "bg", status));

        Assert.Equal(UnknownIntentText, Speech(result));
    }

    [Fact]
    public async Task MetricNow_MetricWithNoHandler_SpeaksUnknownIntent()
    {
        var result = await MetricNow(ResolvedMetric("the weather", "weather"));

        Assert.Equal(UnknownIntentText, Speech(result));
    }

    [Fact]
    public async Task MetricNow_EmptySlot_SpeaksUnknownIntent()
    {
        var result = await MetricNow(new AlexaSlot { Name = "metric" });

        Assert.Equal(UnknownIntentText, Speech(result));
    }

    [Fact]
    public async Task NSBg_IsNotAnIntentOfTheInteractionModel()
    {
        LatestEntry(120, "Flat", minutesAgo: 5);

        var result = await Intent("NSBg");

        Assert.Equal(UnknownIntentText, Speech(result));
    }

    [Fact]
    public async Task ProcessRequestAsync_IntentRequest_HelpIntent_ReturnsHelpMessage()
    {
        var result = await Intent("AMAZON.HelpIntent");

        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.False(result.Response.ShouldEndSession);
        Assert.Contains("I can help you check", result.Response.OutputSpeech?.Text ?? "");
        Assert.NotNull(result.Response.Reprompt);
    }

    [Fact]
    public async Task ProcessRequestAsync_IntentRequest_StopIntent_ReturnsGoodbyeMessage()
    {
        var result = await Intent("AMAZON.StopIntent");

        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.True(result.Response.ShouldEndSession);
        Assert.Contains("Goodbye", result.Response.OutputSpeech?.Text ?? "");
    }

    [Fact]
    public async Task ProcessRequestAsync_IntentRequest_UnknownIntent_ReturnsUnknownMessage()
    {
        var result = await Intent("UnknownIntent");

        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.True(result.Response.ShouldEndSession);
        Assert.Contains("I didn't understand", result.Response.OutputSpeech?.Text ?? "");
    }

    [Fact]
    public async Task ProcessRequestAsync_LocaleProcessing_ExtractsCorrectLocale()
    {
        var request = new AlexaRequest
        {
            Request = new AlexaRequestDetails
            {
                Type = "LaunchRequest",
                Locale = "es-ES", // Should be reduced to "es"
            },
        };

        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.Response.OutputSpeech?.Text);
    }

    [Fact]
    public void BuildSpeechletResponse_WithAllParameters_CreatesValidResponse()
    {
        var title = "Test Title";
        var output = "Test output speech";
        var reprompt = "Test reprompt";
        var shouldEndSession = false;

        var result = _alexaService.BuildSpeechletResponse(
            title,
            output,
            reprompt,
            shouldEndSession
        );

        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.Equal(shouldEndSession, result.Response.ShouldEndSession);
        Assert.Equal(output, result.Response.OutputSpeech?.Text);
        Assert.Equal(title, result.Response.Card?.Title);
        Assert.Equal(output, result.Response.Card?.Content);
        Assert.NotNull(result.Response.Reprompt);
        Assert.Equal(reprompt, result.Response.Reprompt.OutputSpeech?.Text);
    }

    [Fact]
    public void BuildSpeechletResponse_WithEndSession_NoReprompt()
    {
        var result = _alexaService.BuildSpeechletResponse(
            "Test Title",
            "Test output speech",
            "Test reprompt",
            true
        );

        Assert.NotNull(result);
        Assert.True(result.Response.ShouldEndSession);
        Assert.Null(result.Response.Reprompt);
    }
}
