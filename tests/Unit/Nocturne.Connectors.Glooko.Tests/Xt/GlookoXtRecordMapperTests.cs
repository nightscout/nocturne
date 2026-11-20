using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Glooko.Xt;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Xt;

public class GlookoXtRecordMapperTests
{
    private readonly GlookoXtRecordMapper _mapper = new(NullLogger.Instance);
    private const string At = "2026-06-17T20:15:30.000Z";
    private static readonly DateTime AtUtc = new(2026, 6, 17, 20, 15, 30, DateTimeKind.Utc);

    private static GlookoXtRecord Record(long id = 1, Action<GlookoXtRecord>? set = null)
    {
        var r = new GlookoXtRecord { Id = id, RecordedAt = At };
        set?.Invoke(r);
        return r;
    }

    [Fact]
    public void SensorGlucose_ComesFromGlycemiaCgm_ConvertedFromTheAccountUnit()
    {
        var batch = _mapper.Map([Record(set: r => { r.GlycemiaCgm = 6.7; r.Glycemia = 6.7; })], "mmol");

        var sg = batch.SensorGlucose.Should().ContainSingle().Subject;
        sg.Mgdl.Should().Be(121);
        sg.Timestamp.Should().Be(AtUtc);
        sg.SyncIdentifier.Should().Be("glookoxt_1_cgm");
        sg.LegacyId.Should().Be("glookoxt_1_cgm");
        sg.DataSource.Should().Be(DataSources.GlookoConnector);
        batch.BGChecks.Should().BeEmpty("a CGM record echoes the value in glycemia too; it is one reading");
    }

    [Fact]
    public void ManualGlucose_BecomesAFingerstickBGCheck()
    {
        var batch = _mapper.Map([Record(set: r => r.Glycemia = 120)], "mgdl");

        var bg = batch.BGChecks.Should().ContainSingle().Subject;
        bg.Glucose.Should().Be(120);
        bg.Units.Should().Be(GlucoseUnit.MgDl);
        bg.GlucoseType.Should().Be(GlucoseType.Finger);
        bg.SyncIdentifier.Should().Be("glookoxt_1_bg");
        batch.SensorGlucose.Should().BeEmpty();
    }

    [Fact]
    public void AutoUnit_IsInferredAcrossTheWholeBatch()
    {
        var batch = _mapper.Map([
            Record(1, r => r.GlycemiaCgm = 5.5),
            Record(2, r => r.GlycemiaCgm = 6.1),
        ], "Auto");

        batch.SensorGlucose.Select(s => s.Mgdl).Should().BeEquivalentTo([99, 110]);
    }

    [Fact]
    public void OneRecord_FansOutIntoEveryFacetItCarries()
    {
        var batch = _mapper.Map([Record(set: r =>
        {
            r.Glycemia = 180;
            r.FastInsulin = 4.5;
            r.CorrectionInsulin = 1.0;
            r.Carbs = 45;
            r.Note = "Meal Bolus";
        })], "mgdl");

        batch.BGChecks.Should().ContainSingle();
        batch.Boluses.Should().HaveCount(2);
        batch.Boluses.Select(b => b.SyncIdentifier).Should().BeEquivalentTo(["glookoxt_1_fast", "glookoxt_1_corr"]);
        batch.Boluses.Should().OnlyContain(b => b.Kind == BolusKind.Manual && b.BolusType == Nocturne.Core.Models.V4.BolusType.Normal);
        batch.CarbIntakes.Should().ContainSingle().Which.Carbs.Should().Be(45);
        batch.Notes.Should().BeEmpty("a note on a record that yielded data is that record's label, not a logbook entry");
        batch.Total.Should().Be(4);
    }

    [Fact]
    public void SmbMarker_MakesAnAlgorithmBolus()
    {
        var batch = _mapper.Map([Record(set: r => { r.FastInsulin = 0.3; r.Note = "SMB"; })], "mgdl");

        var bolus = batch.Boluses.Should().ContainSingle().Subject;
        bolus.Kind.Should().Be(BolusKind.Algorithm);
        bolus.Automatic.Should().BeTrue();
    }

