using Nocturne.Core.Models;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// Generates wearable-style health data — heart rate, step counts, and sleep
/// sessions — shaped by the same per-date <see cref="DayScenario"/> as the
/// glucose/treatment streams, so exercise days carry the workout step spike and
/// heart-rate ramp that explain the glucose dip, sick days show elevated HR with
/// minimal movement, and poor-sleep days follow a visibly fragmented night.
/// All output is deterministic per date (see <see cref="DayScenarios"/>).
/// </summary>
public static class DemoHealthDataGenerator
{
    private const string WearableDevice = "Demo Watch";

    /// <summary>
    /// Heart rate samples (5-minute cadence) and hourly step-count deltas for
    /// one local calendar day. <paramref name="localDay"/> is a local midnight;
    /// timestamps are stored UTC. <paramref name="dataSource"/> stamps every
    /// record and prefixes the deterministic sync identifiers, so re-seeding
    /// updates in place via the (DataSource, SyncIdentifier) dedup key.
    /// </summary>
    public static (List<HeartRate> HeartRates, List<StepCount> StepCounts) GenerateDailyActivity(
        DateTime localDay, string dataSource)
    {
        var scenario = DayScenarios.For(localDay);
        var rng = DayScenarios.RngFor(localDay, "activity");

        // Exercise window: a 75-minute workout starting late afternoon.
        var workoutStartHour = 16 + DayScenarios.Roll(localDay, "workout", 4); // 16-19
        var workoutStart = localDay.AddHours(workoutStartHour).AddMinutes(rng.Next(0, 45));
        var workoutEnd = workoutStart.AddMinutes(75);
        var hasWorkout = scenario == DayScenario.Exercise;

        var heartRates = new List<HeartRate>();
        for (var t = localDay; t < localDay.AddDays(1); t = t.AddMinutes(5))
        {
            var bpm = BaselineHeartRate(t.Hour, rng);
            if (hasWorkout && t >= workoutStart && t < workoutEnd)
            {
                // Ramp up, plateau, ramp down across the workout.
                var progress = (t - workoutStart).TotalMinutes / 75.0;
                var intensity = progress < 0.2 ? progress / 0.2
                    : progress > 0.85 ? (1.0 - progress) / 0.15
                    : 1.0;
                bpm = (int)(bpm + intensity * rng.Next(55, 75));
            }

            bpm += scenario switch
            {
                DayScenario.SickDay => rng.Next(10, 16),
                DayScenario.StressDay => rng.Next(6, 12),
                DayScenario.PoorSleep when t.Hour is >= 1 and <= 5 && rng.NextDouble() < 0.25
                    => rng.Next(15, 25), // nocturnal awakenings
                _ => 0,
            };

            var utc = t.ToUniversalTime();
            heartRates.Add(new HeartRate
            {
                Timestamp = utc,
                Bpm = bpm,
                Accuracy = 3,
                Device = WearableDevice,
                EnteredBy = dataSource,
                DataSource = dataSource,
                // Keyed on local wall-clock, which the generation loop never
                // repeats — a UTC key would collide across a DST spring-forward
                // hour and silently drop samples via the sync-id upsert.
                SyncIdentifier = $"{dataSource}:hr:{t:yyyyMMddTHHmmss}",
                CreatedAt = utc.ToString("o"),
            });
        }

        var stepCounts = new List<StepCount>();
        for (var hour = 0; hour < 24; hour++)
        {
            var t = localDay.AddHours(hour);
            var steps = BaselineSteps(hour, rng);

            if (hasWorkout && hour >= workoutStart.Hour && hour <= workoutEnd.Hour)
                steps += rng.Next(2500, 4000);

            steps = scenario switch
            {
                DayScenario.SickDay => steps / 6,
                DayScenario.PoorSleep => (int)(steps * 0.6),
                _ => steps,
            };

            if (steps <= 0)
                continue;

            var utc = t.AddMinutes(rng.Next(0, 50)).ToUniversalTime();
            stepCounts.Add(new StepCount
            {
                Timestamp = utc,
                Metric = steps,
                Source = 0, // delta bucket; the steps report sums metrics per day
                Device = WearableDevice,
                EnteredBy = dataSource,
                DataSource = dataSource,
                SyncIdentifier = $"{dataSource}:steps:{localDay:yyyyMMdd}:{hour:D2}",
                CreatedAt = utc.ToString("o"),
            });
        }

        return (heartRates, stepCounts);
    }

