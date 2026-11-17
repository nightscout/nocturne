using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V2;

/// <summary>
/// Parity tests for /api/v2/properties endpoints.
/// V2 Properties API provides client properties and settings:
/// - GET /api/v2/properties - Get all properties
/// - GET /api/v2/properties/{path} - Get specific properties
/// </summary>
[Collection(ParityShards.One)]
public class PropertiesParityTests : ParityTestBase
{
    public PropertiesParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    protected override ComparisonOptions GetComparisonOptions() => Options;

    /// <summary>
    /// Nocturne computes iob activity from the resolved sensitivity, a default one with no profile,
    /// where Nightscout reports 0, and no client shows it. The three-decimal iob moves between the
    /// two requests, so only its display is compared. The unrounded cob and its display are compared
    /// to within <see cref="ComparisonOptions.ReloadLagToleranceTable"/>; the rest of the cob
    /// property (decayedBy, isDecaying, carbs_hr, rawCarbImpact, source, lastCarbs) does not move
    /// with time while the carbs decay, so it is compared exactly.
    /// </summary>
    internal static readonly ComparisonOptions Options = ComparisonOptions.Default.WithIgnoredFields(
        "serverTime",
        "serverTimeEpoch",
        "uptime",
        "lastUpdated"
    ).WithPresenceOnlyFields("activity", "iob").WithReloadLagTolerance();

    #region Helper Methods

    private async Task SeedProfileForTestAsync()
    {
        var profile = TestDataFactory.CreateProfile();
        var nsProfile = new
        {
            defaultProfile = profile.DefaultProfile,
            startDate = profile.StartDate,
            mills = profile.Mills,
            units = profile.Units,
            store = profile.Store
        };

        await NightscoutClient.PostAsJsonAsync("/api/v1/profile", nsProfile);
        await NocturneClient.PostAsJsonAsync("/api/v1/profile", profile);
    }

    /// <summary>
    /// The pump property returns a whole device status, and Nocturne stores a status without
    /// these fields, so <c>/api/v1/devicestatus</c> serves it without them too: an OpenAPS
    /// command's <c>temp</c>, an OpenAPS IOB's <c>activity</c> and <c>time</c>, and Loop's
    /// <c>pumpID</c>, <c>suspended</c> and <c>bolusing</c>. No status, level or display reads them.
    /// </summary>
    private ComparisonOptions DeviceStatusPluginOptions() =>
        GetComparisonOptions().WithIgnoredFields("temp", "activity", "time", "pumpID", "suspended", "bolusing");

    private async Task SeedRawDeviceStatusAsync(params JsonObject[] statuses)
    {
        foreach (var status in statuses)
        {
            (await NightscoutClient.PostAsJsonAsync("/api/v1/devicestatus", status)).EnsureSuccessStatusCode();
            (await NocturneClient.PostAsJsonAsync("/api/v1/devicestatus", status)).EnsureSuccessStatusCode();
        }
    }

    private static string Iso(DateTimeOffset time) => time.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static JsonObject LoopStatus(
        DateTimeOffset at, double? reservoir, int pumpBattery, bool failed,
        string manufacturer = "Medtronic", string model = "723", JsonObject? loopOverride = null)
    {
        var loop = new JsonObject
        {
            ["name"] = "Loop",
            ["version"] = "3.4.1",
            ["timestamp"] = Iso(at),
            ["iob"] = new JsonObject { ["iob"] = 1.35, ["timestamp"] = Iso(at) },
            ["cob"] = new JsonObject { ["cob"] = 12.4, ["timestamp"] = Iso(at) },
            ["predicted"] = new JsonObject
            {
                ["startDate"] = Iso(at),
                ["values"] = new JsonArray(142, 145, 149, 152, 154, 155),
            },
            ["recommendedBolus"] = 0.0,
        };
        if (failed)
            loop["failureReason"] = "pumpDataTooOld";
        else
            loop["enacted"] = new JsonObject
            {
                ["rate"] = 0.85,
                ["duration"] = 30.0,
                ["timestamp"] = Iso(at),
                ["received"] = true,
                ["bolusVolume"] = 0.0,
            };

        var pump = new JsonObject
        {
            ["clock"] = Iso(at),
            ["pumpID"] = "123456",
            ["manufacturer"] = manufacturer,
            ["model"] = model,
            ["battery"] = new JsonObject { ["percent"] = pumpBattery },
            ["suspended"] = false,
            ["bolusing"] = false,
        };
        if (reservoir is { } units)
            pump["reservoir"] = units;

        var status = new JsonObject
        {
            ["device"] = "loop://iPhone",
            ["created_at"] = Iso(at),
            ["uploader"] = new JsonObject { ["name"] = "iPhone", ["battery"] = 64 },
            ["pump"] = pump,
            ["loop"] = loop,
        };
        if (loopOverride is not null)
            status["override"] = loopOverride;
        return status;
    }