    [Fact]
    public void ExtendedBolus_IsSquareWithItsDuration()
    {
        var batch = _mapper.Map([Record(set: r => { r.FastInsulin = 3; r.BolusRate = 1.5; r.Duration = 120; })], "mgdl");

        var bolus = batch.Boluses.Should().ContainSingle().Subject;
        bolus.BolusType.Should().Be(Nocturne.Core.Models.V4.BolusType.Square);
        bolus.Duration.Should().Be(120);
    }

    [Fact]
    public void SlowInsulin_BecomesABasalInjection()
    {
        var batch = _mapper.Map([Record(set: r => r.SlowInsulin = 18)], "mgdl");

        batch.BasalInjections.Should().ContainSingle().Which.Units.Should().Be(18);
    }

    [Fact]
    public void BasalRate_BecomesATempBasal_WithDurationAsEnd()
    {
        var batch = _mapper.Map([Record(set: r => { r.BasalRate = 0.85; r.Duration = 30; })], "mgdl");

        var tb = batch.TempBasals.Should().ContainSingle().Subject;
        tb.Rate.Should().Be(0.85);
        tb.StartTimestamp.Should().Be(AtUtc);
        tb.EndTimestamp.Should().Be(AtUtc.AddMinutes(30));
        tb.Origin.Should().Be(TempBasalOrigin.Manual);
    }

    [Fact]
    public void BasalRate_WithAPercentage_IsAnAlgorithmBasal()
    {
        var batch = _mapper.Map([Record(set: r => { r.BasalRate = 1.2; r.RatePercentage = 150; r.Duration = 30; })], "mgdl");

        batch.TempBasals.Should().ContainSingle().Which.Origin.Should().Be(TempBasalOrigin.Algorithm);
    }

    [Fact]
    public void ZeroRateWithDuration_IsAZeroSpanWithAnEnd()
    {
        var batch = _mapper.Map([Record(set: r => { r.BasalRate = 0; r.Duration = 60; })], "mgdl");

        var tb = batch.TempBasals.Should().ContainSingle().Subject;
        tb.Rate.Should().Be(0);
        tb.EndTimestamp.Should().Be(AtUtc.AddMinutes(60));
    }

    private const string CamApsMemo = """{"source":"CamAPS Sync","detail":null}""";

    [Fact]
    public void StreamedBasal_EachRateRunsUntilTheNext_LastLeftOpen_ZeroKept()
    {
        var batch = _mapper.Map([
            Record(1, r => { r.BasalRate = 1.2; r.RecordedAt = "2026-09-06T01:00:00Z"; r.Memo = CamApsMemo; r.ProductPumpId = 97; }),
            Record(2, r => { r.BasalRate = 0; r.RecordedAt = "2026-09-06T01:11:00Z"; r.Memo = CamApsMemo; r.ProductPumpId = 97; }),
            Record(3, r => { r.BasalRate = 0.8; r.RecordedAt = "2026-09-06T01:22:00Z"; r.Memo = CamApsMemo; r.ProductPumpId = 97; }),
        ], "mgdl", new Dictionary<long, string> { [97] = "Ypsomed YpsoPump" });

        var spans = batch.TempBasals.OrderBy(t => t.StartTimestamp).ToList();
        spans.Should().HaveCount(3);
        spans[0].Rate.Should().Be(1.2);
        spans[0].EndTimestamp.Should().Be(spans[1].StartTimestamp);
        spans[1].Rate.Should().Be(0, "a zero rate is the loop withholding basal, not a gap");
        spans[1].EndTimestamp.Should().Be(spans[2].StartTimestamp);
        spans[2].EndTimestamp.Should().BeNull("the latest rate is still in force");
        spans.Should().OnlyContain(t => t.Origin == TempBasalOrigin.Algorithm, "CamAPS is a closed loop");
        spans.Should().OnlyContain(t => t.Device == "Ypsomed YpsoPump");
        batch.Notes.Should().BeEmpty("the provenance blob in memo is not a note");
        batch.Skipped.Should().Be(0);
    }

    [Fact]
    public void StreamedBasal_TwoChangesAtTheSameInstant_KeepTheLaterId()
    {
        var batch = _mapper.Map([
            Record(10, r => { r.BasalRate = 0.6; r.RecordedAt = "2026-09-06T01:51:14Z"; r.Memo = CamApsMemo; }),
            Record(11, r => { r.BasalRate = 0.65; r.RecordedAt = "2026-09-06T01:51:14Z"; r.Memo = CamApsMemo; }),
            Record(12, r => { r.BasalRate = 0.4; r.RecordedAt = "2026-09-06T02:02:00Z"; r.Memo = CamApsMemo; }),
        ], "mgdl");

        batch.TempBasals.Should().HaveCount(2);
        batch.TempBasals.Select(t => t.SyncIdentifier).Should().BeEquivalentTo(["glookoxt_11_basal", "glookoxt_12_basal"]);
    }