    /// <summary>
    /// Resting-through-active heart rate by local hour: nighttime dip with the
    /// lowest values in deep-sleep hours, gentle daytime activity.
    /// </summary>
    private static int BaselineHeartRate(int hour, Random rng) => hour switch
    {
        >= 0 and < 3 => rng.Next(52, 60),
        >= 3 and < 6 => rng.Next(48, 56), // deepest sleep
        >= 6 and < 8 => rng.Next(58, 70), // waking
        >= 8 and < 22 => rng.Next(64, 84),
        _ => rng.Next(56, 66), // winding down
    };

    /// <summary>
    /// Hourly step deltas: nothing overnight, commute/lunch/evening bumps.
    /// </summary>
    private static int BaselineSteps(int hour, Random rng) => hour switch
    {
        < 7 => 0,
        7 or 8 => rng.Next(400, 1100), // morning routine + commute
        12 or 13 => rng.Next(500, 1200), // lunch walk
        17 or 18 => rng.Next(400, 1000), // evening
        >= 22 => rng.Next(0, 80),
        _ => rng.Next(120, 650),
    };

    /// <summary>
    /// One overnight sleep session for the night ending on the morning of
    /// <paramref name="localMorning"/> (lights-out the previous evening,
    /// 22:00–23:30 local). Realistic ~90-minute stage cycles — deep sleep
    /// concentrated early, REM lengthening toward morning — with per-stage
    /// biometric samples and derived summary fields. The morning's
    /// <see cref="DayScenario"/> shapes the night: PoorSleep fragments it,
    /// Exercise deepens it. OriginalId is stable per night so re-seeding
    /// upserts via the (Source, OriginalId) dedup key.
    /// </summary>
    public static SleepSession GenerateSleepSession(DateTime localMorning, string sourceApp)
    {
        var scenario = DayScenarios.For(localMorning);
        var rng = DayScenarios.RngFor(localMorning, "sleep");
        var poorNight = scenario == DayScenario.PoorSleep;
        var deepMultiplier = scenario == DayScenario.Exercise ? 1.15 : 1.0;

        var bedtimeLocal = localMorning.AddDays(-1).AddHours(22).AddMinutes(rng.Next(0, 90));
        var bedtime = bedtimeLocal.ToUniversalTime();

        var stages = new List<SleepStageInterval>();
        long deepMs = 0, lightMs = 0, remMs = 0, awakeMs = 0;
        var cursor = bedtime;
        var ordinal = 0;

        void AddStage(SleepStageType stage, int minutes)
        {
            if (minutes <= 0)
                return;
            var end = cursor.AddMinutes(minutes);
            stages.Add(new SleepStageInterval
            {
                StartTime = cursor,
                EndTime = end,
                Stage = stage,
                Ordinal = ordinal++,
            });
            var ms = (long)minutes * 60_000;
            switch (stage)
            {
                case SleepStageType.Deep: deepMs += ms; break;
                case SleepStageType.Rem: remMs += ms; break;
                case SleepStageType.Light or SleepStageType.Asleep: lightMs += ms; break;
                default: awakeMs += ms; break;
            }
            cursor = end;
        }

        var latencyMinutes = poorNight ? rng.Next(20, 45) : rng.Next(5, 20);
        AddStage(SleepStageType.Awake, latencyMinutes);

        // 5–6 ~90-minute cycles → a realistic ~7–8 h night; poor nights lose a cycle.
        var cycles = poorNight ? rng.Next(4, 6) : rng.Next(5, 7);
        var wakeProbability = poorNight ? 0.85 : 0.5;
        for (var c = 0; c < cycles; c++)
        {
            var progress = c / (double)cycles;
            AddStage(SleepStageType.Light, rng.Next(15, 30));
            AddStage(SleepStageType.Deep,
                (int)(rng.Next(20, 45) * (1.0 - 0.6 * progress) * deepMultiplier)); // deep fades through the night
            AddStage(SleepStageType.Light, rng.Next(12, 22));
            AddStage(SleepStageType.Rem, (int)(rng.Next(12, 30) * (0.5 + 0.8 * progress))); // REM lengthens toward morning
            if (c < cycles - 1 && rng.NextDouble() < wakeProbability)
                AddStage(SleepStageType.Awake, poorNight ? rng.Next(5, 18) : rng.Next(2, 8));
        }
        AddStage(SleepStageType.Awake, rng.Next(2, 10));

        var start = bedtime;
        var end = cursor;
        var durationMs = (long)(end - start).TotalMilliseconds;
        var totalSleepMs = deepMs + lightMs + remMs;

        var samples = new List<SleepBiometricSample>();
        for (var t = start.AddMinutes(10); t < end; t = t.AddMinutes(rng.Next(18, 26)))
        {
            var stage = stages.FirstOrDefault(s => s.StartTime <= t && s.EndTime > t)?.Stage
                ?? SleepStageType.Light;
            float hr = stage switch
            {
                SleepStageType.Deep => rng.Next(48, 54),
                SleepStageType.Rem => rng.Next(56, 64),
                SleepStageType.Awake or SleepStageType.AwakeInBed => rng.Next(60, 70),
                _ => rng.Next(52, 60),
            };
            samples.Add(new SleepBiometricSample
            {
                Timestamp = t,
                HeartRate = hr,
                Hrv = rng.Next(40, 90),
                Spo2 = rng.Next(94, 99),
                RespirationRate = rng.Next(12, 17),
                Movement = (float)Math.Round(rng.NextDouble(), 2),
            });
        }

        var efficiency = durationMs > 0 ? (float)Math.Round(100.0 * totalSleepMs / durationMs, 1) : 0f;
        var restfulPct = totalSleepMs > 0 ? (deepMs + remMs) * 100.0 / totalSleepMs : 0;
        var score = (short)Math.Clamp((int)Math.Round(efficiency * 0.5 + restfulPct * 0.5), 50, 98);

        return new SleepSession
        {
            StartTime = start,
            EndTime = end,
            Type = SleepSessionType.Overnight,
            DetectionMethod = SleepDetectionMethod.Auto,
            Source = SleepSource.Oura,
            SourceDevice = "Oura Ring Gen3",
            SourceApp = sourceApp,
            IsMainSleep = true,
            DurationMs = durationMs,
            TotalSleepMs = totalSleepMs,
            TotalAwakeMs = awakeMs,
            DeepSleepMs = deepMs,
            LightSleepMs = lightMs,
            RemSleepMs = remMs,
            SleepLatencyMs = (long)latencyMinutes * 60_000,
            Efficiency = efficiency,
            RestlessPeriods = stages.Count(s => s.Stage == SleepStageType.Awake) - 1, // exclude sleep-onset latency
            SleepScore = score,
            AvgHeartRate = samples.Count > 0 ? (float)Math.Round(samples.Average(s => s.HeartRate!.Value), 1) : null,
            MinHeartRate = samples.Count > 0 ? samples.Min(s => s.HeartRate!.Value) : null,
            AvgHrv = samples.Count > 0 ? (float)Math.Round(samples.Average(s => s.Hrv!.Value), 1) : null,
            AvgBreathRate = samples.Count > 0 ? (float)Math.Round(samples.Average(s => s.RespirationRate!.Value), 1) : null,
            AvgSpo2 = samples.Count > 0 ? (float)Math.Round(samples.Average(s => s.Spo2!.Value), 1) : null,
            // Stable per-night key so re-seeding upserts rather than duplicates.
            OriginalId = $"{sourceApp}:sleep:{start:yyyy-MM-dd}",
            Stages = stages,
            BiometricSamples = samples,
        };
    }
}
