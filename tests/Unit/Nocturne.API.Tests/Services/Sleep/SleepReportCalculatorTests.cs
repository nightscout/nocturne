using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Sleep.Report;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Sleep;

[Trait("Category", "Unit")]
public class SleepReportCalculatorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static readonly DateTime _sessionStart = new(2026, 5, 16, 23, 0, 0, DateTimeKind.Utc);

    private static readonly GlycemicThresholds _thresholds = new();

    private static SleepSession MakeSession(DateTime? start = null)
    {
        var s = start ?? _sessionStart;
        return new SleepSession { StartTime = s, EndTime = s.AddHours(8) };
    }

    private static SensorGlucose MakeGlucose(DateTime timestamp, double mgdl) =>
        new() { Timestamp = timestamp, Mgdl = mgdl };

    // ── Stage Breakdown ───────────────────────────────────────────────────

    [Fact]
    public void ComputeStageBreakdown_UsesSummaryFields_WhenPopulated()
    {
        var session = new SleepSession
        {
            StartTime    = DateTime.UtcNow,
            EndTime      = DateTime.UtcNow.AddHours(8),
            DeepSleepMs  = 90  * 60 * 1000L,
            RemSleepMs   = 100 * 60 * 1000L,
            LightSleepMs = 220 * 60 * 1000L,
            TotalAwakeMs = 30  * 60 * 1000L,
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeStageBreakdown(session);

        result.DeepMinutes.Should().Be(90);
        result.RemMinutes.Should().Be(100);
        result.LightMinutes.Should().Be(220);
        result.AwakeMinutes.Should().Be(30);
        result.TotalMinutes.Should().Be(440);
        result.DeepPct.Should().BeApproximately(90.0 / 440 * 100, 0.01);
    }

    [Fact]
    public void ComputeStageBreakdown_DerivesUnspecified_WhenTotalSleepExceedsDifferentiatedStages()
    {
        var session = new SleepSession
        {
            StartTime    = DateTime.UtcNow,
            EndTime      = DateTime.UtcNow.AddHours(8),
            DeepSleepMs  = 90  * 60 * 1000L,
            RemSleepMs   = 100 * 60 * 1000L,
            LightSleepMs = 200 * 60 * 1000L,
            TotalAwakeMs = 30  * 60 * 1000L,
            TotalSleepMs = 440 * 60 * 1000L, // 50 minutes more than deep + rem + light
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeStageBreakdown(session);

        result.UnspecifiedMinutes.Should().Be(50);
        result.TotalMinutes.Should().Be(470); // 90 + 100 + 200 + 30 + 50
        result.UnspecifiedPct.Should().BeApproximately(50.0 / 470 * 100, 0.01);
    }

    [Fact]
    public void ComputeStageBreakdown_UnspecifiedIsZero_WhenTotalSleepWithinDifferentiatedStages()
    {
        var session = new SleepSession
        {
            StartTime    = DateTime.UtcNow,
            EndTime      = DateTime.UtcNow.AddHours(8),
            DeepSleepMs  = 90  * 60 * 1000L,
            RemSleepMs   = 100 * 60 * 1000L,
            LightSleepMs = 220 * 60 * 1000L,
            TotalAwakeMs = 30  * 60 * 1000L,
            TotalSleepMs = 400 * 60 * 1000L, // less than deep + rem + light — no negative remainder
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeStageBreakdown(session);

        result.UnspecifiedMinutes.Should().Be(0);
        result.TotalMinutes.Should().Be(440);
        result.UnspecifiedPct.Should().Be(0);
    }

    [Fact]
    public void ComputeStageBreakdown_BucketsAsleepAsUnspecified_NotLight()
    {
        var now = new DateTime(2026, 5, 16, 23, 0, 0, DateTimeKind.Utc);
        var session = new SleepSession
        {
            StartTime = now,
            EndTime   = now.AddHours(8),
            Stages =
            [
                new SleepStageInterval { StartTime = now, EndTime = now.AddMinutes(420), Stage = SleepStageType.Asleep },
            ],
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeStageBreakdown(session);

        result.UnspecifiedMinutes.Should().Be(420);
        result.LightMinutes.Should().Be(0);
        result.DeepMinutes.Should().Be(0);
        result.RemMinutes.Should().Be(0);
        result.TotalMinutes.Should().Be(420);
        result.UnspecifiedPct.Should().BeApproximately(100, 0.01);
    }

    [Fact]
    public void ComputeStageBreakdown_DerivesFromStages_WhenSummaryFieldsNull()
    {
        var now = new DateTime(2026, 5, 16, 23, 0, 0, DateTimeKind.Utc);
        var session = new SleepSession
        {
            StartTime    = now,
            EndTime      = now.AddHours(8),
            DeepSleepMs  = null,
            RemSleepMs   = null,
            LightSleepMs = null,
            TotalAwakeMs = null,
            Stages =
            [
                new SleepStageInterval { StartTime = now,                  EndTime = now.AddMinutes(30),  Stage = SleepStageType.Light },
                new SleepStageInterval { StartTime = now.AddMinutes(30),   EndTime = now.AddMinutes(120), Stage = SleepStageType.Deep  },
                new SleepStageInterval { StartTime = now.AddMinutes(120),  EndTime = now.AddMinutes(180), Stage = SleepStageType.Rem   },
            ],
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeStageBreakdown(session);

        result.LightMinutes.Should().Be(30);
        result.DeepMinutes.Should().Be(90);
        result.RemMinutes.Should().Be(60);
        result.AwakeMinutes.Should().Be(0);
        result.TotalMinutes.Should().Be(180);
    }

    // ── Overnight TIR ─────────────────────────────────────────────────────

    [Fact]
    public void ComputeOvernightTir_ReturnsNull_WhenNoGlucoseData()
    {
        var session = MakeSession();
        var result = API.Services.Sleep.SleepReportCalculator.ComputeOvernightTir(session, [], _thresholds);
        result.Should().BeNull();
    }

    [Fact]
    public void ComputeOvernightTir_ComputesRanges_UsingClinicalThresholds()
    {
        var session = MakeSession();
        var readings = new[]
        {
            MakeGlucose(session.StartTime.AddMinutes(10), 50),   // very low
            MakeGlucose(session.StartTime.AddMinutes(20), 65),   // low
            MakeGlucose(session.StartTime.AddMinutes(30), 120),  // in range
            MakeGlucose(session.StartTime.AddMinutes(40), 120),  // in range
            MakeGlucose(session.StartTime.AddMinutes(50), 200),  // high
            MakeGlucose(session.StartTime.AddMinutes(60), 260),  // very high
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeOvernightTir(session, readings, _thresholds);

        result.Should().NotBeNull();
        result!.VeryLowPct.Should().BeApproximately(100.0 / 6, 0.01);
        result.LowPct.Should().BeApproximately(100.0 / 6, 0.01);
        result.InRangePct.Should().BeApproximately(200.0 / 6, 0.01);
        result.HighPct.Should().BeApproximately(100.0 / 6, 0.01);
        result.VeryHighPct.Should().BeApproximately(100.0 / 6, 0.01);
        result.MeanBg.Should().Be((int)Math.Round((50 + 65 + 120 + 120 + 200 + 260) / 6.0));
    }

    // ── Hypo Events ───────────────────────────────────────────────────────

    [Fact]
    public void ComputeHypoEvents_ReturnsEmpty_WhenNoLowReadings()
    {
        var session = MakeSession();
        var glucose = new[] { MakeGlucose(session.StartTime.AddMinutes(10), 85) };
        var result = API.Services.Sleep.SleepReportCalculator.ComputeHypoEvents(session, glucose, [], _thresholds);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ComputeHypoEvents_DetectsContiguousRun_AndTagsSeverity()
    {
        var session = MakeSession();
        var t0 = session.StartTime.AddMinutes(60);
        var glucose = new[]
        {
            MakeGlucose(t0,                65),  // low
            MakeGlucose(t0.AddMinutes(5),  62),  // low (nadir)
            MakeGlucose(t0.AddMinutes(10), 68),  // still low
            MakeGlucose(t0.AddMinutes(15), 75),  // recovered
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeHypoEvents(session, glucose, [], _thresholds);

        result.Should().HaveCount(1);
        result[0].LowestBg.Should().Be(62);
        result[0].Severity.Should().Be(SleepHypoSeverity.Low);
        result[0].DurationMinutes.Should().Be(10);
        result[0].Stage.Should().Be(SleepStageType.Unknown);
    }

    [Fact]
    public void ComputeHypoEvents_MarksVeryLow_WhenBelowFiftyFour()
    {
        var session = MakeSession();
        var t0 = session.StartTime.AddMinutes(120);
        var glucose = new[]
        {
            MakeGlucose(t0,               50),
            MakeGlucose(t0.AddMinutes(5), 71),
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeHypoEvents(session, glucose, [], _thresholds);

        result[0].Severity.Should().Be(SleepHypoSeverity.VeryLow);
    }

    [Fact]
    public void ComputeHypoEvents_TagsStage_FromStageIntervals()
    {
        var session = MakeSession();
        var t0 = session.StartTime.AddMinutes(90);
        var glucose = new[]
        {
            MakeGlucose(t0,               65),
            MakeGlucose(t0.AddMinutes(5), 71),
        };
        var stages = new[]
        {
            new SleepStageInterval
            {
                StartTime = session.StartTime.AddMinutes(60),
                EndTime   = session.StartTime.AddMinutes(120),
                Stage     = SleepStageType.Deep,
            },
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeHypoEvents(session, glucose, stages, _thresholds);

        result[0].Stage.Should().Be(SleepStageType.Deep);
    }

    // ── Dawn Phenomenon ───────────────────────────────────────────────────

    [Fact]
    public void ComputeDawnPhenomenon_ReturnsNull_WhenFewerThanFourReadings()
    {
        var session = MakeSession();
        var glucose = new[]
        {
            MakeGlucose(session.EndTime.AddMinutes(-90), 100),
            MakeGlucose(session.EndTime.AddMinutes(-60), 110),
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeDawnPhenomenon(session, glucose);

        result.Should().BeNull();
    }

    [Fact]
    public void ComputeDawnPhenomenon_ComputesDeltaAndRate_ForPositiveRise()
    {
        var session = MakeSession();
        // Glucose rises across the window: first=98, last=140
        var glucose = new[]
        {
            MakeGlucose(session.EndTime.AddMinutes(-115), 98),   // first (trough)
            MakeGlucose(session.EndTime.AddMinutes(-90),  105),
            MakeGlucose(session.EndTime.AddMinutes(-60),  115),
            MakeGlucose(session.EndTime.AddMinutes(-10),  140),  // last (peak)
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeDawnPhenomenon(session, glucose);

        result.Should().NotBeNull();
        result!.TroughBg.Should().Be(98);
        result.PeakBg.Should().Be(140);
        result.DeltaBg.Should().Be(42); // last - first = 140 - 98
        result.RateOfClimbPerHour.Should().BePositive();
    }

    [Fact]
    public void ComputeDawnPhenomenon_ReportsNegativeDelta_WhenGlucoseDeclining()
    {
        var session = MakeSession();
        // Glucose declines across the window: first=145, last=98
        var glucose = new[]
        {
            MakeGlucose(session.EndTime.AddMinutes(-115), 145), // first (peak)
            MakeGlucose(session.EndTime.AddMinutes(-90),  130),
            MakeGlucose(session.EndTime.AddMinutes(-45),  110),
            MakeGlucose(session.EndTime.AddMinutes(-10),  98),  // last (trough)
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeDawnPhenomenon(session, glucose);

        result.Should().NotBeNull();
        result!.DeltaBg.Should().BeNegative();
        result.RateOfClimbPerHour.Should().BeNegative();
    }

    // ── Wake Events ───────────────────────────────────────────────────────

    [Fact]
    public void ComputeWakeEvents_ExtractsAwakeIntervals()
    {
        var session = MakeSession();
        var stages = new[]
        {
            new SleepStageInterval { StartTime = session.StartTime,                EndTime = session.StartTime.AddMinutes(20),  Stage = SleepStageType.Awake },
            new SleepStageInterval { StartTime = session.StartTime.AddMinutes(20), EndTime = session.StartTime.AddMinutes(100), Stage = SleepStageType.Deep  },
            new SleepStageInterval { StartTime = session.StartTime.AddMinutes(100),EndTime = session.StartTime.AddMinutes(110), Stage = SleepStageType.Awake },
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeWakeEvents(session, stages, []);

        result.Should().HaveCount(2);
        result[0].DurationMinutes.Should().Be(20);
        result[0].IsPreSleep.Should().BeTrue();
        result[1].DurationMinutes.Should().Be(10);
        result[1].IsPreSleep.Should().BeFalse();
    }

    [Fact]
    public void ComputeWakeEvents_AttachesNearestGlucose_WhenWithinFifteenMinutes()
    {
        var session = MakeSession();
        var wakeStart = session.StartTime.AddMinutes(100);
        var stages = new[]
        {
            new SleepStageInterval { StartTime = wakeStart, EndTime = wakeStart.AddMinutes(10), Stage = SleepStageType.Awake },
        };
        var glucose = new[] { MakeGlucose(wakeStart.AddMinutes(3), 88) };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeWakeEvents(session, stages, glucose);

        result[0].BgAtStart.Should().Be(88);
    }

    [Fact]
    public void ComputeWakeEvents_NullsBg_WhenNearestGlucoseExceedsFifteenMinutes()
    {
        var session = MakeSession();
        var wakeStart = session.StartTime.AddMinutes(100);
        var stages = new[]
        {
            new SleepStageInterval { StartTime = wakeStart, EndTime = wakeStart.AddMinutes(10), Stage = SleepStageType.Awake },
        };
        var glucose = new[] { MakeGlucose(wakeStart.AddMinutes(20), 88) };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeWakeEvents(session, stages, glucose);

        result[0].BgAtStart.Should().BeNull();
    }

    // ── Score Resolution ──────────────────────────────────────────────────

    [Fact]
    public void ResolveScore_UsesDeviceScore_WhenPresent()
    {
        var session = new SleepSession { SleepScore = 82 };
        var (score, source) = API.Services.Sleep.SleepReportCalculator.ResolveScore(session, 0, new SleepStageBreakdown());
        score.Should().Be(82);
        source.Should().Be(SleepScoreSource.Device);
    }

    [Fact]
    public void ResolveScore_ComputesFallback_WhenScoreNull()
    {
        var session = new SleepSession { SleepScore = null };
        var breakdown = new SleepStageBreakdown
        {
            DeepMinutes = 90, RemMinutes = 100, LightMinutes = 230, AwakeMinutes = 20, TotalMinutes = 440,
        };
        var (score, source) = API.Services.Sleep.SleepReportCalculator.ResolveScore(session, hypoCount: 0, breakdown);
        score.Should().NotBeNull();
        score!.Value.Should().BeInRange(0, 100);
        source.Should().Be(SleepScoreSource.Computed);
    }

    [Fact]
    public void ResolveScore_ReturnsNull_WhenNoStageData()
    {
        var session = new SleepSession { SleepScore = null };
        var breakdown = new SleepStageBreakdown { TotalMinutes = 0 };
        var (score, source) = API.Services.Sleep.SleepReportCalculator.ResolveScore(session, hypoCount: 0, breakdown);
        score.Should().BeNull();
        source.Should().BeNull();
    }

    [Fact]
    public void ResolveScore_ReturnsNull_WhenOnlyUnspecifiedMinutes()
    {
        var session = new SleepSession { SleepScore = null };
        var breakdown = new SleepStageBreakdown { UnspecifiedMinutes = 420, TotalMinutes = 420 };
        var (score, source) = API.Services.Sleep.SleepReportCalculator.ResolveScore(session, hypoCount: 0, breakdown);
        score.Should().BeNull();
        source.Should().BeNull();
    }

    [Fact]
    public void ResolveScore_IncludesUnspecifiedInEfficiency_WhenDifferentiatedMinutesExist()
    {
        var session = new SleepSession { SleepScore = null };
        var breakdown = new SleepStageBreakdown
        {
            DeepMinutes = 60, RemMinutes = 60, LightMinutes = 120, UnspecifiedMinutes = 60, AwakeMinutes = 0, TotalMinutes = 300,
        };

        var (score, source) = API.Services.Sleep.SleepReportCalculator.ResolveScore(session, hypoCount: 0, breakdown);

        // efficiency = (60+60+120+60)/300 = 1.0, deepFrac = 0.2, remFrac = 0.2, disruption = 0
        // raw = 40 + 1.0*25 + 0.2*90 + 0.2*35 = 90
        score.Should().Be(90);
        source.Should().Be(SleepScoreSource.Computed);
    }

    // ── Night Summary ─────────────────────────────────────────────────────

    [Fact]
    public void ComputeNightSummary_PopulatesFields()
    {
        var sessionId = Guid.NewGuid();
        var session = MakeSession();
        session.Id           = sessionId.ToString();
        session.DeepSleepMs  = 90  * 60_000L;
        session.RemSleepMs   = 100 * 60_000L;
        session.LightSleepMs = 220 * 60_000L;
        session.TotalAwakeMs = 30  * 60_000L;
        session.SleepScore   = 75;

        var result = API.Services.Sleep.SleepReportCalculator.ComputeNightSummary(session, [], _thresholds);

        result.SessionId.Should().Be(sessionId);
        result.SleepScore.Should().Be(75);
        result.ScoreSource.Should().Be(SleepScoreSource.Device);
        result.DeepMinutes.Should().Be(90);
        result.HypoCount.Should().Be(0);
        result.LowestBg.Should().BeNull();
    }

    [Fact]
    public void ComputeNightSummary_ComputesScore_WhenDeviceScoreAbsent()
    {
        var session = MakeSession();
        session.Id           = Guid.NewGuid().ToString();
        session.DeepSleepMs  = 90  * 60_000L;
        session.RemSleepMs   = 100 * 60_000L;
        session.LightSleepMs = 220 * 60_000L;
        session.TotalAwakeMs = 30  * 60_000L;
        session.SleepScore   = null;

        var result = API.Services.Sleep.SleepReportCalculator.ComputeNightSummary(session, [], _thresholds);

        result.ScoreSource.Should().Be(SleepScoreSource.Computed);
        result.SleepScore.Should().NotBeNull();
        result.SleepScore!.Value.Should().BeInRange(1, 100); // computed from real stage data
    }

    [Fact]
    public void ComputeNightSummary_NullsScoreAndSource_WhenNoStageData()
    {
        var session = new SleepSession
        {
            Id         = Guid.NewGuid().ToString(),
            StartTime  = _sessionStart,
            EndTime    = _sessionStart.AddHours(8),
            SleepScore = null,
            // All stage summary fields null, no Stages collection → TotalMinutes == 0
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeNightSummary(session, [], _thresholds);

        result.SleepScore.Should().BeNull();
        result.ScoreSource.Should().BeNull();
    }

    [Fact]
    public void ComputeNightSummary_CountsUnspecifiedAsSleep_ButNullsScore_WhenOnlyAsleepIntervals()
    {
        var session = MakeSession();
        session.Id = Guid.NewGuid().ToString();
        session.Stages =
        [
            new SleepStageInterval { StartTime = session.StartTime, EndTime = session.StartTime.AddMinutes(420), Stage = SleepStageType.Asleep },
        ];

        var result = API.Services.Sleep.SleepReportCalculator.ComputeNightSummary(session, [], _thresholds);

        result.UnspecifiedMinutes.Should().Be(420);
        result.SleepMinutes.Should().Be(420);
        result.LightMinutes.Should().Be(0);
        result.SleepScore.Should().BeNull();
        result.ScoreSource.Should().BeNull();
    }

    [Fact]
    public void ComputeNightSummary_PopulatesLowestBg_FromAllReadings()
    {
        var session = MakeSession();
        session.Id = Guid.NewGuid().ToString();
        var readings = new[]
        {
            MakeGlucose(session.StartTime.AddMinutes(30), 90),
            MakeGlucose(session.StartTime.AddMinutes(60), 110),
            MakeGlucose(session.StartTime.AddMinutes(90), 75),  // lowest
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeNightSummary(session, readings, _thresholds);

        // LowestBg should be the session minimum, not constrained to hypo events
        result.LowestBg.Should().Be(75);
    }

    // ── Deduplication ─────────────────────────────────────────────────────

    [Fact]
    public void DeduplicateToOnePerNight_PicksLongestSession()
    {
        var night   = new DateTime(2026, 5, 16, 23, 0, 0, DateTimeKind.Utc);
        var shorter = new SleepSession { StartTime = night, EndTime = night.AddHours(6), TotalSleepMs = 6 * 3_600_000L, Source = SleepSource.Samsung };
        var longer  = new SleepSession { StartTime = night, EndTime = night.AddHours(8), TotalSleepMs = 8 * 3_600_000L, Source = SleepSource.Oura };

        var result = API.Services.Sleep.SleepReportCalculator.DeduplicateToOnePerNight([shorter, longer]);

        result.Should().HaveCount(1);
        result[0].Source.Should().Be(SleepSource.Oura);
    }

    [Fact]
    public void DeduplicateToOnePerNight_TieBreaksBySourcePriority()
    {
        var night   = new DateTime(2026, 5, 16, 23, 0, 0, DateTimeKind.Utc);
        var oura    = new SleepSession { StartTime = night, EndTime = night.AddHours(8), TotalSleepMs = 8 * 3_600_000L, Source = SleepSource.Oura };
        var samsung = new SleepSession { StartTime = night, EndTime = night.AddHours(8), TotalSleepMs = 8 * 3_600_000L, Source = SleepSource.Samsung };

        var result = API.Services.Sleep.SleepReportCalculator.DeduplicateToOnePerNight([samsung, oura]);

        result[0].Source.Should().Be(SleepSource.Oura);
    }

    // ── Trends Summary ────────────────────────────────────────────────────

    [Fact]
    public void ComputeTrendsSummary_ComputesMeans()
    {
        var nights = new[]
        {
            new SleepNightSummary { SleepScore = 70, OvernightTirPct = 80, DeepMinutes = 90, SleepMinutes = 440, HypoCount = 0 },
            new SleepNightSummary { SleepScore = 80, OvernightTirPct = 90, DeepMinutes = 110, SleepMinutes = 460, HypoCount = 1 },
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeTrendsSummary(nights, daysInRange: 30);

        result.NightCount.Should().Be(2);
        result.MeanScore.Should().BeApproximately(75, 0.01);
        result.MeanTirPct.Should().BeApproximately(85, 0.01);
        result.TotalHypoCount.Should().Be(1);
        result.NightsWithHypoPct.Should().BeApproximately(50, 0.01);
    }

    [Fact]
    public void ComputeTrendsSummary_Computes7dVsPrior7dDeltas()
    {
        // 14 nights: first 7 score=60, last 7 score=80 → delta = +20
        var nights = Enumerable.Range(0, 14).Select(i => new SleepNightSummary
        {
            SleepScore      = i < 7 ? 60 : 80,
            OvernightTirPct = 85,
            DeepMinutes     = 90,
            SleepMinutes    = 440,
            HypoCount       = 0,
        }).ToArray();

        var result = API.Services.Sleep.SleepReportCalculator.ComputeTrendsSummary(nights, daysInRange: 14);

        result.Last7dVsPrior7d.ScoreDelta.Should().BeApproximately(20, 0.01);
    }

    [Fact]
    public void ComputeTrendsSummary_Computes7dVsPrior7dDawnRiseDelta()
    {
        // 14 nights: first 7 dawnRise=10, last 7 dawnRise=25 → delta = +15
        var nights = Enumerable.Range(0, 14).Select(i => new SleepNightSummary
        {
            DawnRiseDeltaMg = i < 7 ? 10 : 25,
            SleepMinutes    = 440,
            HypoCount       = 0,
        }).ToArray();

        var result = API.Services.Sleep.SleepReportCalculator.ComputeTrendsSummary(nights, daysInRange: 14);

        result.Last7dVsPrior7d.DawnRiseDelta.Should().BeApproximately(15, 0.01);
    }

    [Fact]
    public void ComputeTrendsSummary_ComputesCoverage_FromDaysInRange()
    {
        var nights = Enumerable.Range(0, 3).Select(_ => new SleepNightSummary
        {
            SleepMinutes = 440,
            HypoCount    = 0,
        }).ToArray();

        var result = API.Services.Sleep.SleepReportCalculator.ComputeTrendsSummary(nights, daysInRange: 10);

        result.DaysInRange.Should().Be(10);
        result.CoveragePct.Should().BeApproximately(30, 0.01);
    }

    [Fact]
    public void ComputeTrendsSummary_ClampsCoverage_WhenNightsExceedDays()
    {
        var nights = Enumerable.Range(0, 5).Select(_ => new SleepNightSummary
        {
            SleepMinutes = 440,
            HypoCount    = 0,
        }).ToArray();

        var result = API.Services.Sleep.SleepReportCalculator.ComputeTrendsSummary(nights, daysInRange: 3);

        result.CoveragePct.Should().Be(100);
    }

    [Fact]
    public void ComputeTrendsSummary_SetsDaysInRange_WhenNoNights()
    {
        var result = API.Services.Sleep.SleepReportCalculator.ComputeTrendsSummary([], daysInRange: 30);

        result.NightCount.Should().Be(0);
        result.DaysInRange.Should().Be(30);
        result.CoveragePct.Should().Be(0);
    }

    [Fact]
    public void ComputeTrendsSummary_ZeroCoverage_WhenDaysInRangeIsZero()
    {
        var result = API.Services.Sleep.SleepReportCalculator.ComputeTrendsSummary([], daysInRange: 0);

        result.DaysInRange.Should().Be(0);
        result.CoveragePct.Should().Be(0);
    }

    [Fact]
    public void ComputeNightSummary_DisplayDate_UsesSessionTimezone_NotUtc()
    {
        // 22:00 Sydney (UTC+11 in Jan) = 11:00 UTC. The noon rule in Sydney lands on
        // Jan 15; the timezone-naive UTC rule (11:00 − 12h) would land on Jan 14.
        var session = new SleepSession
        {
            Id           = Guid.NewGuid().ToString(),
            StartTime    = new DateTime(2026, 1, 15, 11, 0, 0, DateTimeKind.Utc),
            EndTime      = new DateTime(2026, 1, 15, 19, 0, 0, DateTimeKind.Utc),
            Timezone     = "Australia/Sydney",
            TotalSleepMs = 480L * 60_000,
            Source       = SleepSource.Oura,
        };

        var summary = API.Services.Sleep.SleepReportCalculator.ComputeNightSummary(session, [], _thresholds);

        summary.DisplayDate.Should().Be("2026-01-15");
    }

    [Fact]
    public void ComputeNightSummary_DisplayDate_ResolvesMisCasedTimezoneId()
    {
        // ETC/GMT-11 is UTC+11, the same offset as Sydney in January, mis-cased the
        // way connectors store it. Linux looks up /usr/share/zoneinfo/<id> literally,
        // so an exact lookup misses (and throws) where the shared resolver recovers
        // the intended zone; falling back to UTC would key this night to Jan 14.
        var session = new SleepSession
        {
            Id           = Guid.NewGuid().ToString(),
            StartTime    = new DateTime(2026, 1, 15, 11, 0, 0, DateTimeKind.Utc),
            EndTime      = new DateTime(2026, 1, 15, 19, 0, 0, DateTimeKind.Utc),
            Timezone     = "ETC/GMT-11",
            TotalSleepMs = 480L * 60_000,
            Source       = SleepSource.Oura,
        };

        var summary = API.Services.Sleep.SleepReportCalculator.ComputeNightSummary(session, [], _thresholds);

        summary.DisplayDate.Should().Be("2026-01-15");
    }

    // ── Weekly Summaries ──────────────────────────────────────────────────

    // 2026-05-04, -11, -18 are Mondays.
    private static SleepNightSummary MakeNight(DateTime inBedAt, int? score = null, double? tirPct = null, int sleepMinutes = 440, int hypoCount = 0) =>
        new()
        {
            SessionId       = Guid.NewGuid(),
            InBedAt         = inBedAt,
            WakeAt          = inBedAt.AddHours(8),
            SleepMinutes    = sleepMinutes,
            SleepScore      = score,
            OvernightTirPct = tirPct,
            HypoCount       = hypoCount,
        };

    [Fact]
    public void ComputeWeekSummaries_BucketsNightsIntoMondayWeeks_OldestFirst()
    {
        var nights = new[]
        {
            MakeNight(new DateTime(2026, 5, 12, 23, 0, 0, DateTimeKind.Utc), score: 70, tirPct: 80, sleepMinutes: 400, hypoCount: 1),
            MakeNight(new DateTime(2026, 5, 13, 23, 0, 0, DateTimeKind.Utc), score: 80, tirPct: 90, sleepMinutes: 480, hypoCount: 0),
            MakeNight(new DateTime(2026, 5, 19, 23, 0, 0, DateTimeKind.Utc), score: 60, sleepMinutes: 420),
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeWeekSummaries(
            nights, from: new DateTime(2026, 5, 11), to: new DateTime(2026, 5, 24));

        result.Should().HaveCount(2);

        result[0].WeekStart.Should().Be(new DateTime(2026, 5, 11));
        result[0].WeekEnd.Should().Be(new DateTime(2026, 5, 17));
        result[0].NightCount.Should().Be(2);
        result[0].DaysInRange.Should().Be(7);
        result[0].MeanAsleepMinutes.Should().BeApproximately(440, 0.01);
        result[0].MeanScore.Should().BeApproximately(75, 0.01);
        result[0].MeanTirPct.Should().BeApproximately(85, 0.01);
        result[0].TotalHypoCount.Should().Be(1);
        result[0].SessionIds.Should().Equal(nights[0].SessionId, nights[1].SessionId);

        result[1].WeekStart.Should().Be(new DateTime(2026, 5, 18));
        result[1].NightCount.Should().Be(1);
        result[1].MeanScore.Should().Be(60);
        result[1].MeanTirPct.Should().BeNull("no night in the week has CGM data");
    }

    [Fact]
    public void ComputeWeekSummaries_IncludesGapWeeks_WithZeroNights()
    {
        var nights = new[]
        {
            MakeNight(new DateTime(2026, 5, 5, 23, 0, 0, DateTimeKind.Utc)),
            MakeNight(new DateTime(2026, 5, 19, 23, 0, 0, DateTimeKind.Utc)),
        };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeWeekSummaries(
            nights, from: new DateTime(2026, 5, 4), to: new DateTime(2026, 5, 24));

        result.Should().HaveCount(3);
        result[1].WeekStart.Should().Be(new DateTime(2026, 5, 11));
        result[1].NightCount.Should().Be(0);
        result[1].MeanScore.Should().BeNull();
        result[1].SessionIds.Should().BeEmpty();
    }

    [Fact]
    public void ComputeWeekSummaries_PartialEdgeWeeks_ReportOverlapDays()
    {
        // Range Wed May 13 – Tue May 19: 5 days of the first week, 2 of the second.
        var result = API.Services.Sleep.SleepReportCalculator.ComputeWeekSummaries(
            [], from: new DateTime(2026, 5, 13), to: new DateTime(2026, 5, 19));

        result.Should().HaveCount(2);
        result[0].DaysInRange.Should().Be(5);
        result[1].DaysInRange.Should().Be(2);
    }

    [Fact]
    public void ComputeWeekSummaries_NoonRule_EarlyMorningStartBelongsToPreviousWeek()
    {
        // In bed 1am Monday May 18 → display day Sunday May 17 → week of May 11.
        var nights = new[] { MakeNight(new DateTime(2026, 5, 18, 1, 0, 0, DateTimeKind.Utc)) };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeWeekSummaries(
            nights, from: new DateTime(2026, 5, 11), to: new DateTime(2026, 5, 24));

        result[0].WeekStart.Should().Be(new DateTime(2026, 5, 11));
        result[0].NightCount.Should().Be(1);
        result[1].NightCount.Should().Be(0);
    }

    [Fact]
    public void ComputeWeekSummaries_SpilloverNightBeforeRange_WidensToItsWeek()
    {
        // Range starts Monday May 18, but a night in bed 00:30 that Monday displays
        // under Sunday May 17, whose week (May 11) precedes the range.
        var nights = new[] { MakeNight(new DateTime(2026, 5, 18, 0, 30, 0, DateTimeKind.Utc)) };

        var result = API.Services.Sleep.SleepReportCalculator.ComputeWeekSummaries(
            nights, from: new DateTime(2026, 5, 18), to: new DateTime(2026, 5, 24));

        result.Should().HaveCount(2);
        result[0].WeekStart.Should().Be(new DateTime(2026, 5, 11));
        result[0].NightCount.Should().Be(1);
        result[0].DaysInRange.Should().Be(1, "floored at NightCount even though the week has no overlap with the range");
    }

    [Fact]
    public void ComputeWeekSummaries_EmptyRange_ReturnsEmpty()
    {
        var result = API.Services.Sleep.SleepReportCalculator.ComputeWeekSummaries(
            [], from: new DateTime(2026, 5, 18), to: new DateTime(2026, 5, 11));

        result.Should().BeEmpty();
    }
}