    [Fact]
    public void StreamedBasal_FromANonLoopSync_IsScheduled_AndTypedIn_IsManual()
    {
        var batch = _mapper.Map([
            Record(1, r => { r.BasalRate = 1.0; r.Memo = """{"source":"Some Pump Sync"}"""; }),
            Record(2, r => { r.BasalRate = 1.0; r.RecordedAt = "2026-06-18T20:15:30.000Z"; }),
        ], "mgdl");

        batch.TempBasals.Single(t => t.SyncIdentifier == "glookoxt_1_basal").Origin.Should().Be(TempBasalOrigin.Scheduled);
        batch.TempBasals.Single(t => t.SyncIdentifier == "glookoxt_2_basal").Origin.Should().Be(TempBasalOrigin.Manual);
    }

    [Fact]
    public void SuspendWithZeroRate_IsASuspendedSpan_AndADeviceEvent()
    {
        var batch = _mapper.Map([Record(set: r =>
        {
            r.BasalRate = 0; r.PumpStop = true;
            r.Memo = """{"source":"CamAPS Sync","detail":{"raw_alarm":"alarm_occlusion"}}""";
        })], "mgdl");

        batch.TempBasals.Should().ContainSingle().Which.Origin.Should().Be(TempBasalOrigin.Suspended);
        batch.DeviceEvents.Should().ContainSingle().Which.EventType.Should().Be(DeviceEventType.PumpSuspend);
        var alarm = batch.SystemEvents.Should().ContainSingle().Subject;
        alarm.Code.Should().Be("alarm_occlusion");
        alarm.Description.Should().Be("Occlusion");
        alarm.Category.Should().Be(SystemEventCategory.Pump);
        alarm.EventType.Should().Be(SystemEventType.Alarm);
        alarm.OriginalId.Should().Be("glookoxt_1_alarm");
    }

    [Fact]
    public void WizardRecord_BecomesABolusCalculation_NotANote()
    {
        var batch = _mapper.Map([Record(set: r => r.Memo = """
            {"source":"CamAPS Sync","detail":{"total_value":8,"suggestion_based_on_bg":"no","suggestion_based_on_carb":"yes","suggestion_overridden":"no"},"type":"wizard","deviceId":"CamAPS_X","recommended":{"carb":8,"net":8,"correction":0},"bgInput":null,"carbInput":40}
            """)], "mgdl");

        var calc = batch.BolusCalculations.Should().ContainSingle().Subject;
        calc.InsulinRecommendation.Should().Be(8);
        calc.InsulinRecommendationForCarbs.Should().Be(8);
        calc.CarbInput.Should().Be(40);
        calc.BloodGlucoseInput.Should().BeNull();
        calc.CalculationType.Should().Be(Nocturne.Core.Models.V4.CalculationType.Suggested);
        calc.LegacyId.Should().Be("glookoxt_1_wizard");
        batch.Notes.Should().BeEmpty();
        batch.Skipped.Should().Be(0);
    }

    [Fact]
    public void Wizard_KeepsTheSuggestedAndTheDeliveredDose()
    {
        var batch = _mapper.Map([
            Record(1, r => r.Memo = """{"source":"CamAPS Sync","detail":{"total_value":0.81,"suggestion_overridden":"no"},"type":"wizard","recommended":{"carb":3.5,"net":3.5,"correction":0},"bgInput":null,"carbInput":null}"""),
            Record(2, r => { r.FastInsulin = 0.81; r.Memo = CamApsMemo; }),
        ], "mgdl");

        var calc = batch.BolusCalculations.Should().ContainSingle().Subject;
        calc.InsulinRecommendation.Should().Be(3.5, "what the calculator proposed");
        calc.InsulinProgrammed.Should().Be(0.81, "what was actually given");
        calc.EnteredInsulin.Should().Be(0.81);
        batch.Boluses.Should().ContainSingle().Which.BolusCalculationId.Should().BeNull("the calculation is upserted by legacy id, so a pointer at a fresh Id would not survive a re-read");
    }