    private static JsonObject OpenApsStatus(
        DateTimeOffset at, double reservoir, bool? received, string device = "openaps://samsung SM-G973F")
    {
        JsonObject Command(bool enacted)
        {
            var command = new JsonObject
            {
                ["temp"] = "absolute",
                ["bg"] = 156,
                ["tick"] = "+4",
                ["eventualBG"] = 171,
                ["targetBG"] = 100,
                ["insulinReq"] = 0.42,
                ["sensitivityRatio"] = 1.0,
                ["reason"] = "COB: 18, Dev: 21, BGI: -2.1, ISF: 50, CR: 10, Target: 100",
                ["COB"] = 18,
                ["IOB"] = 1.21,
                ["rate"] = 1.6,
                ["duration"] = 30,
                ["deliverAt"] = Iso(at),
                ["timestamp"] = Iso(at),
                ["predBGs"] = new JsonObject
                {
                    ["IOB"] = new JsonArray(156, 159, 161, 162),
                    ["ZT"] = new JsonArray(156, 154, 151, 149),
                    ["COB"] = new JsonArray(156, 162, 168, 171),
                },
            };
            if (enacted)
                command["received"] = received;
            return command;
        }

        var openAps = new JsonObject
        {
            ["suggested"] = Command(enacted: false),
            ["iob"] = new JsonObject
            {
                ["iob"] = 1.21,
                ["basaliob"] = 0.31,
                ["bolusiob"] = 0.9,
                ["activity"] = 0.0123,
                ["time"] = Iso(at),
            },
        };
        if (received is not null)
            openAps["enacted"] = Command(enacted: true);

        return new JsonObject
        {
            ["device"] = device,
            ["created_at"] = Iso(at),
            ["uploaderBattery"] = 71,
            ["pump"] = new JsonObject
            {
                ["clock"] = Iso(at),
                ["reservoir"] = reservoir,
                ["battery"] = new JsonObject { ["percent"] = 18 },
                ["status"] = new JsonObject { ["status"] = "normal" },
            },
            ["openaps"] = openAps,
        };
    }

    #endregion

    #region GET /api/v2/properties

    [Fact]
    public async Task GetAllProperties_Empty_ReturnsSameShape()
    {
        await WaitForNightscoutEmptyAsync();
        await AssertGetParityAsync("/api/v2/properties");
    }

