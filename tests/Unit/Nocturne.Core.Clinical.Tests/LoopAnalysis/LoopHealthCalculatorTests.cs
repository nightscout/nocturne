using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

public class LoopHealthCalculatorTests
{
    // Sydney in June is UTC+10 with no DST.
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;

    /// <summary>Local midnight starting 2024-06-10 in Sydney.</summary>
    private static readonly long Day0 = new DateTimeOffset(2024, 6, 10, 0, 0, 0, TimeSpan.FromHours(10)).ToUnixTimeMilliseconds();

    private static LoopHealthRange Calculate(
        long from,
        long to,
        IEnumerable<ApsLoopCycle>? cycles = null,
        IEnumerable<DeliveredBasalSegment>? delivered = null,
        IEnumerable<InsulinDose>? doses = null,
        IntervalSet? recorded = null,
        IntervalSet? inferable = null,
        IEnumerable<SensorGlucose>? glucose = null,
        StepTimeline<double>? loopMaximum = null,
        LoopAnalysisMasks? masks = null,
        LoopAnalysisMaskSources? sources = null,
        IEnumerable<TempBasal>? temps = null)
    {
        var deliveredList = (delivered ?? []).ToList();
        var inputs = new LoopHealthInputs
        {
            Zone = Sydney,
            Cycles = [.. cycles ?? []],
            Insulin = new NetInsulinAboveSchedule(deliveredList, [], doses ?? []),
            Evidence = new BasalEvidence(recorded ?? IntervalSet.Empty, IntervalSet.Empty, inferable ?? IntervalSet.Empty),
            Temps = [.. (temps ?? deliveredList.Select(d => TempAt(d.Start))).OrderBy(t => t.StartMills)],
            Glucose = [.. glucose ?? []],
            LoopMaximumBasalRate = loopMaximum,
        };
        var mask = LoopAnalysisMask.Build(
            masks ?? new LoopAnalysisMasks { ExcludeIllness = false, ExcludeOpenLoop = false },
            from, to, Sydney, sources ?? new LoopAnalysisMaskSources());
        return LoopHealthCalculator.Calculate(inputs, mask, from, to);
    }

    private static ApsLoopCycle Cycle(
        long mills,
        AidAlgorithm algorithm = AidAlgorithm.Trio,
        bool enacted = true,
        double? rate = null,
        string? limitReason = null,
        bool hasReason = true,
        double? iob = null,
        double? sensitivity = null,
        bool? suggestedChange = true,
        Guid? id = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime,
            AidAlgorithm = algorithm,
            Enacted = enacted,
            EnactedRate = rate,
            Iob = iob,
            SensitivityRatio = sensitivity,
            HasReason = algorithm != AidAlgorithm.Loop && hasReason,
            SuggestedChange = algorithm != AidAlgorithm.Loop ? suggestedChange : null,
            LimitReason = limitReason,
        };