    [Fact]
    public void SyncedCgm_IsStampedWithTheCgmProduct_NotThePump()
    {
        var batch = _mapper.Map([Record(set: r => { r.GlycemiaCgm = 85.07; r.ProductGlycemiaId = 85; r.ProductPumpId = 97; r.Memo = CamApsMemo; })],
            "mgdl", new Dictionary<long, string> { [85] = "Abbott FreeStyle Libre", [97] = "Ypsomed YpsoPump" });

        var sg = batch.SensorGlucose.Should().ContainSingle().Subject;
        sg.Mgdl.Should().Be(85);
        sg.Device.Should().Be("Ypsomed YpsoPump", "the pump that relayed the reading is the record's device; the CGM is named only when no pump is");
    }

    [Fact]
    public void UnknownProduct_FallsBackToTheSyncSource_ThenTheConnector()
    {
        var batch = _mapper.Map([
            Record(1, r => { r.Carbs = 10; r.Memo = CamApsMemo; r.ProductPumpId = 999; }),
            Record(2, r => r.Carbs = 10),
        ], "mgdl");

        batch.CarbIntakes.Single(c => c.SyncIdentifier == "glookoxt_1_carbs").Device.Should().Be("CamAPS Sync");
        batch.CarbIntakes.Single(c => c.SyncIdentifier == "glookoxt_2_carbs").Device.Should().Be(DataSources.GlookoConnector);
    }

    [Fact]
    public void PercentageAlone_IsDroppedNotGuessed()
    {
        var batch = _mapper.Map([Record(set: r => { r.RatePercentage = 150; r.Duration = 30; })], "mgdl");

        batch.TempBasals.Should().BeEmpty();
        batch.Skipped.Should().Be(0, "a known-but-unmappable facet is not an empty record");
    }

    [Fact]
    public void PumpFlags_BecomeDeviceEvents()
    {
        var batch = _mapper.Map([
            Record(1, r => r.PumpStop = true),
            Record(2, r => r.PumpResume = true),
            Record(3, r => r.IsPrime = true),
        ], "mgdl");

        batch.DeviceEvents.Select(e => e.EventType).Should().BeEquivalentTo(
            [DeviceEventType.PumpSuspend, DeviceEventType.PumpResume, DeviceEventType.Priming]);
        batch.DeviceEvents.Select(e => e.SyncIdentifier).Should().BeEquivalentTo(
            ["glookoxt_1_suspend", "glookoxt_2_resume", "glookoxt_3_prime"]);
    }

    [Fact]
    public void NoteOnlyRecord_BecomesANote_JoiningNoteAndMemo()
    {
        var batch = _mapper.Map([Record(set: r => { r.Note = "Felt low"; r.Memo = "after walk"; })], "mgdl");

        batch.Notes.Should().ContainSingle().Which.Text.Should().Be("Felt low — after walk");
    }

    [Fact]
    public void Medication_IsKeptAsANote_EvenBesideOtherData()
    {
        var batch = _mapper.Map([Record(set: r => { r.Carbs = 20; r.MedicationName = "Metformin"; r.MedicationValue = 500; })], "mgdl");

        batch.CarbIntakes.Should().ContainSingle();
        batch.Notes.Should().ContainSingle().Which.Text.Should().Be("Metformin 500");
    }

    [Fact]
    public void RecordsWithoutIdOrTimestamp_AreSkipped()
    {
        var batch = _mapper.Map([
            new GlookoXtRecord { Id = null, RecordedAt = At, Carbs = 10 },
            new GlookoXtRecord { Id = 5, RecordedAt = "yesterday", Carbs = 10 },
        ], "mgdl");

        batch.Total.Should().Be(0);
        batch.Skipped.Should().Be(2);
    }

    [Fact]
    public void Timestamp_WithoutAZone_IsTakenAsUtc()
    {
        GlookoXtRecordMapper.TryParseTimestamp("2026-06-17T20:15:30", out var utc).Should().BeTrue();
        utc.Should().Be(AtUtc);
        utc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Timestamp_WithAnOffset_IsConvertedToUtc()
    {
        GlookoXtRecordMapper.TryParseTimestamp("2026-06-17T22:15:30+02:00", out var utc).Should().BeTrue();
        utc.Should().Be(AtUtc);
    }
}