    [Fact]
    public async Task GetAllProperties_WithData_ReturnsSameShape()
    {
        // Seed some data so properties have values to compute
        await SeedEntrySequenceAsync(count: 5);
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Meal Bolus",
                insulin: 5.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1))
        };
        await SeedTreatmentsAsync(treatments);

        await WaitForNightscoutReloadAsync("/api/v2/summary", sgvs: 5, treatments: 1);
        await AssertGetParityAsync("/api/v2/properties");
    }

    [Fact]
    public async Task GetAllProperties_Pretty_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 3);

        await WaitForNightscoutReloadAsync("/api/v2/summary", sgvs: 3);
        await AssertGetParityAsync("/api/v2/properties?pretty=true");
    }

    #endregion

    #region GET /api/v2/properties/{path}

    [Fact]
    public async Task GetSpecificProperty_Iob_ReturnsSameShape()
    {
        // Seed insulin treatments for IOB calculation
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Correction Bolus",
                insulin: 2.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1)),
            TestDataFactory.CreateTreatment(
                eventType: "Meal Bolus",
                insulin: 5.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-2))
        };
        await SeedTreatmentsAsync(treatments);

        await WaitForNightscoutReloadAsync("/api/v2/summary", sgvs: 0, treatments: 2);
        await AssertGetParityAsync("/api/v2/properties/iob");
    }

    [Fact]
    public async Task GetSpecificProperty_Cob_ReturnsSameShape()
    {
        // Seed treatments for COB calculation (Meal Bolus without carbs will have 0 COB)
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Meal Bolus",
                insulin: 5.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1)),
            TestDataFactory.CreateTreatment(
                eventType: "Carb Correction",
                insulin: 1.0,
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-30))
        };
        await SeedTreatmentsAsync(treatments);

        await WaitForNightscoutReloadAsync("/api/v2/summary", sgvs: 0, treatments: 2);
        await AssertGetParityAsync("/api/v2/properties/cob");
    }

    [Fact]
    public async Task GetSpecificProperty_Cob_WithCarbsOnBoard_ReturnsSameShape()
    {
        await SeedProfileForTestAsync();
        var carbs = TestDataFactory.CreateTreatment(
            eventType: "Carb Correction",
            timestamp: TestTimeProvider.GetTestTime().AddMinutes(-30));
        carbs.Insulin = null;
        carbs.Carbs = 30;
        await SeedTreatmentsAsync(carbs);

        await WaitForNightscoutBodyAsync(
            "/api/v2/properties/cob",
            body => body?["cob"]?["isDecaying"]?.GetValue<double>() == 1);
        await AssertGetParityAsync("/api/v2/properties/cob");
    }

    [Fact]
    public async Task GetSpecificProperty_Basal_ReturnsSameShape()
    {
        await SeedProfileForTestAsync();

        await WaitForNightscoutBodyAsync("/api/v2/summary", body => body?["profile"] is JsonObject { Count: > 0 });
        await AssertGetParityAsync("/api/v2/properties/basal");
    }

    [Fact]
    public async Task GetSpecificProperty_Bwp_ReturnsSameShape()
    {
        // BWP (Bolus Wizard Preview) requires entries and profile
        await SeedEntrySequenceAsync(count: 5);
        await SeedProfileForTestAsync();

        await WaitForNightscoutReloadAsync("/api/v2/summary", sgvs: 5);
        await AssertGetParityAsync("/api/v2/properties/bwp");
    }

    [Fact]
    public async Task GetSpecificProperty_Cage_ReturnsSameShape()
    {
        // CAGE (Cannula Age) requires site change treatment
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Site Change",
                timestamp: TestTimeProvider.GetTestTime().AddDays(-2))
        };
        await SeedTreatmentsAsync(treatments);
        await WaitForNightscoutBodyAsync(
            "/api/v2/properties/cage",
            body => body?["cage"]?["found"]?.GetValue<bool>() == true);

        await AssertGetParityAsync("/api/v2/properties/cage");
    }

    [Fact]
    public async Task GetSpecificProperty_PumpLoop_ForLoop_ReturnsSameShape()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedRawDeviceStatusAsync(
            LoopStatus(now.AddMinutes(-7), reservoir: 9.5, pumpBattery: 75, failed: false),
            LoopStatus(now.AddMinutes(-2), reservoir: 86.25, pumpBattery: 75, failed: true));
        await WaitForNightscoutBodyAsync(
            "/api/v2/ddata/at",
            body => body?["devicestatus"]?.AsArray().Count == 2);

        await AssertGetParityAsync("/api/v2/properties/pump,loop,openaps", DeviceStatusPluginOptions());
    }

    [Fact]
    public async Task GetSpecificProperty_PumpOpenaps_ForOpenAps_ReturnsSameShape()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedRawDeviceStatusAsync(
            OpenApsStatus(now.AddMinutes(-8), reservoir: 4.5, received: true),
            OpenApsStatus(now.AddMinutes(-3), reservoir: 120, received: false));
        await WaitForNightscoutBodyAsync(
            "/api/v2/ddata/at",
            body => body?["devicestatus"]?.AsArray().Count == 2);

        await AssertGetParityAsync("/api/v2/properties/pump,loop,openaps", DeviceStatusPluginOptions());
    }

    [Fact]
    public async Task GetSpecificProperty_PumpLoop_ForAnEnactingLoopOnOmnipodDash_ReturnsSameShape()
    {
        var now = DateTimeOffset.UtcNow;
        var loopOverride = new JsonObject
        {
            ["name"] = "Exercise",
            ["timestamp"] = Iso(now.AddMinutes(-2)),
            ["active"] = true,
            ["multiplier"] = 0.5,
            ["currentCorrectionRange"] = new JsonObject { ["minValue"] = 140.0, ["maxValue"] = 160.0 },
        };
        await SeedRawDeviceStatusAsync(
            LoopStatus(now.AddMinutes(-7), reservoir: null, pumpBattery: 25, failed: true, "Insulet", "Dash"),
            LoopStatus(now.AddMinutes(-2), reservoir: null, pumpBattery: 25, failed: false, "Insulet", "Dash", loopOverride));
        await WaitForNightscoutBodyAsync(
            "/api/v2/ddata/at",
            body => body?["devicestatus"]?.AsArray().Count == 2);

        await AssertGetParityAsync("/api/v2/properties/pump,loop,openaps", DeviceStatusPluginOptions());
    }

    [Fact]
    public async Task GetSpecificProperty_PumpOpenaps_ForAnEnactingOpenAps_ReturnsSameShape()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedRawDeviceStatusAsync(
            OpenApsStatus(now.AddMinutes(-8), reservoir: 120, received: false),
            OpenApsStatus(now.AddMinutes(-3), reservoir: 120, received: true));
        await WaitForNightscoutBodyAsync(
            "/api/v2/ddata/at",
            body => body?["devicestatus"]?.AsArray().Count == 2);

        await AssertGetParityAsync("/api/v2/properties/pump,loop,openaps", DeviceStatusPluginOptions());
    }

    [Fact]
    public async Task GetSpecificProperty_PumpOpenaps_ForASuggestOnlyAaps_ReturnsSameShape()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedRawDeviceStatusAsync(
            OpenApsStatus(now.AddMinutes(-25), reservoir: 120, received: true, device: "openaps://old-rig"),
            OpenApsStatus(now.AddMinutes(-20), reservoir: 120, received: true),
            OpenApsStatus(now.AddMinutes(-3), reservoir: 120, received: null));
        await WaitForNightscoutBodyAsync(
            "/api/v2/ddata/at",
            body => body?["devicestatus"]?.AsArray().Count == 3);

        await AssertGetParityAsync("/api/v2/properties/pump,loop,openaps", DeviceStatusPluginOptions());
    }

    [Fact]
    public async Task GetSpecificProperty_Sage_ReturnsSameShape()
    {
        // SAGE (Sensor Age) requires sensor start treatment
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Sensor Start",
                timestamp: TestTimeProvider.GetTestTime().AddDays(-5))
        };
        await SeedTreatmentsAsync(treatments);
        await WaitForNightscoutBodyAsync(
            "/api/v2/properties/sage",
            body => body?["sage"]?["Sensor Start"]?["found"]?.GetValue<bool>() == true);

        await AssertGetParityAsync("/api/v2/properties/sage");
    }

    [Fact]
    public async Task GetMultipleProperties_CommaSeparated_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 5);
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(
                eventType: "Correction Bolus",
                insulin: 2.0,
                timestamp: TestTimeProvider.GetTestTime().AddHours(-1))
        };
        await SeedTreatmentsAsync(treatments);
        await SeedProfileForTestAsync();

        await WaitForNightscoutReloadAsync("/api/v2/summary", sgvs: 5, treatments: 1);
        await AssertGetParityAsync("/api/v2/properties/iob,cob,basal");
    }

    [Fact]
    public async Task GetSpecificProperty_Pretty_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 3);

        await WaitForNightscoutReloadAsync("/api/v2/summary", sgvs: 3);
        await AssertGetParityAsync("/api/v2/properties/iob?pretty=true");
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task GetSpecificProperty_NonExistent_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v2/properties/nonexistentproperty");
    }

    [Fact]
    public async Task GetSpecificProperty_EmptyPath_ReturnsSameShape()
    {
        await WaitForNightscoutEmptyAsync();
        await AssertGetParityAsync("/api/v2/properties/");
    }

    [Fact]
    public async Task GetMultipleProperties_SomeInvalid_ReturnsSameShape()
    {
        await SeedEntrySequenceAsync(count: 3);

        await WaitForNightscoutReloadAsync("/api/v2/summary", sgvs: 3);
        await AssertGetParityAsync("/api/v2/properties/iob,invalidprop,cob");
    }

    #endregion
}