    /// <summary>A temp's start, origin evidence and setter, as <see cref="LoopHealthInputs.Temps"/> reads them.</summary>
    private static TempBasal TempAt(
        long start, TempBasalOriginEvidence evidence = TempBasalOriginEvidence.Reported, Guid? setter = null) => new()
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime,
            Origin = TempBasalOrigin.Algorithm,
            OriginEvidence = evidence,
            ApsSnapshotId = setter,
        };

    private static IEnumerable<ApsLoopCycle> EveryFiveMinutes(long from, long to, Func<long, ApsLoopCycle> cycle)
    {
        for (var t = from; t < to; t += 5 * Minute)
            yield return cycle(t);
    }

    /// <summary>The algorithm temp each enacting cycle set, recorded when it ran, until the next one did.</summary>
    private static List<DeliveredBasalSegment> TempsSetBy(IReadOnlyList<ApsLoopCycle> cycles, long to)
    {
        var setters = cycles.Where(c => c.Enacted && c.EnactedRate is not null).ToList();
        return [.. setters.Select((c, i) => new DeliveredBasalSegment(
            c.Mills, i + 1 < setters.Count ? setters[i + 1].Mills : to, c.EnactedRate!.Value, BasalDeliveryOrigin.Algorithm))];
    }

    private const string Capped = "adj. req. rate: 3.2 to maxSafeBasal: 2.4, temp 1.2 < 2.4U/hr.";

    // Shapes after oref0 lib/determine-basal/determine-basal.js, AAPS DetermineBasalSMB.kt (which
    // formats most numbers in the phone's locale, but writes max_iob from a string template) and
    // Trio's determine-basal; the values are made up.
    [Theory]
    [InlineData("COB: 0, Dev: 25, BGI: -1.5, ISF: 45, CR: 9, minPredBG 128, minGuardBG 118, IOBpredBG 140; Eventual BG 156 >= 100, insulinReq 0.62. adj. req. rate: 3.24 to maxSafeBasal: 2.8, temp 1.2 < 2.8U/hr. ", true, false, null)]
    [InlineData("COB: 0, Dev: 40, BGI: -0.8, ISF: 45, CR: 9, Target: 100; Eventual BG 210 >= 100, IOB 3.21 > max_iob 3; setting current basal of 0.9 as temp. ", false, true, 3.0)]
    [InlineData("COB: 0, Dev: 31, BGI: -1,2, ISF: 2,5, CR: 9, Target: 5,6, minPredBG 7,1; Eventual BG 9,4 >= 5,6, max_iob 3.5, insulinReq 0,8; maxBolus 1,2. adj. req. rate: 3,2 to maxSafeBasal: 2,4, ", true, true, 3.5)]
    [InlineData("COB: 0, Dev: 31, BGI: -1.2, ISF: 45, CR: 9, Target: 100; Eventual BG 170 >= 100, max_iob 2,  insulinReq 0.5; maxBolus 1; setting 30m low temp of 0.4U/h. ", false, true, 2.0)]
    [InlineData("Autosens/Dynamic Limit: 1.2 (1.05), AF: 1, SMB Ratio: 0.5; Eventual BG 180 >= 100, insulinReq 0.9. Microbolusing 0.4U. adj. req. rate: 3.1 to maxSafeBasal: 2.6, temp 1.2 < 2.6U/hr. ", true, false, null)]
    [InlineData("Autosens/Dynamic Limit: 1.2 (1.05), AF: 1, SMB Ratio: 0.5; Eventual BG 120 >= 100, temp 1.2 >~ req 1.15U/hr. ", false, false, null)]
    public void OrefReason_ReadsTheLimitsEachSystemNames(string reason, bool capped, bool atMaxIob, double? maxIob)
    {
        OrefReason.Parse(reason).Should().Be(new OrefReason(capped, atMaxIob, maxIob));
        ApsLoopCycle.NamesALimit(reason).Should().Be(capped || atMaxIob, "the repository keeps a reason only where the parser finds a limit in it");
    }

    [Fact]
    public void UnknownLimits_AreReportedUnknownRatherThanDefaulted()
    {
        var from = Day0;
        var to = Day0 + 2 * Hour;
        List<ApsLoopCycle> cycles =
        [
            .. EveryFiveMinutes(from, from + Hour, t => Cycle(t, rate: 4.0, hasReason: false)),
            .. EveryFiveMinutes(from + Hour, to, t => Cycle(t, AidAlgorithm.Loop, rate: 4.0)),
        ];
        var result = Calculate(from, to, cycles, TempsSetBy(cycles, to));

        var maxBasal = result.MaxBasal!;
        maxBasal.AtCap.Percent.Should().BeNull("no temp's cap is known: no reason to read and no Loop setting");
        maxBasal.CapKnown.Percent.Should().Be(0);
        result.AtMaxIob.Should().BeNull("only oref cycles with a reason say whether max_iob bound");
        result.Limits.Should().BeEquivalentTo(new LoopHealthLimits());
    }

    [Fact]
    public void ATempHeldThroughCyclesThatEnactNothing_IsAtTheCapForAsLongAsItRuns()
    {
        // Enact at the cap, hold for half an hour, enact below it, hold again.
        var from = Day0;
        var to = Day0 + Hour;
        List<ApsLoopCycle> cycles = [.. EveryFiveMinutes(from, to, t => (t - from) switch
        {
            0 => Cycle(t, rate: 2.4, limitReason: Capped),
            30 * Minute => Cycle(t, rate: 1.0),
            _ => Cycle(t, enacted: false, suggestedChange: false),
        })];
        var result = Calculate(from, to, cycles, TempsSetBy(cycles, to));

        var maxBasal = result.MaxBasal!;
        maxBasal.CapKnown.Percent.Should().Be(100);
        maxBasal.AtCap.Percent.Should().Be(50);
        result.Activity.ClosedLoop!.Percent.Should().Be(100, "holding a temp it set, oref is in control");
    }

    [Fact]
    public void ARateThePumpRounded_IsJudgedByTheCycleThatSetIt()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to,
            [Cycle(from, rate: 2.43, limitReason: "adj. req. rate: 3.2 to maxSafeBasal: 2.43, ")],
            [new(from, to, 2.40, BasalDeliveryOrigin.Algorithm)]);

        result.MaxBasal!.CapKnown.Percent.Should().Be(100);
        result.MaxBasal.AtCap.Percent.Should().Be(100);
    }

    [Theory]
    [InlineData(2.43, true)]
    [InlineData(2.4498, false)]
    [InlineData(2.45, false)]
    public void AnEnactedRateIsTheDeliveredOne_OnlyWithinTheTolerance_TheOriginCorroboratorUses(double enacted, bool setter)
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to,
            [Cycle(from, rate: enacted, limitReason: Capped)],
            [new(from, to, 2.40, BasalDeliveryOrigin.Algorithm)]);

        TempBasalOriginProvenance.SameRate(enacted, 2.40).Should().Be(setter);
        result.MaxBasal!.CapKnown.Percent.Should().Be(setter ? 100 : 0);
    }

    [Fact]
    public void APieceOfATempAfterAnotherStreamCoveredItsStart_IsJudgedByTheCycleThatSetTheRecord()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var record = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime,
            Rate = 2.4,
            Origin = TempBasalOrigin.Algorithm,
            OriginEvidence = TempBasalOriginEvidence.Reported,
        };
        var result = Calculate(from, to,
            [Cycle(from, rate: 2.4, limitReason: Capped)],
            [new(from + 20 * Minute, to, 2.4, BasalDeliveryOrigin.Algorithm)],
            temps: [record.WithSpan(from + 20 * Minute, to)]);

        result.MaxBasal!.CapKnown.Percent.Should().Be(100, "the record was set at the start of the hour, not where its piece starts");
        result.MaxBasal.AtCap.Percent.Should().Be(100);
    }

    [Fact]
    public void ATempRecordedMinutesBeforeItsCycle_IsJudgedByThatCycle()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to,
            [Cycle(from, rate: 1.0), Cycle(from + 33 * Minute + 30 * 1000, rate: 2.4, limitReason: Capped)],
            [new(from, from + 30 * Minute, 1.0, BasalDeliveryOrigin.Algorithm), new(from + 30 * Minute, to, 2.4, BasalDeliveryOrigin.Algorithm)]);

        result.MaxBasal!.CapKnown.Percent.Should().Be(100);
        result.MaxBasal.AtCap.Percent.Should().Be(50);
    }

    [Fact]
    public void ATempRecordedAfterTheNextCycleRan_IsJudgedByTheCycleAtItsRate()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to,
            [Cycle(from, rate: 2.4, limitReason: Capped), Cycle(from + 5 * Minute, rate: 1.0)],
            [new(from + 4 * Minute, from + 5 * Minute + 30 * 1000, 2.4, BasalDeliveryOrigin.Algorithm),
             new(from + 5 * Minute + 30 * 1000, to, 1.0, BasalDeliveryOrigin.Algorithm)]);

        result.MaxBasal!.CapKnown.Percent.Should().Be(100);
        result.MaxBasal.AtCap.Percent.Should().Be(SlotSummary.Round(150.0 / 56));
    }

    [Fact]
    public void ATempWhoseSetterIsMissing_IsNotJudgedByAnOlderCycleAtTheSameRate()
    {
        var from = Day0;
        var to = Day0 + Hour;
        List<ApsLoopCycle> cycles = [Cycle(from - 90 * Minute, rate: 2.4), .. EveryFiveMinutes(from - 85 * Minute, to, t =>
            Cycle(t, enacted: false, suggestedChange: false, limitReason: Capped))];
        var result = Calculate(from, to, cycles, [new(from, to, 2.4, BasalDeliveryOrigin.Algorithm)]);

        result.MaxBasal!.CapKnown.Percent.Should().Be(0);
        result.MaxBasal.AtCap.Percent.Should().BeNull();
    }

    [Fact]
    public void PiecesSplitByTheSchedule_BelongToTheTempTheyFallIn()
    {
        // A1's own pieces: a capped temp recorded at 00:00 runs through a schedule change at 00:20.
        var from = Day0;
        var to = Day0 + Hour;
        TempBasal Temp(long start, long end, double rate) => new()
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(end).UtcDateTime,
            Rate = rate, Origin = TempBasalOrigin.Algorithm, Device = "pump",
        };
        var (clipped, evidence) = NetInsulinAboveSchedule.ClipTempBasals(
            [Temp(from, from + 30 * Minute, 2.4), Temp(from + 30 * Minute, to, 1.0)], to);
        var delivered = NetInsulinAboveSchedule.DeliveredFromSeries(
            clipped.Select(t => new BasalPoint { Timestamp = t.StartMills, Rate = t.Rate!.Value, Origin = BasalDeliveryOrigin.Algorithm }),
            to, evidence);
        var insulin = new NetInsulinAboveSchedule(
            delivered, [new RateSegment(from, from + 20 * Minute, 0.8), new RateSegment(from + 20 * Minute, to, 1.0)], []);
        insulin.Pieces.Count(p => p.Delivered == 2.4).Should().Be(2, "the schedule change splits the capped temp");

        var result = LoopHealthCalculator.Calculate(
            new LoopHealthInputs
            {
                Zone = Sydney,
                Cycles = [Cycle(from, rate: 2.4, limitReason: Capped), .. EveryFiveMinutes(from + 5 * Minute, from + 30 * Minute, t =>
                    Cycle(t, enacted: false, suggestedChange: false)), Cycle(from + 30 * Minute, rate: 1.0)],
                Insulin = insulin,
                Evidence = evidence,
                Temps = [.. clipped.OrderBy(t => t.StartMills)],
            },
            LoopAnalysisMask.Build(new LoopAnalysisMasks { ExcludeIllness = false, ExcludeOpenLoop = false }, from, to, Sydney, new()),
            from, to);

        result.MaxBasal!.CapKnown.Percent.Should().Be(100);
        result.MaxBasal.AtCap.Percent.Should().Be(50);
    }

    [Fact]
    public void ATempsRecordedSetter_DecidesItsCapOverTheNearestCycle()
    {
        // The nearest cycle with the rate cut it to maxSafeBasal; the snapshot the temp names did not.
        var from = Day0;
        var to = Day0 + Hour;
        var named = Guid.NewGuid();
        var cycles = new[]
        {
            Cycle(from - 4 * Minute, rate: 2.4, limitReason: "temp 1.2 < 2.4U/hr.", id: named),
            Cycle(from, rate: 2.4, limitReason: Capped),
        };
        LoopHealthMaxBasal MaxBasal(Guid? setter) => Calculate(from, to, cycles,
            [new(from, to, 2.4, BasalDeliveryOrigin.Algorithm)],
            temps: [TempAt(from, setter: setter)]).MaxBasal!;

        MaxBasal(null).AtCap.Percent.Should().Be(100, "without a recorded setter the nearest cycle is taken");
        MaxBasal(named).AtCap.Percent.Should().Be(0);
        MaxBasal(named).CapKnown.Percent.Should().Be(100);
        MaxBasal(Guid.NewGuid()).AtCap.Percent.Should().Be(100, "a setter outside the cycles read falls back to the window");
    }

    [Fact]
    public void ATempNamingACycleThatWasDroppedAsADuplicate_FallsBackToTheNearest()
    {
        // Two uploaders posted the cycle at 00:00; the one that saw it enacted is kept.
        var from = Day0;
        var to = Day0 + Hour;
        var dropped = Guid.NewGuid();
        var cycles = new[]
        {
            Cycle(from, rate: 2.4, limitReason: Capped),
            Cycle(from, enacted: false, rate: 2.4, limitReason: "temp 1.2 < 2.4U/hr.", id: dropped),
        };

        var maxBasal = Calculate(from, to, cycles,
            [new(from, to, 2.4, BasalDeliveryOrigin.Algorithm)],
            temps: [TempAt(from, setter: dropped)]).MaxBasal!;

        maxBasal.AtCap.Percent.Should().Be(100);
    }

    [Fact]
    public void AnUnknownCap_IsExplainedByEachTempsOwnSystem()
    {
        // Loop for the first ten minutes, then Trio for fifty with no stored reasons.
        var from = Day0;
        var to = Day0 + Hour;
        var switched = from + 10 * Minute;
        var result = Calculate(from, to,
            cycles:
            [
                .. EveryFiveMinutes(from, switched, t => Cycle(t, AidAlgorithm.Loop, rate: 1.0)),
                .. EveryFiveMinutes(switched, to, t => Cycle(t, rate: 1.0, hasReason: false)),
            ],
            delivered: [new(from, switched, 1.0, BasalDeliveryOrigin.Algorithm), new(switched, to, 1.0, BasalDeliveryOrigin.Algorithm)]);

        result.MaxBasal!.CapKnown.Percent.Should().Be(0);
        result.Unknown.MaxBasal.Should().Be(LoopHealthUnknownReason.NoReasonRecorded, "most of the temp time was Trio's");
    }

    [Theory]
    [InlineData(TempBasalOriginEvidence.Reported, 100)]
    [InlineData(TempBasalOriginEvidence.Snapshot, 100)]
    [InlineData(TempBasalOriginEvidence.Uploader, 0)]
    [InlineData(TempBasalOriginEvidence.Uncorroborated, 0)]
    public void Activity_WithNoCycles_CreditsOnlyAnAlgorithmTempWhoseOriginIsMoreThanTheUploadersDefault(
        TempBasalOriginEvidence evidence, double closed)
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to,
            delivered: [new(from, to, 1.0, BasalDeliveryOrigin.Algorithm)],
            recorded: IntervalSet.Of([new MillsInterval(from, to)]),
            temps: [TempAt(from, evidence)]);

        result.Activity.ClosedLoop!.Percent.Should().Be(closed);
        result.Activity.Pump!.Percent.Should().Be(100);
    }

    [Fact]
    public void ATempWithNoCycleThatSetIt_HasNoKnownCap()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to,
            [Cycle(from, rate: 1.0)],
            [new(from, from + 30 * Minute, 1.0, BasalDeliveryOrigin.Algorithm), new(from + 30 * Minute, to, 2.4, BasalDeliveryOrigin.Algorithm)]);

        result.MaxBasal!.CapKnown.Percent.Should().Be(50);
        result.MaxBasal.AtCap.Percent.Should().Be(0);
    }

    [Fact]
    public void Loop_IsAtItsMaximumBasalRateSetting()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var maximum = StepTimeline<double>.LatestStartedWins([new Step<double>(Day0 - Hour, to, 2.0)]);
        List<ApsLoopCycle> cycles = [.. EveryFiveMinutes(from, to, t =>
            Cycle(t, AidAlgorithm.Loop, rate: t < from + 30 * Minute ? 1.98 : t < from + 45 * Minute ? 1.97 : 1.0))];
        var result = Calculate(from, to, cycles, TempsSetBy(cycles, to), loopMaximum: maximum);

        result.MaxBasal!.AtCap.Percent.Should().Be(50, "1.98 U/h is within half a pump step of 2.0 U/h and 1.97 is not");
        result.Limits.LoopMaximumBasalRate.Should().Be(2.0);
        result.AtMaxIob.Should().BeNull();
        result.SuggestedNotEnacted.Should().BeNull("Loop records no suggestion apart from what it enacts");
    }

    [Fact]
    public void WithNoCyclesInTheRange_GapsAreUnknownNotNone()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to, cycles: [Cycle(from - 30 * Minute)],
            delivered: [new(from, to, 1.0, BasalDeliveryOrigin.Algorithm)]);

        result.Cadence.LoopGaps.Should().BeNull();
        result.Cadence.UnrecordedDelivery.Should().BeNull();
        result.Unknown.Should().BeEquivalentTo(new LoopHealthUnknown
        {
            LoopGaps = LoopHealthUnknownReason.NoCyclesInRange,
            UnrecordedDelivery = LoopHealthUnknownReason.NoCyclesInRange,
            MaxBasal = LoopHealthUnknownReason.NoCyclesInRange,
            AtMaxIob = LoopHealthUnknownReason.NoCyclesInRange,
            SuggestedNotEnacted = LoopHealthUnknownReason.NoCyclesInRange,
            LoopMaximumBasalRate = LoopHealthUnknownReason.NoCyclesInRange,
            OrefMaxIob = LoopHealthUnknownReason.NoCyclesInRange,
        });
    }

    [Fact]
    public void AnOrefTempNoCycleSet_HasNoSetterFound()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to,
            cycles: EveryFiveMinutes(from, to, t => Cycle(t, enacted: false, suggestedChange: false)),
            delivered: [new(from, to, 2.4, BasalDeliveryOrigin.Algorithm)]);

        result.Unknown.MaxBasal.Should().Be(LoopHealthUnknownReason.SetterNotFound);
    }

    [Fact]
    public void ACycleMonthsBeforeTheRange_SaysNothingOfItsTemps()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to,
            cycles: [Cycle(from - 60L * 24 * Hour, rate: 1.0)],
            delivered: [new(from, to, 1.0, BasalDeliveryOrigin.Algorithm)]);

        result.Unknown.MaxBasal.Should().Be(LoopHealthUnknownReason.NoCyclesInRange);
    }

    [Fact]
    public void ForLoop_TheOrefFiguresAreNotRecordedAndAMissingSettingIsSaid()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to,
            cycles: EveryFiveMinutes(from, to, t => Cycle(t, AidAlgorithm.Loop, rate: 1.0)),
            delivered: [new(from, to, 1.0, BasalDeliveryOrigin.Algorithm)]);

        result.Cadence.LoopGaps!.Count.Should().Be(0);
        result.Unknown.Should().BeEquivalentTo(new LoopHealthUnknown
        {
            MaxBasal = LoopHealthUnknownReason.SettingNotUploaded,
            AtMaxIob = LoopHealthUnknownReason.NotRecordedByThisSystem,
            SuggestedNotEnacted = LoopHealthUnknownReason.NotRecordedByThisSystem,
            LoopMaximumBasalRate = LoopHealthUnknownReason.SettingNotUploaded,
            OrefMaxIob = LoopHealthUnknownReason.NotRecordedByThisSystem,
        });
    }

    [Fact]
    public void ForIaps_TheOrefFiguresAreReadAndUnknownOnlyWithoutAReason()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to,
            cycles: EveryFiveMinutes(from, to, t => Cycle(t, AidAlgorithm.IAPS, hasReason: false, suggestedChange: null)));

        result.Unknown.AtMaxIob.Should().Be(LoopHealthUnknownReason.NoReasonRecorded);
        result.Unknown.SuggestedNotEnacted.Should().Be(LoopHealthUnknownReason.NoReasonRecorded);
        result.Unknown.OrefMaxIob.Should().Be(LoopHealthUnknownReason.NoReasonRecorded);
        result.Unknown.LoopMaximumBasalRate.Should().Be(LoopHealthUnknownReason.NotRecordedByThisSystem);
        result.Unknown.MaxBasal.Should().Be(LoopHealthUnknownReason.NoAlgorithmTemps);
    }

    [Fact]
    public void AFigureMeasuredOnlyInMaskedTime_IsMaskedOut()
    {
        var from = Day0;
        var to = Day0 + 2 * Hour;
        var result = Calculate(from, to,
            cycles: EveryFiveMinutes(from, from + Hour, t => Cycle(t, rate: 1.0)),
            delivered: [new(from, from + Hour, 1.0, BasalDeliveryOrigin.Algorithm)],
            masks: new LoopAnalysisMasks { ExcludeIllness = true, ExcludeOpenLoop = false },
            sources: new LoopAnalysisMaskSources { Illness = [new MillsInterval(from, from + Hour + LoopHealthCalculator.LoopGapMs)] });

        result.AtMaxIob.Should().BeNull();
        result.Unknown.AtMaxIob.Should().Be(LoopHealthUnknownReason.MaskedOut);
        result.Unknown.MaxBasal.Should().Be(LoopHealthUnknownReason.MaskedOut);
    }

    [Fact]
    public void AGapAcrossMidnight_CountsOnce()
    {
        var from = Day0;
        var to = Day0 + 48 * Hour;
        var lastBefore = Day0 + 23 * Hour + 30 * Minute;
        var firstAfter = Day0 + 25 * Hour;
        var result = Calculate(from, to, cycles:
        [
            .. EveryFiveMinutes(from, lastBefore + Minute, t => Cycle(t)),
            .. EveryFiveMinutes(firstAfter, to, t => Cycle(t)),
        ]);

        var gaps = result.Cadence.LoopGaps!;
        gaps.Count.Should().Be(1);
        gaps.TotalMinutes.Should().Be(90);
        gaps.LongestMinutes.Should().Be(90);
        gaps.Gaps.Single().Date.Should().Be("2024-06-10");
        gaps.Gaps.Single().StartMinuteOfDay.Should().Be(23 * 60 + 30);
    }

    [Fact]
    public void AGapThatStraddlesTheRangeIsClippedToIt()
    {
        var from = Day0 + Hour;
        var to = Day0 + 3 * Hour;
        var result = Calculate(from, to, cycles: [Cycle(Day0), Cycle(Day0 + 2 * Hour), Cycle(Day0 + 2 * Hour + 5 * Minute)]);

        result.Cadence.LoopGaps!.Gaps.Single().Minutes.Should().Be(60);
        result.Cadence.LoopGaps.Count.Should().Be(1);
    }

    [Fact]
    public void ZeroDelivery_IsSplitByOrigin()
    {
        var from = Day0;
        var to = Day0 + 4 * Hour;
        var result = Calculate(from, to, delivered:
        [
            new(from, from + Hour, 0, BasalDeliveryOrigin.Algorithm),
            new(from + Hour, from + 2 * Hour, 0, BasalDeliveryOrigin.Manual),
            new(from + 2 * Hour, from + 3 * Hour, 0, BasalDeliveryOrigin.Suspended),
            new(from + 3 * Hour, to, 1.0, BasalDeliveryOrigin.Algorithm),
        ]);

        result.ZeroDelivery!.AlgorithmZeroTemp.Percent.Should().Be(25);
        result.ZeroDelivery.ManualZeroTemp.Percent.Should().Be(25);
        result.ZeroDelivery.Suspended.Percent.Should().Be(25);
        result.ZeroDelivery.AlgorithmZeroTemp.Hours[0].Percent.Should().Be(100);
        result.ZeroDelivery.Suspended.Hours[2].Percent.Should().Be(100);
        result.ZeroDelivery.Suspended.Hours[5].Percent.Should().BeNull("delivery is unknown there");
    }

    [Fact]
    public void AutomaticBoluses_AreCountedPerDayAndSized()
    {
        var from = Day0;
        var to = Day0 + 2 * 24 * Hour;
        var result = Calculate(from, to, doses:
        [
            new(Day0 + 3 * Hour, 0.2, true),
            new(Day0 + 3 * Hour + 10 * Minute, 0.4, true),
            new(Day0 + 27 * Hour, 0.6, true),
            new(Day0 + 8 * Hour, 3.0, false),
        ]);

        var boluses = result.AutomaticBoluses!;
        boluses.PerDay.Overall!.Mean.Should().Be(1.5);
        boluses.PerDay.Overall.N.Should().Be(2);
        boluses.Size.Overall!.P50.Should().Be(0.4);
        boluses.Size.Overall.N.Should().Be(3);
        boluses.PerDay.Hours[3].Value!.Mean.Should().Be(1.5);
        boluses.PerDay.Hours[8].Value!.Mean.Should().Be(0, "a manual bolus is not an automatic one");
    }

    [Fact]
    public void SuggestedNotEnacted_IsTheShareOfChangesAskedForAndNotMade()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to, cycles:
        [
            Cycle(from, enacted: true),
            Cycle(from + 5 * Minute, enacted: false),
            Cycle(from + 10 * Minute, enacted: true),
            Cycle(from + 15 * Minute, enacted: true),
            Cycle(from + 20 * Minute, enacted: false, suggestedChange: false),
        ]);

        result.SuggestedNotEnacted!.Percent.Should().Be(25);
        result.SuggestedNotEnacted.Hours[0].Percent.Should().Be(25);
    }

    [Fact]
    public void AtMaxIob_ReadsTheReasonAndThenIobNearTheReportedLimit()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to, cycles: EveryFiveMinutes(from, to, t =>
            t == from
                ? Cycle(t, limitReason: "Eventual BG 200 >= 100, max_iob 3,  insulinReq 0.4", iob: 2.6)
                : Cycle(t, iob: t < from + 30 * Minute ? 2.95 : 1.0)));

        result.AtMaxIob!.Percent.Should().Be(50);
        result.Limits.OrefMaxIob.Should().Be(3);
    }

    [Fact]
    public void SensitivityRatio_IsPooledByHour()
    {
        var from = Day0;
        var to = Day0 + 2 * Hour;
        var result = Calculate(from, to, cycles: EveryFiveMinutes(from, to, t =>
            Cycle(t, sensitivity: t < from + Hour ? 0.8 : 1.2)));

        result.SensitivityRatio!.Hours[0].Value!.P50.Should().Be(0.8);
        result.SensitivityRatio.Hours[1].Value!.P50.Should().Be(1.2);
        result.SensitivityRatio.Overall!.N.Should().Be(24);
        result.SensitivityRatio.Overall.Sparse.Should().BeTrue();
    }

    [Fact]
    public void UnrecordedDelivery_IsLoopTimeWithDeliveryUnknown()
    {
        var from = Day0;
        var to = Day0 + 6 * Hour;
        var result = Calculate(from, to,
            cycles: EveryFiveMinutes(from, to + 5 * Minute, t => Cycle(t))
                .Where(c => c.Mills <= from + 3 * Hour || c.Mills >= from + 3 * Hour + 30 * Minute),
            recorded: IntervalSet.Of(from, from + Hour),
            inferable: IntervalSet.Of(from + Hour, from + 2 * Hour));

        var gaps = result.Cadence.UnrecordedDelivery!;
        gaps.Gaps.Select(g => g.Minutes).Should().Equal([60.0, 150.0],
            "the schedule is inferred for the second hour, and the loop's own gap is not loop time");
        result.Cadence.LoopGaps!.Count.Should().Be(1);
    }

    [Fact]
    public void ALoopThatStopsBeforeTheDataDoes_LeavesAGapToTheEndOfTheData()
    {
        var from = Day0;
        var to = Day0 + 6 * Hour;
        var result = Calculate(from, to,
            cycles: EveryFiveMinutes(from, from + 2 * Hour + Minute, t => Cycle(t)),
            glucose: [new SensorGlucose { Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(from + 4 * Hour).UtcDateTime, Mgdl = 120 }]);

        result.Cadence.LoopGaps!.Gaps.Should().ContainSingle().Which.Minutes.Should().Be(120);
    }

    [Fact]
    public void Activity_CountsAnOrefLoopHoldingItsTempsAsClosed()
    {
        // oref enacts a temp every half hour and holds it, or needs none, in between.
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to, cycles: EveryFiveMinutes(from, to, t => (t - from) % (30 * Minute) == 0
            ? Cycle(t, rate: 1.0)
            : Cycle(t, enacted: false, suggestedChange: false)));

        result.Activity.ClosedLoop!.Percent.Should().Be(100);
    }

    [Fact]
    public void Activity_CountsAnEnactedOrefCycleWithNoStoredSuggestionAsClosed()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to, cycles: EveryFiveMinutes(from, to, t => Cycle(t, rate: 1.0, suggestedChange: null)));

        result.Activity.ClosedLoop!.Percent.Should().Be(100);
    }

    [Fact]
    public void Activity_CountsAnOrefChangeItDidNotEnactAsOpenUntilItEnactsOne()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to, cycles: EveryFiveMinutes(from, to, t => (t - from) switch
        {
            0 => Cycle(t, rate: 1.0),
            30 * Minute => Cycle(t, enacted: false),
            _ => Cycle(t, enacted: false, suggestedChange: false),
        }));

        result.Activity.ClosedLoop!.Percent.Should().Be(50);
    }

    [Fact]
    public void Activity_CreditsFiveMinutesPerCycle()
    {
        var from = Day0;
        var to = Day0 + 2 * Hour;
        var result = Calculate(from, to, cycles: EveryFiveMinutes(from, from + Hour, t =>
            Cycle(t, AidAlgorithm.Loop, enacted: (t - from) / (5 * Minute) % 2 == 0)));

        result.Activity.Pump!.Hours[0].Percent.Should().Be(100);
        result.Activity.ClosedLoop!.Hours[0].Percent.Should().Be(50);
        result.Activity.ClosedLoop.Hours[1].Percent.Should().Be(0);
        result.Activity.ClosedLoop.Percent.Should().Be(25);
    }

    [Fact]
    public void Activity_CountsCyclesFasterThanFiveMinutesOnce()
    {
        // Every two minutes for the first half hour: 33 minutes covered, not fifteen credits of five.
        var from = Day0;
        var to = Day0 + Hour;
        var cycles = Enumerable.Range(0, 15).Select(i => Cycle(from + i * 2 * Minute));
        var result = Calculate(from, to, cycles);

        result.Activity.Pump!.Percent.Should().Be(55);
        result.Activity.ClosedLoop!.Percent.Should().Be(55);
    }

    [Fact]
    public void ACycleTwoUploadersPosted_IsEnactedWhereEitherSawItEnacted()
    {
        var from = Day0;
        var to = Day0 + Hour;
        var result = Calculate(from, to, cycles: [Cycle(from, enacted: false), Cycle(from, enacted: true)]);

        result.Activity.ClosedLoop!.Percent.Should().Be(SlotSummary.Round(500.0 / 60));
    }

    [Fact]
    public void Days_ListsEveryDayOfTheRange_EvenOneTheMasksEmptied()
    {
        var from = Day0;
        var to = Day0 + 2 * 24 * Hour;
        var result = Calculate(from, to,
            cycles: EveryFiveMinutes(from, to, t => Cycle(t)),
            masks: new LoopAnalysisMasks { ExcludeIllness = true, ExcludeOpenLoop = false },
            sources: new LoopAnalysisMaskSources { Illness = [new MillsInterval(from, from + 24 * Hour)] });

        result.Days.Select(d => d.Date).Should().Equal("2024-06-10", "2024-06-11");
        result.Activity.Pump!.Daily!.N.Should().Be(1, "only the second day was analysed");
    }

    [Fact]
    public void DayDates_AreGregorian_WhateverTheProcessCulture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");

            Calculate(Day0, Day0 + 24 * Hour, cycles: EveryFiveMinutes(Day0, Day0 + 24 * Hour, t => Cycle(t)))
                .Days.Select(d => d.Date).Should().Equal("2024-06-10");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void FallBackDay_WeighsTheRepeatedHourByItsLength()
    {
        // 2024-04-07 in Sydney is 25 hours long: 03:00 AEDT falls back to 02:00 AEST.
        var from = new DateTimeOffset(2024, 4, 6, 13, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var to = new DateTimeOffset(2024, 4, 7, 14, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var secondTwoAm = new DateTimeOffset(2024, 4, 6, 16, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var result = Calculate(from, to,
            cycles: EveryFiveMinutes(from, to, t => Cycle(t)),
            doses: [new(secondTwoAm + Minute, 0.2, true)]);

        result.Days.Should().ContainSingle();
        result.Activity.Pump!.Hours[2].Percent.Should().Be(100);
        result.Activity.Pump.Hours[2].Daily!.N.Should().Be(1);
        result.AutomaticBoluses!.PerDay.Hours[2].Value!.Mean.Should().Be(0.5, "one bolus over the two hours 02:00 ran for");
        result.AutomaticBoluses.PerDay.Overall!.Mean.Should().Be(1);
    }

    [Fact]
    public void AMaskedHour_IsLeftOutOfEveryFigure()
    {
        var from = Day0;
        var to = Day0 + 2 * Hour;
        var result = Calculate(from, to,
            cycles: EveryFiveMinutes(from, to, t => Cycle(t, sensitivity: 1.0)),
            masks: new LoopAnalysisMasks { ExcludeIllness = true, ExcludeOpenLoop = false },
            sources: new LoopAnalysisMaskSources { Illness = [new MillsInterval(from, from + Hour)] });

        result.Activity.Pump!.Hours[0].Percent.Should().BeNull();
        result.Activity.Pump.Hours[1].Percent.Should().Be(100);
        result.SensitivityRatio!.Overall!.N.Should().Be(12);
    }
}
