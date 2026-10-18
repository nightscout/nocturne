using FluentAssertions;
using Nocturne.Connectors.Glooko.Xt;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Xt;

public class GlookoXtExportParserTests
{
    internal const string SampleCsv =
        "GLOOKO XT EXPORT - 20/09/2026\n" +
        "PERIOD;06/09/2026 to 21/09/2026\n" +
        "TIMEZONE;Europe/Paris\n" +
        "DOE;JANE;UNDEFINED;.;jane@example.com\n" +
        "\n" +
        "Date;Pump device;BG device;Blood glucose (mg/dl);Rapid injections / Bolus (u);Type;Low injections (u);Type;Serial number;Comments;Basal rate (u/h);Basal detail;Basal delivery type;Duration (ms);Bolus type;Injection correction;Injection carbs;Event;Settings;Carbs;Weight;BMI;Blood Ketone;Activity (steps);Activity (minutes);Carb ratio;Sensitivity;Schedule name;Priming;IOB;Meal tags\n" +
        "06/09/2026 13:09;YpsoPump;;;;;;;CamAPS_X;;;;;103000;;false;false;pumpMode - Attempting;;;;;;;;;;;false;;\n" +
        "06/09/2026 13:11;YpsoPump;;;;;;;CamAPS_X;;;;;38553000;;false;false;pumpMode - Closed loop;;;;;;;;;;;false;;\n" +
        "06/09/2026 11:29;YpsoPump;;;;;;;CamAPS_X;;;;;3600000;;false;false;pumpSettingsOverride - boost;;;;;;;;;;;false;;\n" +
        "07/09/2026 15:51;YpsoPump;;;;;;;CamAPS_X;;;;;7200000;;false;false;pumpSettingsOverride - easeOff;;;;;;;;;;;false;;\n" +
        "07/09/2026 08:00;YpsoPump;;;;;;;CamAPS_X;;;;;;;false;false;Alerte de sécurité - Hypoglycémie : 69.06 mg/dl;;;;;;;;;;;false;;\n" +
        "07/09/2026 09:00;YpsoPump;;;;;;;CamAPS_X;;;;;;;false;false;ReservoirChange;;;;;;;;;;;false;;\n" +
        "07/09/2026 09:30;YpsoPump;;;;;;;CamAPS_X;;;;;;;false;false;AlarmOther;;;;;;;;;;;false;;\n" +
        "07/09/2026 09:31;YpsoPump;;;;;;;CamAPS_X;;;;;;;false;false;AlarmOcclusion;;;;;;;;;;;false;;\n" +
        "07/09/2026 10:00;YpsoPump;;;;;;;CamAPS_X;;;;;;;false;false;info - Total daily dose;;;;;;;;;;;false;;\n" +
        "19/09/2026 12:38;YpsoPump;;;;;;;CamAPS_X;;;;;;;false;false;pump settings;\"{\"\"time\"\":\"\"2026-09-19T10:38:55.895Z\"\",\"\"deviceId\"\":\"\"CamAPS_X\"\",\"\"type\"\":\"\"pumpSettings\"\",\"\"activeSchedule\"\":\"\"1\"\",\"\"units\"\":{\"\"carb\"\":\"\"g\"\",\"\"bg\"\":\"\"mg/dl\"\"},\"\"basalSchedules\"\":{\"\"1\"\":[{\"\"rate\"\":0.8,\"\"start\"\":0},{\"\"rate\"\":0.7,\"\"start\"\":28800000}]},\"\"carbRatios\"\":{\"\"1\"\":[{\"\"amount\"\":10,\"\"start\"\":0}]},\"\"insulinSensitivities\"\":{\"\"1\"\":[{\"\"amount\"\":40,\"\"start\"\":0}]},\"\"glucoseTarget\"\":{\"\"1\"\":[{\"\"value\"\":90,\"\"start\"\":\"\"00:00\"\"}]},\"\"model\"\":\"\"mylife YpsoPump\"\",\"\"automatedDelivery\"\":true,\"\"iobDuration\"\":9000,\"\"glucoseTargetLow\"\":70.26,\"\"glucoseTargetHigh\"\":180.15}\";;;;;;;;;;false;;\n" +
        "19/09/2026 12:40;YpsoPump;FreeStyle Libre;121.1;;;;;CamAPS_X;;;;;;;false;false;;;;;;;;;;;;false;;\n";

    [Fact]
    public void Parse_ReadsTheZone_AndConvertsRowMinutesToUtc()
    {
        var export = GlookoXtExportParser.Parse(SampleCsv);

        export.TimeZoneId.Should().Be("Europe/Paris");
        export.Rows.Should().HaveCount(11);
        var attempting = export.Rows.Single(r => r.Event == "pumpMode - Attempting");
        attempting.TimestampUtc.Should().Be(new DateTime(2026, 9, 6, 11, 9, 0, DateTimeKind.Utc), "13:09 Paris in September is 11:09 UTC");
        attempting.DurationMs.Should().Be(103_000);
        attempting.PumpDevice.Should().Be("YpsoPump");
    }

    [Fact]
    public void Parse_KeepsTheQuotedSettingsJsonIntact()
    {
        var export = GlookoXtExportParser.Parse(SampleCsv);
        var settingsRow = export.Rows.Single(r => r.SettingsJson is not null);

        var settings = GlookoXtPumpSettings.TryParse(settingsRow.SettingsJson);
        settings.Should().NotBeNull();
        settings!.ActiveSchedule.Should().Be("1");
        settings.Model.Should().Be("mylife YpsoPump");
        settings.IobDurationSeconds.Should().Be(9000);
        settings.BasalSchedules["1"].Should().Equal([(0, 0.8), (28_800_000, 0.7)]);
        settings.CarbRatios["1"].Should().Equal([(0, 10.0)]);
        settings.InsulinSensitivities["1"].Should().Equal([(0, 40.0)]);
        settings.GlucoseTargets["1"].Should().Equal([(0, 90.0)]);
        settings.GlucoseTargetLow.Should().Be(70.26);
        settings.Time.Should().Be(new DateTime(2026, 9, 19, 10, 38, 55, 895, DateTimeKind.Utc));
    }

    [Fact]
    public void Parse_EmptyOrHeaderless_IsEmpty()
    {
        GlookoXtExportParser.Parse("").Rows.Should().BeEmpty();
        GlookoXtExportParser.Parse("GLOOKO XT EXPORT\nTIMEZONE;Europe/Paris\n").Rows.Should().BeEmpty();
    }

    [Fact]
    public void ResolveZone_UnknownFallsBackToUtc()
    {
        GlookoXtExportParser.ResolveZone("Mars/Olympus").Should().Be(TimeZoneInfo.Utc);
        GlookoXtExportParser.ResolveZone(null).Should().Be(TimeZoneInfo.Utc);
    }
}
