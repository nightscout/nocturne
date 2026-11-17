using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Tests.Services.Glucose;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

[Parity("pump.test.js")]
public class DeviceStatusPropertiesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static string Iso(DateTimeOffset time) => time.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static DeviceStatus Status(DateTimeOffset at, string device = "openaps://rig") =>
        new() { Id = Guid.NewGuid().ToString(), Device = device, Mills = at.ToUnixTimeMilliseconds(), CreatedAt = Iso(at) };

    private static DeviceStatusProperties.Result Build(bool offline = false, params DeviceStatus[] statuses) =>
        DeviceStatusProperties.Build(statuses, Now.ToUnixTimeMilliseconds(), new PumpPreferences(), offline);

    private static DeviceStatus PumpStatus(
        DateTimeOffset at, double? reservoir = null, PumpBattery? battery = null, string? clock = null)
    {
        var status = Status(at);
        status.Pump = new PumpStatus { Clock = clock ?? Iso(at), Reservoir = reservoir, Battery = battery };
        return status;
    }

    [Theory]
    [InlineData(86.25, "86.3")]
    [InlineData(120, "120")]
    [InlineData(4.5, "4.50")]
    [InlineData(0, "0.00")]
    [InlineData(0.125, "0.125")]
    [InlineData(2.675, "2.67")]
    [InlineData(999.6, "1.00e+3")]
    public void ToPrecision3_FormatsAsJavaScriptDoes(double value, string expected) =>
        DeviceStatusProperties.ToPrecision3(value).Should().Be(expected);

    [Fact]
    public void Pump_TakesTheStatusWithTheNewestClock()
    {
        var newerClock = PumpStatus(Now.AddMinutes(-10), reservoir: 50, clock: Iso(Now.AddMinutes(-1)));
        var newerStatus = PumpStatus(Now.AddMinutes(-5), reservoir: 40, clock: Iso(Now.AddMinutes(-6)));

        var pump = Build(false, newerClock, newerStatus).Pump;

        pump["pump"]!["reservoir"]!.GetValue<double>().Should().Be(50);
        pump["clockMills"]!.GetValue<long>().Should().Be(Now.AddMinutes(-1).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Pump_IgnoresAStatusOlderThanTwiceTheUrgentClock()
    {
        var pump = Build(false, PumpStatus(Now.AddMinutes(-121), reservoir: 50)).Pump;

        pump.ContainsKey("pump").Should().BeFalse();
        pump["data"]!["reservoir"].Should().BeNull();
    }

    [Fact]
    public void Pump_WithALowReservoir_IsUrgentAndSaysWhy()
    {
        var data = Build(false, PumpStatus(Now.AddMinutes(-2), reservoir: 4.5, battery: new PumpBattery { Percent = 80 })).Pump["data"]!;

        data["reservoir"]!["display"]!.GetValue<string>().Should().Be("4.50U");
        data["reservoir"]!["level"]!.GetValue<int>().Should().Be(DeviceAgeLevel.Urgent);
        data["level"]!.GetValue<int>().Should().Be(DeviceAgeLevel.Urgent);
        data["title"]!.GetValue<string>().Should().Be("URGENT: Pump Reservoir Low");
        data["message"]!.GetValue<string>().Should().Be("Pump Battery: 80%\nPump Reservoir: 4.50U");
    }

    [Fact]
    public void Pump_WithAVoltageBetweenTheThresholds_Warns()
    {
        var data = Build(false, PumpStatus(Now.AddMinutes(-2), reservoir: 100, battery: new PumpBattery { Voltage = 1.32 })).Pump["data"]!;

        data["battery"]!["display"]!.GetValue<string>().Should().Be("1.32v");
        data["battery"]!["unit"]!.GetValue<string>().Should().Be("volts");
        data["level"]!.GetValue<int>().Should().Be(DeviceAgeLevel.Warn);
        data["title"]!.GetValue<string>().Should().Be("Warning, Pump Battery Low");
    }

    [Fact]
    public void Pump_WithAStaleClock_WarnsAndShowsItsAge()
    {
        var status = PumpStatus(Now.AddMinutes(-5), reservoir: 100, clock: Iso(Now.AddMinutes(-45)));

        var data = Build(false, status).Pump["data"]!;

        data["clock"]!["display"]!.GetValue<string>().Should().Be("45m ago");
        data["clock"]!["level"]!.GetValue<int>().Should().Be(DeviceAgeLevel.Warn);
        data["title"]!.GetValue<string>().Should().Be("Warning, Pump data stale");
    }

    [Fact]
    public void Pump_WhenOpenApsIsKnownOffline_RaisesNothing()
    {
        var data = Build(true, PumpStatus(Now.AddMinutes(-2), reservoir: 1)).Pump["data"]!;

        data["reservoir"]!["level"]!.GetValue<int>().Should().Be(DeviceAgeLevel.Urgent);
        data["level"]!.GetValue<int>().Should().Be(DeviceAgeLevel.None);
        data["title"]!.GetValue<string>().Should().Be("Pump Status");
        data.AsObject().ContainsKey("message").Should().BeFalse();
    }

    [Fact]
    public void Pump_WithNoStatus_SendsAnEmptyStatus()
    {
        var pump = Build().Pump;

        pump["data"]!.ToJsonString().Should().Be(
            """{"level":-3,"clock":null,"reservoir":null,"reservoir_display_override":null,"reservoir_level_override":null,"extended":null,"device":{"label":"Device"},"title":"Pump Status"}""");
    }

    [Theory]
    [InlineData("Eros")]
    [InlineData("Dash")]
    [InlineData(null)]
    public void Pump_AnInsuletPodWithNoReservoir_ShowsFiftyPlus(string? model)
    {
        var status = PumpStatus(Now.AddMinutes(-2), battery: new PumpBattery { Percent = 10 });
        status.Pump!.Manufacturer = "Insulet";
        status.Pump.Model = model;

        var data = Build(false, status).Pump["data"]!;

        var reservoir = data["reservoir"]!.AsObject();
        reservoir.Select(p => p.Key).Should().Equal("label", "display");
        reservoir["label"]!.GetValue<string>().Should().Be("Reservoir");
        reservoir["display"]!.GetValue<string>().Should().Be("50+ U");
        data["message"]!.GetValue<string>().Should().Be("Pump Battery: 10%\nPump Reservoir: 50+ U");
    }

    [Fact]
    public void Pump_TheDisplayOverride_ReplacesAnInsuletPodsReservoir()
    {
        var status = PumpStatus(Now.AddMinutes(-2));
        status.Pump!.Manufacturer = "Insulet";
        status.Pump.ReservoirDisplayOverride = "40U";

        var data = Build(false, status).Pump["data"]!;

        data["reservoir"]!["display"]!.GetValue<string>().Should().Be("40U");
    }

    [Fact]
    public void Pump_AnotherPumpWithNoReservoir_ShowsNone()
    {
        var status = PumpStatus(Now.AddMinutes(-2));
        status.Pump!.Manufacturer = "Medtronic";

        Build(false, status).Pump["data"]!["reservoir"].Should().BeNull();
    }

    [Fact]
    public void Pump_StampsTheUploaderBatteryAsUpbatDoes()
    {
        var status = PumpStatus(Now.AddMinutes(-2), reservoir: 100);
        status.Uploader = new UploaderStatus { Battery = 64 };

        var uploader = Build(false, status).Pump["uploader"]!;

        uploader["value"]!.GetValue<int>().Should().Be(64);
        uploader["display"]!.GetValue<string>().Should().Be("64%");
        uploader["level"]!.GetValue<int>().Should().Be(75);
    }

    private static DeviceStatus LoopStatus(DateTimeOffset at, Action<LoopStatus>? configure = null)
    {
        var status = Status(at, "loop://iPhone");
        status.Loop = new LoopStatus { Timestamp = Iso(at) };
        configure?.Invoke(status.Loop);
        return status;
    }

    [Theory]
    [InlineData("enacted", "⌁", "Enacted")]
    [InlineData("failure", "x", "Error")]
    [InlineData("unreceived", "x", "Error")]
    [InlineData("recommended", "⏀", "Recomendation")]
    [InlineData("looping", "↻", "Looping")]
    public void Loop_ShowsTheNewestLoopsState(string state, string symbol, string label)
    {
        var at = Now.AddMinutes(-3);
        var status = LoopStatus(at, loop =>
        {
            switch (state)
            {
                case "enacted":
                    loop.Enacted = new LoopEnacted { Timestamp = Iso(at), Received = true, Rate = 1, Duration = 30 };
                    break;
                case "failure":
                    loop.FailureReason = "pumpDataTooOld";
                    break;
                case "unreceived":
                    loop.Enacted = new LoopEnacted { Timestamp = Iso(at), Received = false };
                    break;
                case "recommended":
                    loop.RecommendedTempBasal = new LoopRecommendedTempBasal { Timestamp = Iso(at), Rate = 1, Duration = 30 };
                    break;
            }
        });

        var display = Build(false, status).Loop["display"]!;

        display["code"]!.GetValue<string>().Should().Be(state switch
        {
            "failure" or "unreceived" => "error",
            "recommended" => "recommendation",
            _ => state,
        });
        display["symbol"]!.GetValue<string>().Should().Be(symbol);
        display["label"]!.GetValue<string>().Should().Be(label);
    }

    [Fact]
    public void Loop_OlderThanHalfTheWarning_IsAWarning()
    {
        var display = Build(false, LoopStatus(Now.AddMinutes(-16))).Loop["display"]!;

        display["code"]!.GetValue<string>().Should().Be("warning");
    }

    [Fact]
    public void Loop_LastOkMoment_SkipsAFailedLoop()
    {
        var ok = Now.AddMinutes(-8);
        var loop = Build(
            false,
            LoopStatus(ok, l => l.Predicted = new LoopPredicted { StartDate = Iso(ok), Values = [100, 110] }),
            LoopStatus(Now.AddMinutes(-3), l => l.FailureReason = "pumpDataTooOld")).Loop;

        loop["lastOkMoment"]!.GetValue<string>().Should().Be(Iso(ok));
        loop["lastLoop"]!["failureReason"]!.GetValue<string>().Should().Be("pumpDataTooOld");
        loop["lastLoop"]!["moment"]!.GetValue<string>().Should().Be(Iso(Now.AddMinutes(-3)));
        loop["lastPredicted"]!["startDate"]!.GetValue<string>().Should().Be(Iso(ok));
    }

    [Fact]
    public void Loop_LastOverride_IsTheNewestStampedOverride()
    {
        var older = LoopStatus(Now.AddMinutes(-8));
        older.Override = new OverrideStatus { Name = "Exercise", Timestamp = Iso(Now.AddMinutes(-8)), Multiplier = 0.5 };
        var newer = LoopStatus(Now.AddMinutes(-3));
        newer.Override = new OverrideStatus { Name = "Pre-Meal", Timestamp = Iso(Now.AddMinutes(-3)), Multiplier = 1.2 };

        var loop = Build(false, newer, older).Loop;

        loop["lastOverride"]!["name"]!.GetValue<string>().Should().Be("Pre-Meal");
        loop["lastOverride"]!["moment"]!.GetValue<string>().Should().Be(Iso(Now.AddMinutes(-3)));
    }

    [Fact]
    public void Loop_AnOverrideOnlyAnOlderStatusCarried_IsNotOffered()
    {
        var older = LoopStatus(Now.AddMinutes(-8));
        older.Override = new OverrideStatus { Name = "Exercise", Timestamp = Iso(Now.AddMinutes(-8)), Multiplier = 0.5 };

        Build(false, LoopStatus(Now.AddMinutes(-3)), older).Loop.ContainsKey("lastOverride").Should().BeFalse();
    }

    [Fact]
    public void Loop_WithNoOverride_OffersNoLastOverride() =>
        Build(false, LoopStatus(Now.AddMinutes(-3))).Loop.ContainsKey("lastOverride").Should().BeFalse();

    [Fact]
    public void Loop_IgnoresAStatusOlderThanSixHours()
    {
        var loop = Build(false, LoopStatus(Now.AddHours(-6).AddMinutes(-1))).Loop;

        loop["lastLoop"].Should().BeNull();
        loop["lastOkMoment"].Should().BeNull();
    }

    private static DeviceStatus OpenApsStatus(
        DateTimeOffset at, bool? received, bool suggested = true, double eventualBg = 160)
    {
        var status = Status(at);
        status.OpenAps = new OpenApsStatus
        {
            Suggested = suggested
                ? new OpenApsSuggested { Timestamp = Iso(at), EventualBG = eventualBg, PredBGs = new OpenApsPredBGs { IOB = [150, 155] } }
                : null,
            Enacted = received is { } r
                ? new OpenApsEnacted { Timestamp = Iso(at), Received = r, EventualBG = eventualBg, Rate = 1.5, Duration = 30 }
                : null,
            Iob = new OpenApsIobData { Iob = 1.2, BasalIob = 0.3 },
        };
        return status;
    }

    [Fact]
    public void OpenAps_WithAReceivedCommand_IsEnactedAndLoopsAtIt()
    {
        var at = Now.AddMinutes(-4);
        var openAps = Build(false, OpenApsStatus(at, received: true, suggested: false, eventualBg: 171)).OpenAps;

        ShouldBe(openAps["status"]!, "⌁", "enacted", "Enacted");
        openAps["lastLoopMoment"]!.GetValue<string>().Should().Be(Iso(at));
        openAps["lastEventualBG"]!.GetValue<double>().Should().Be(171);
        openAps["lastEnacted"]!["moment"]!.GetValue<string>().Should().Be(Iso(at));
        openAps["lastNotEnacted"].Should().BeNull();
    }

    [Fact]
    public void OpenAps_WithANewerCommandNotReceived_IsNotEnacted()
    {
        var openAps = Build(
            false,
            OpenApsStatus(Now.AddMinutes(-9), received: true),
            OpenApsStatus(Now.AddMinutes(-4), received: false)).OpenAps;

        ShouldBe(openAps["status"]!, "x", "notenacted", "Not Enacted");
        openAps["lastEnacted"]!["moment"]!.GetValue<string>().Should().Be(Iso(Now.AddMinutes(-9)));
        openAps["lastNotEnacted"]!["moment"]!.GetValue<string>().Should().Be(Iso(Now.AddMinutes(-4)));
        openAps["lastPredBGs"]!["moment"]!.GetValue<string>().Should().Be(Iso(Now.AddMinutes(-4)));
    }

    [Fact]
    public void OpenAps_WithOnlyASuggestion_IsLooping()
    {
        var openAps = Build(false, OpenApsStatus(Now.AddMinutes(-4), received: null)).OpenAps;

        ShouldBe(openAps["status"]!, "↻", "looping", "Looping");
        ShouldBe(openAps["seenDevices"]!["openaps://rig"]!["status"]!, "↻", "looping", "Looping");
        openAps["lastLoopMoment"]!.GetValue<string>().Should().Be(Iso(Now.AddMinutes(-4)));
    }

    [Fact]
    public void OpenAps_WithASuggestionNewerThanAnOldCommand_IsLooping()
    {
        var openAps = Build(
            false,
            OpenApsStatus(Now.AddMinutes(-20), received: true, suggested: false),
            OpenApsStatus(Now.AddMinutes(-4), received: null)).OpenAps;

        ShouldBe(openAps["status"]!, "↻", "looping", "Looping");
        openAps["lastLoopMoment"]!.GetValue<string>().Should().Be(Iso(Now.AddMinutes(-4)));
    }

    [Fact]
    public void OpenAps_WhenTheLastLoopIsOld_WarnsButTheDeviceWaits()
    {
        var openAps = Build(false, OpenApsStatus(Now.AddMinutes(-20), received: true)).OpenAps;

        ShouldBe(openAps["status"]!, "⚠", "warning", "Warning");
        var device = openAps["seenDevices"]!["openaps://rig"]!;
        device["name"]!.GetValue<string>().Should().Be("rig");
        ShouldBe(device["status"]!, "◉", "waiting", "Waiting");
        device["status"]!["when"]!.GetValue<string>().Should().Be(Iso(Now.AddMinutes(-20)));
    }

    private static void ShouldBe(JsonNode status, string symbol, string code, string label)
    {
        status["symbol"]!.GetValue<string>().Should().Be(symbol);
        status["code"]!.GetValue<string>().Should().Be(code);
        status["label"]!.GetValue<string>().Should().Be(label);
    }

    [Fact]
    public void OpenAps_TakesAStoredIobAtTheStatusTime()
    {
        var at = Now.AddMinutes(-4);
        var lastIob = Build(false, OpenApsStatus(at, received: true)).OpenAps["lastIOB"]!;

        lastIob["iob"]!.GetValue<double>().Should().Be(1.2);
        lastIob["timestamp"]!.GetValue<string>().Should().Be(Iso(at));
        lastIob["moment"]!.GetValue<string>().Should().Be(Iso(at));
    }

    [Fact]
    public void OpenAps_WithNoStatus_WarnsWithNoLoop()
    {
        var openAps = Build().OpenAps;

        openAps["seenDevices"]!.AsObject().Count.Should().Be(0);
        foreach (var name in new[] { "lastEnacted", "lastNotEnacted", "lastSuggested", "lastIOB", "lastMMTune", "lastPredBGs" })
        {
            openAps.ContainsKey(name).Should().BeTrue();
            openAps[name].Should().BeNull();
        }
        openAps.ContainsKey("lastLoopMoment").Should().BeFalse();
        openAps["status"]!["code"]!.GetValue<string>().Should().Be("warning");
    }

    [Fact]
    public void Pump_CarriesTheMomentsTheOtherPluginsStamp()
    {
        var at = Now.AddMinutes(-4);
        var status = OpenApsStatus(at, received: true);
        status.Pump = new PumpStatus { Clock = Iso(at), Reservoir = 100 };

        var pump = Build(false, status).Pump;

        pump["openaps"]!["enacted"]!["moment"]!.GetValue<string>().Should().Be(Iso(at));
        pump["openaps"]!["iob"]!["moment"]!.GetValue<string>().Should().Be(Iso(at));
    }
}
