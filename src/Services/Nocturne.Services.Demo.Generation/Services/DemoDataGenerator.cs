using Microsoft.Extensions.Options;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Models;
using Nocturne.Services.Demo.Configuration;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// Interface for generating demo glucose and treatment data.
/// </summary>
public interface IDemoDataGenerator
{
    /// <summary>
    /// Whether the generator is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the current configuration.
    /// </summary>
    DemoModeConfiguration GetConfiguration();

    /// <summary>
    /// Generates a single glucose entry for the current time.
    /// </summary>
    Entry GenerateCurrentEntry();

    /// <summary>
    /// Generates current treatments based on the latest entry.
    /// </summary>
    IEnumerable<Treatment> GenerateCurrentTreatments(Entry entry);

    /// <summary>
    /// Seeds the current glucose from the latest backfill entry.
    /// </summary>
    void SeedCurrentGlucose(double glucose);

    /// <summary>
    /// Generates the device status for the current realtime tick, carrying
    /// IOB/COB continuity from the treatments issued so far.
    /// </summary>
    DeviceStatus GenerateCurrentDeviceStatus(Entry entry, IReadOnlyList<Treatment> treatments);

    /// <summary>
    /// Streams the unified historical timeline: one simulation pass yielding
    /// entries, treatments, and simulator state per 5-minute step, so every
    /// derived stream (chart, treatments, device status, alarm episodes)
    /// agrees. Each call runs a fresh simulation — consumers must enumerate
    /// once and project what they need.
    /// </summary>
    /// <param name="endDate">Local end of the timeline; now when omitted.</param>
    IEnumerable<DemoTimeStep> GenerateHistoricalTimeline(DateTime? endDate = null);
}

/// <summary>
/// Generates realistic demo CGM and treatment data using oref pharmacokinetic models.
/// Uses the same insulin curves and carb absorption algorithms as OpenAPS/Loop.
/// </summary>
public class DemoDataGenerator : IDemoDataGenerator
{
    private readonly ILogger<DemoDataGenerator> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DemoModeConfiguration _config;
    private readonly Random _random;
    private double _currentGlucose;
    private readonly object _lock = new();
    private const double PumpBolusIncrementUnits = 0.1;
    private const double PumpBasalIncrementUnits = 0.05;
    private const double PumpMaxBolusUnits = 25.0;
    private const double PumpMaxBasalRateUnitsPerHour = 5.0;

    /// <summary>The loop's maxSafeBasal as a multiple of the base rate.</summary>
    private const double MaxSafeBasalMultiplier = 2.0;

    private const int LoopTempMinutes = 30;
    private const int TrendStepsMin = 3;
    private const int TrendStepsMax = 8;
    private static readonly double[] TrendStepMultipliers = { 0.3, 0.6, 1.0, 1.3, 1.6, 2.0 };
    private double _trendTargetGlucose;
    private int _trendStepsRemaining;
    private DateTime? _lastTempBasalIssuedAt;
    private OrefPhysiologySimulator? _realtimeSimulator;

    public bool IsRunning { get; internal set; }

    public DemoDataGenerator(
        IOptions<DemoModeConfiguration> config,
        ILogger<DemoDataGenerator> logger,
        ILoggerFactory loggerFactory
    )
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config.Value;
        _random = _config.RandomSeed is { } seed ? new Random(seed) : new Random();
        _currentGlucose = _config.InitialGlucose;
    }

    /// <summary>
    /// Creates an OrefProfile from the current configuration and scenario parameters.
    /// </summary>
    private OrefProfile CreateOrefProfile(ScenarioParameters scenarioParams)
    {
        return new OrefProfile
        {
            Dia = _config.InsulinDurationMinutes / 60.0,
            CurrentBasal = _config.BasalRate * scenarioParams.BasalMultiplier,
            MaxIob = 10.0,
            MaxBasal = 4.0,
            MinBg = 80,
            MaxBg = 120,
            Sens = _config.InsulinSensitivityFactor * scenarioParams.InsulinSensitivityMultiplier,
            CarbRatio = _config.CarbRatio * scenarioParams.InsulinSensitivityMultiplier,
            Curve = "rapid-acting",
            Peak = (int)_config.InsulinPeakMinutes,
            Min5mCarbimpact = 8,
            MaxCob = 120,
            AutosensMin = 0.7,
            AutosensMax = 1.2,
        };
    }

    public DemoModeConfiguration GetConfiguration() => _config;

    public Entry GenerateCurrentEntry()
    {
        lock (_lock)
        {
            var nextGlucose = GetNextTrendGlucose();
            var change = nextGlucose - _currentGlucose;
            _currentGlucose = nextGlucose;

            var now = DateTime.UtcNow;
            var mills = new DateTimeOffset(now).ToUnixTimeMilliseconds();
            var direction = CalculateDirection(change);

            return new Entry
            {
                Type = "sgv",
                Device = _config.Device,
                Mills = mills,
                Date = now,
                DateString = now.ToString("o"),
                Mgdl = Math.Round(_currentGlucose, 0),
                Sgv = Math.Round(_currentGlucose, 0),
                Direction = direction.ToString(),
                Delta = Math.Round(change, 1),
                DataSource = DataSources.DemoService,
                Filtered = Math.Round(_currentGlucose + (_random.NextDouble() - 0.5) * 2, 0),
                Unfiltered = Math.Round(_currentGlucose + (_random.NextDouble() - 0.5) * 5, 0),
                Rssi = _random.Next(0, 101),
                Noise = _random.Next(0, 5),
                CreatedAt = now.ToString("o"),
                ModifiedAt = now,
            };
        }
    }

    public IEnumerable<Treatment> GenerateCurrentTreatments(Entry entry)
    {
        var tempBasalDuration = Math.Max(5, _config.TempBasalDurationMinutes);
        var entryTime = entry.Date ?? DateTime.UtcNow;

        if (!CanIssueTempBasal(entryTime, tempBasalDuration))
        {
            yield break;
        }

        var glucose = entry.Sgv ?? entry.Mgdl;
        var delta = entry.Delta ?? 0;
        var targetGlucose = _config.TargetGlucose;

        if (glucose > targetGlucose + 10)
        {
            var (_, highTempRate) = HighTempRate(glucose - targetGlucose);
            if (highTempRate > _config.BasalRate)
            {
                yield return MarkTempBasal(entryTime, highTempRate, tempBasalDuration);
            }
        }
        else if (glucose < 90 || (glucose < 100 && delta < -2))
        {
            var reductionFactor =
                glucose < 75 ? 0.0
                : glucose < 85 ? 0.2
                : 0.4;
            var reducedRate = NormalizeBasalRate(_config.BasalRate * reductionFactor);
            if (reducedRate < _config.BasalRate)
            {
                yield return MarkTempBasal(entryTime, reducedRate, tempBasalDuration);
            }
        }
        else
        {
            var localTime = entryTime.Kind == DateTimeKind.Utc ? entryTime.ToLocalTime() : entryTime;
            yield return MarkTempBasal(entryTime, InRangeTempRate(localTime, glucose - targetGlucose), tempBasalDuration);
        }
    }

    public void SeedCurrentGlucose(double glucose)
    {
        lock (_lock)
        {
            _currentGlucose = glucose;
            _trendTargetGlucose = 0;
            _trendStepsRemaining = 0;
            _lastTempBasalIssuedAt = null;
        }
    }

    /// <summary>
    /// The single historical simulation pass. Runs the oref simulator over the
    /// backfill window and yields one <see cref="DemoTimeStep"/> per 5 minutes
    /// carrying the CGM entry, the treatments issued at that step, and the
    /// simulator's IOB/COB — so the chart, the treatment history, the device
    /// status stream, and the alarm episodes all derive from the same run.
    /// </summary>
    public IEnumerable<DemoTimeStep> GenerateHistoricalTimeline(DateTime? endDate = null)
    {
        // Local-time day iteration: meals land at local wall-clock mealtimes,
        // and the per-date DayScenario key matches the sleep/activity/device
        // generators, which anchor on local dates. Timestamps convert to UTC
        // at the point of storage (DateTimeOffset respects Kind).
        var end = endDate ?? DateTime.Now;
        var startDate = end.AddDays(-_config.BackfillDays);

        _logger.LogInformation(
            "Streaming historical timeline from {StartDate} to {EndDate}",
            startDate,
            end
        );

        var currentDay = startDate.Date;
        double? previousDayEndingGlucose = null;
        double previousDayMomentum = 0;
        var totalSteps = 0;
        // Treatments timestamped past a day boundary (late boluses near
        // midnight) carry into the next day's step buckets.
        var carriedOver = new List<(DateTime Time, Treatment Treatment)>();
        DateTime? lastLevelCorrectionAt = null;
        DateTime? lastCarbsAt = null;
        var recentGlucose = new Queue<double>();

        while (currentDay <= end.Date)
        {
            var dayScenario = SelectDayScenario(currentDay);
            var scenarioParams = GetScenarioParameters(dayScenario);
            var orefProfile = CreateOrefProfile(scenarioParams);
            var simulator = new OrefPhysiologySimulator(
                _loggerFactory.CreateLogger<OrefPhysiologySimulator>(),
                orefProfile
            );

            double glucose =
                previousDayEndingGlucose
                ?? scenarioParams.FastingGlucose + (_random.NextDouble() - 0.5) * 20;
            var mealPlan = GenerateMealPlan(currentDay, dayScenario);
            var basalAdjustments = GenerateBasalAdjustments(currentDay, dayScenario);
            if (HasDawnHigh(currentDay, dayScenario))
            {
                scenarioParams.DawnPhenomenonStrength = DawnHighStrength;
                mealPlan = LateBreakfast(currentDay, mealPlan);
            }

            // Pre-populate the simulator with the day's meals and boluses, and
            // bucket the corresponding treatments by their actual timestamps.
            var pending = new List<(DateTime Time, Treatment Treatment)>(carriedOver);
            carriedOver = [];
            foreach (var meal in mealPlan)
            {
                // The simulator's absorption curve peaks at 0.4x its nominal time.
                var absorptionHours =
                    _config.CarbAbsorptionPeakMinutes / 0.4 / 60.0 / meal.GlycemicIndex;
                simulator.AddCarbs(meal.MealTime, meal.Carbs, absorptionHours);
                var (lapseMinutes, lapseScale) = MealBolusLapse(dayScenario);
                var bolusTime = meal.MealTime.AddMinutes(meal.BolusOffsetMinutes + lapseMinutes);
                var bolus = NormalizeBolus(CalculateMealBolus(meal.Carbs, glucose, scenarioParams) * lapseScale);
                simulator.AddInsulinDose(bolusTime, bolus);

                pending.Add((meal.MealTime, CreateCarbTreatment(meal.MealTime, meal.Carbs, meal.FoodType)));
                pending.Add((bolusTime, CreateBolusTreatment(
                    bolusTime,
                    bolus,
                    meal.FoodType == "Snack" ? "Snack Bolus" : "Meal Bolus"
                )));
            }

            pending.AddRange(PlanNotes(currentDay));
            pending.Sort((a, b) => a.Time.CompareTo(b.Time));
            var fingerstickTimes = PlanFingersticks(currentDay);
            var calibrations = PlanCalibrations(currentDay);

            var effectiveIsf =
                _config.InsulinSensitivityFactor * scenarioParams.InsulinSensitivityMultiplier;

            double glucoseMomentum = previousDayMomentum * 0.5;
            double lastGlucose = glucose;
            DateTime? lastLowTreatedAt = null;
            var targetGlucose = _config.TargetGlucose;
            var currentTime = currentDay;
            // Cap endTime to now to prevent generating future data
            var endTime = currentDay.Date == end.Date
                ? end
                : currentDay.AddDays(1);

            while (currentTime < endTime)
            {
                var stepEnd = currentTime.AddMinutes(5);
                // The final (partial) step must not emit anything past "now" —
                // a future-dated planned item would outrank the realtime stream.
                var consumeUntil = stepEnd < endTime ? stepEnd : endTime;
                var stepTreatments = new List<Treatment>();
                double? tempRate = null;
                int? tempDuration = null;
                double? requestedRate = null;
                (double Rate, double? Requested)? loopTemp = null;
                var loop = DemoLoopInterruptions.StateAt(currentTime, end);

                // Consume planned treatments due this step.
                while (pending.Count > 0 && pending[0].Time < consumeUntil)
                {
                    if (pending[0].Treatment.Carbs > 0)
                        lastCarbsAt = pending[0].Time;
                    stepTreatments.Add(pending[0].Treatment);
                    pending.RemoveAt(0);
                }
                var smbEnabled = loop == DemoLoopState.Closed
                    && lastCarbsAt is { } carbsAt && currentTime - carbsAt < SmbAfterCarbsTime;

                var basalAdj = basalAdjustments.FirstOrDefault(b =>
                    Math.Abs((b.Time - currentTime).TotalMinutes) < 2.5
                );
                var adjustedRate = NormalizeBasalRate(basalAdj.Rate);
                var adjustedDuration = DemoLoopInterruptions.MinutesToNext(currentTime, basalAdj.Duration, end);
                if (loop == DemoLoopState.Closed && adjustedDuration > 0 && (adjustedRate > 0 || basalAdj.Duration > 0))
                {
                    stepTreatments.Add(CreateTempBasalTreatment(currentTime, adjustedRate, adjustedDuration));
                    tempRate = adjustedRate;
                    tempDuration = adjustedDuration;
                    simulator.AddInsulinDose(
                        currentTime,
                        adjustedRate * adjustedDuration / 60.0,
                        isTempBasal: true,
                        duration: adjustedDuration
                    );
                }

                glucose = SimulateGlucoseWithOref(
                    glucose,
                    currentTime,
                    simulator,
                    scenarioParams,
                    dayScenario,
                    ref glucoseMomentum
                );

                // 400 is the CGM reporting ceiling; MaxGlucose bounds the live random walk.
                glucose = Math.Max(40, Math.Min(400, glucose));

                var hour = currentTime.Hour;
                var isWakingHours = hour >= 7 && hour < 22;

                // Handle LOW glucose - treat with fast carbs, often confirmed
                // with a fingerstick as a real user would. A low takes ~15 min
                // to notice, longer asleep, and each treatment gets 15 minutes
                // to work before the next. Until then the pump's suspend below
                // still applies.
                var noticeChance = isWakingHours ? 0.35 : 0.2;
                if (glucose < 70
                    && (lastLowTreatedAt is null || (currentTime - lastLowTreatedAt.Value).TotalMinutes >= 15)
                    && _random.NextDouble() < noticeChance)
                {
                    lastLowTreatedAt = currentTime;
                    var correctionCarbs =
                        glucose < 55 ? _random.Next(15, 25) : _random.Next(10, 18);
                    stepTreatments.Add(CreateCarbCorrectionTreatment(currentTime, correctionCarbs));
                    lastCarbsAt = currentTime;
                    simulator.AddCarbs(currentTime, correctionCarbs, 0.4);

                    if (_random.NextDouble() < 0.4)
                        stepTreatments.Add(CreateBGCheckTreatment(currentTime, glucose));
                }
                // Handle HIGH glucose - aggressive AID-style insulin delivery
                else if (glucose > targetGlucose + 10)
                {
                    var glucoseAboveTarget = glucose - targetGlucose;
                    var insulinNeeded = glucoseAboveTarget / effectiveIsf;
                    var insulinToDeliver = Math.Max(0, insulinNeeded - simulator.CalculateIob(currentTime));

                    // A high the insulin on board already covers needs no high temp: oref's
                    // eventual glucose counts it.
                    var (requested, highTempRate) = HighTempRate(glucoseAboveTarget);
                    loopTemp = insulinNeeded > simulator.CalculateIob(currentTime)
                        ? (highTempRate, requested)
                        : (InRangeTempRate(currentTime, 0), null);
                    var correctedRecently = lastLevelCorrectionAt is { } corrected
                        && currentTime - corrected < LevelCorrectionQuietTime;

                    if (!correctedRecently
                        && loop == DemoLoopState.Closed
                        && recentGlucose.Count == 3
                        && !stepTreatments.Any(t => t.Insulin > 0)
                        && LevelCorrection(
                            currentTime, glucose, recentGlucose.Peek(), simulator.CalculateIob(currentTime), effectiveIsf,
                            dayScenario, mealPlan, pending, end) is { } levelCorrection)
                    {
                        lastLevelCorrectionAt = currentTime;
                        stepTreatments.Add(CreateManualCorrectionBolusTreatment(currentTime, levelCorrection));
                        simulator.AddInsulinDose(currentTime, levelCorrection);
                    }
                    // MANUAL CORRECTION BOLUS. A person corrects a clear high, not a mild one.
                    // Nor within minutes of it: ~3% per step is one in ~2.5 hours.
                    else if (!correctedRecently
                             && isWakingHours && glucose > targetGlucose + 90 && _random.NextDouble() < 0.03)
                    {
                        var manualCorrectionBolus = insulinToDeliver;
                        manualCorrectionBolus = NormalizeBolus(
                            Math.Clamp(manualCorrectionBolus, 0.5, 6.0)
                        );
                        stepTreatments.Add(CreateManualCorrectionBolusTreatment(currentTime, manualCorrectionBolus));
                        simulator.AddInsulinDose(currentTime, manualCorrectionBolus);
                    }
                    // A larger automatic bolus every 30 minutes when significantly high. Trio uploads it
                    // as an SMB like any other, so it is not read as the person's correction.
                    else if (
                        smbEnabled
                        && glucose > targetGlucose + 40
                        && currentTime.Minute % 30 == 0
                        && insulinToDeliver > 0.1
                    )
                    {
                        var correctionBolus = insulinToDeliver * (0.3 + _random.NextDouble() * 0.1);
                        correctionBolus = NormalizeBolus(Math.Clamp(correctionBolus, 0.1, 1.0));
                        stepTreatments.Add(CreateAlgorithmBolusTreatment(currentTime, correctionBolus));
                        simulator.AddInsulinDose(currentTime, correctionBolus);
                    }
                    // SMBs every 5 minutes for fine-tuning when moderately high
                    else if (smbEnabled && glucose > targetGlucose + 30 && insulinToDeliver > 0.05)
                    {
                        var algorithmBolus = NormalizeBolus(
                            Math.Clamp(insulinToDeliver * 0.1, 0.05, 0.3)
                        );
                        if (algorithmBolus >= PumpBolusIncrementUnits)
                        {
                            stepTreatments.Add(CreateAlgorithmBolusTreatment(currentTime, algorithmBolus));
                        }
                        simulator.AddInsulinDose(currentTime, algorithmBolus);
                    }
                }
                // Reduce basal when trending low (predictive low glucose suspend)
                else if (glucose < 90 || (glucose < 100 && glucoseMomentum < -0.3))
                {
                    var reductionFactor =
                        glucose < 75 ? 0.0
                        : glucose < 85 ? 0.2
                        : 0.4;
                    loopTemp = (NormalizeBasalRate(_config.BasalRate * reductionFactor), null);
                }
                else
                {
                    loopTemp = (InRangeTempRate(currentTime, glucose - targetGlucose), null);
                }

                if (loop == DemoLoopState.Manual)
                {
                    // Open loop: every cycle suggests a temp that the pump never runs.
                    (tempRate, requestedRate) = loopTemp ?? (NormalizeBasalRate(_config.BasalRate), null);
                    tempDuration = LoopTempMinutes;
                }
                else if (loop == DemoLoopState.Closed
                         && loopTemp is { } temp
                         && (currentTime.Minute == 0 || currentTime.Minute == 30)
                         && tempRate is null
                         && DemoLoopInterruptions.MinutesToNext(currentTime, LoopTempMinutes, end) is var minutes and > 0)
                {
                    stepTreatments.Add(CreateTempBasalTreatment(currentTime, temp.Rate, minutes));
                    tempRate = temp.Rate;
                    tempDuration = minutes;
                    requestedRate = temp.Requested;
                    // Below the scheduled rate the dose is negative: less insulin than scheduled.
                    var extraInsulin = (temp.Rate - _config.BasalRate) * minutes / 60.0;
                    if (Math.Abs(extraInsulin) > 0.001)
                    {
                        simulator.AddInsulinDose(
                            currentTime,
                            extraInsulin,
                            isTempBasal: true,
                            duration: minutes
                        );
                    }
                }

                var delta = glucose - lastGlucose;
                var entry = CreateEntry(currentTime, glucose, delta);

                List<Entry> extraEntries = [];
                while (fingerstickTimes.Count > 0 && fingerstickTimes[0] < consumeUntil)
                {
                    extraEntries.Add(CreateFingerstickEntry(fingerstickTimes[0], glucose));
                    fingerstickTimes.RemoveAt(0);
                }
                while (calibrations.Count > 0 && calibrations[0].Time < consumeUntil)
                {
                    extraEntries.Add(CreateCalibrationEntry(calibrations[0]));
                    calibrations.RemoveAt(0);
                }

                yield return new DemoTimeStep
                {
                    Time = currentTime,
                    Entry = entry,
                    ExtraEntries = extraEntries,
                    Treatments = stepTreatments,
                    Iob = Math.Max(0, simulator.CalculateIob(currentTime)),
                    Cob = Math.Max(0, simulator.CalculateCob(currentTime)),
                    TempBasalRate = tempRate,
                    TempBasalDuration = tempDuration,
                    RequestedBasalRate = requestedRate,
                    MaxSafeBasalRate = MaxSafeBasalRate,
                    Loop = loop,
                    EffectiveIsf = effectiveIsf,
                    EffectiveCarbRatio = scenarioParams.CarbRatio,
                    Scenario = dayScenario,
                };
                totalSteps++;

                lastGlucose = glucose;
                recentGlucose.Enqueue(glucose);
                if (recentGlucose.Count > 3)
                    recentGlucose.Dequeue();
                currentTime = stepEnd;
                simulator.CleanupExpired(currentTime);
            }

            // Unconsumed items (late boluses past midnight) carry into the next
            // day. On the final day they would be future-dated — drop them.
            if (currentDay != end.Date)
                carriedOver = pending;

            previousDayEndingGlucose = glucose;
            previousDayMomentum = glucoseMomentum;
            currentDay = currentDay.AddDays(1);
        }

        _logger.LogInformation("Streamed {StepCount} timeline steps", totalSteps);
    }


    /// <summary>
    /// Deterministic per-date selection (see <see cref="DayScenarios"/>): the
    /// entry and treatment streams iterate days independently, so a random roll
    /// here would give the same date different scenarios in each stream.
    /// </summary>
    private static DayScenario SelectDayScenario(DateTime date) => DayScenarios.For(date);


    /// <summary>
    /// Simulates glucose changes using oref pharmacokinetic models plus scenario-specific effects.
    /// Uses the OrefPhysiologySimulator for insulin activity and carb absorption calculations.
    /// </summary>
    private double SimulateGlucoseWithOref(
        double currentGlucose,
        DateTime time,
        OrefPhysiologySimulator simulator,
        ScenarioParameters @params,
        DayScenario scenario,
        ref double momentum
    )
    {
        var hour = time.Hour + time.Minute / 60.0;

        // Use oref simulator for core glucose prediction (insulin and carb effects)
        var simulatedGlucose = simulator.SimulateNextGlucose(currentGlucose, time);
        var orefChange = simulatedGlucose - currentGlucose;

        // Basal effect - background insulin lowering glucose slightly each interval
        // Without basal, glucose would rise ~0.5-1 mg/dL per 5 min from liver glucose output
        var liverGlucoseOutput = 0.5 + _random.NextDouble() * 0.3; // ~0.7 mg/dL/5min average
        var basalCoverage = @params.BasalMultiplier * 0.7; // Basal covers liver output
        var netBasalEffect = liverGlucoseOutput - basalCoverage;

        // Dawn phenomenon - moderate effect, liver dumps glucose 4-8am
        var dawnEffect = 0.0;
        if (hour >= 4 && hour < 8)
        {
            var dawnIntensity = Math.Sin((hour - 4) * Math.PI / 4); // Peaks around 6am
            dawnEffect = @params.DawnPhenomenonStrength * 1.5 * dawnIntensity;
        }

        // Exercise effects - can drop glucose 50-100 mg/dL over 2 hours
        var exerciseEffect = 0.0;
        if (@params.HasExercise)
        {
            if (hour >= 16 && hour < 17)
                exerciseEffect = -2.5; // During exercise - rapid drop
            else if (hour >= 17 && hour < 18)
                exerciseEffect = -1.8;
            else if (hour >= 18 && hour < 22)
                exerciseEffect = -0.8; // Post-exercise sensitivity
            else if (hour >= 22 || hour < 6)
                exerciseEffect = -0.3; // Overnight sensitivity increase
        }

        // Net glucose change this interval (oref handles insulin/carbs, we add scenario effects)
        var netChange = orefChange + netBasalEffect + dawnEffect + exerciseEffect;

        // CGM noise and lag
        var noise = (_random.NextDouble() - 0.5) * 3.0;

        var targetChange = netChange + noise;

        // Minimal smoothing - real glucose moves sharply after meals
        // Only smooth to prevent unrealistic jumps, not to dampen real movement
        momentum = momentum * 0.1 + targetChange * 0.9;

        // Real CGM can show up to 3-4 mg/dL/min during rapid rises/falls
        // That's 15-20 mg/dL per 5-minute interval
        const double maxChangePerInterval = 15.0;
        momentum = Math.Clamp(momentum, -maxChangePerInterval, maxChangePerInterval);

        var glucose = currentGlucose + momentum;

        // Occasional CGM artifacts - compression lows, signal drops (rare)
        if (_random.NextDouble() < 0.002)
            glucose += (_random.NextDouble() - 0.5) * 15;

        // Scenario-specific modifiers - kept subtle
        if (scenario == DayScenario.SickDay)
            glucose += (_random.NextDouble() - 0.3) * 1.0; // Slight upward trend when sick
        else if (scenario == DayScenario.StressDay && _random.NextDouble() < 0.05)
            glucose += _random.Next(2, 6); // Occasional stress spikes

        return glucose;
    }

    private ScenarioParameters GetScenarioParameters(DayScenario scenario)
    {
        // Add random daily variation - even "normal" days vary
        var dailyVariation = 0.9 + _random.NextDouble() * 0.2; // 90-110% effectiveness (was 80-120%)

        return scenario switch
        {
            DayScenario.Normal => new ScenarioParameters
            {
                FastingGlucose = 95 + _random.Next(-10, 20),
                CarbRatio = _config.CarbRatio * (0.95 + dailyVariation * 0.1),
                BasalMultiplier = 0.95 + _random.NextDouble() * 0.1,
                InsulinSensitivityMultiplier = 0.95 + dailyVariation * 0.1, // Closer to 1.0 for better control
                DawnPhenomenonStrength = 0.1 + _random.NextDouble() * 0.15,
            },
            DayScenario.HighDay => new ScenarioParameters
            {
                FastingGlucose = 110 + _random.Next(0, 25),
                CarbRatio = _config.CarbRatio * (0.95 + dailyVariation * 0.05),
                BasalMultiplier = 1.0 + _random.NextDouble() * 0.1,
                InsulinSensitivityMultiplier = 0.7 + _random.NextDouble() * 0.1,
                DawnPhenomenonStrength = 0.2 + _random.NextDouble() * 0.15,
            },
            DayScenario.LowDay => new ScenarioParameters
            {
                FastingGlucose = 80 + _random.Next(-10, 15),
                CarbRatio = _config.CarbRatio * 1.2 * dailyVariation,
                BasalMultiplier = 0.75 + _random.NextDouble() * 0.15,
                InsulinSensitivityMultiplier = 1.2 + _random.NextDouble() * 0.2,
                DawnPhenomenonStrength = 0.05,
            },
            DayScenario.Exercise => new ScenarioParameters
            {
                FastingGlucose = 90 + _random.Next(-10, 15),
                CarbRatio = _config.CarbRatio * 1.2,
                BasalMultiplier = 0.65 + _random.NextDouble() * 0.15,
                InsulinSensitivityMultiplier = 1.3 + _random.NextDouble() * 0.3,
                DawnPhenomenonStrength = 0.1,
                HasExercise = true,
            },
            DayScenario.SickDay => new ScenarioParameters
            {
                FastingGlucose = 125 + _random.Next(0, 30),
                CarbRatio = _config.CarbRatio * 0.9,
                BasalMultiplier = 1.1 + _random.NextDouble() * 0.1,
                InsulinSensitivityMultiplier = 0.65 + _random.NextDouble() * 0.1,
                DawnPhenomenonStrength = 0.25,
            },
            DayScenario.StressDay => new ScenarioParameters
            {
                FastingGlucose = 105 + _random.Next(0, 20),
                CarbRatio = _config.CarbRatio * 0.95,
                BasalMultiplier = 1.0 + _random.NextDouble() * 0.1,
                InsulinSensitivityMultiplier = 0.85 + _random.NextDouble() * 0.1,
                DawnPhenomenonStrength = 0.2,
            },
            DayScenario.PoorSleep => new ScenarioParameters
            {
                FastingGlucose = 105 + _random.Next(-10, 20),
                CarbRatio = _config.CarbRatio * 0.95,
                BasalMultiplier = 1.0 + _random.NextDouble() * 0.1,
                InsulinSensitivityMultiplier = 0.9 + _random.NextDouble() * 0.1,
                DawnPhenomenonStrength = 0.25,
            },
            _ => new ScenarioParameters
            {
                FastingGlucose = 100 + _random.Next(-15, 30),
                CarbRatio = _config.CarbRatio,
                BasalMultiplier = 1.0,
                InsulinSensitivityMultiplier = 1.0,
                DawnPhenomenonStrength = 0.3,
            },
        };
    }

    private record MealEvent(
        DateTime MealTime,
        double Carbs,
        string FoodType,
        int BolusOffsetMinutes,
        double GlycemicIndex
    );

    private List<MealEvent> GenerateMealPlan(DateTime date, DayScenario scenario)
    {
        var meals = new List<MealEvent>();

        // Breakfast - often rushed, sometimes skipped
        if (_random.NextDouble() > 0.1) // 10% chance of skipping
        {
            var breakfastHour = 6 + _random.Next(0, 4);
            var breakfastMinute = _random.Next(0, 12) * 5;
            var breakfastCarbs =
                scenario == DayScenario.LowDay ? _random.Next(20, 40)
                : scenario == DayScenario.HighDay ? _random.Next(50, 80)
                : _random.Next(35, 65);

            // Bolus timing - more realistic distribution with better pre-bolusing
            // Negative = pre-bolus, Positive = late bolus
            int bolusOffset;
            var timingRoll = _random.NextDouble();
            if (timingRoll < 0.25)
                bolusOffset = _random.Next(-15, -3); // Pre-bolused (good practice)
            else if (timingRoll < 0.55)
                bolusOffset = _random.Next(0, 10); // Roughly on time
            else if (timingRoll < 0.80)
                bolusOffset = _random.Next(10, 25); // Slightly late bolus
            else if (timingRoll < 0.93)
                bolusOffset = _random.Next(25, 50); // Late - causes spike
            else
                bolusOffset = _random.Next(50, 90); // Forgot, bolused later

            meals.Add(
                new MealEvent(
                    date.AddHours(breakfastHour).AddMinutes(breakfastMinute),
                    breakfastCarbs,
                    "Breakfast",
                    bolusOffset,
                    0.7 + _random.NextDouble() * 0.8 // GI variation (0.7-1.5)
                )
            );
        }

        // Lunch
        var lunchHour = 11 + _random.Next(0, 3);
        var lunchMinute = _random.Next(0, 12) * 5;
        var lunchCarbs =
            scenario == DayScenario.LowDay ? _random.Next(30, 55)
            : scenario == DayScenario.HighDay ? _random.Next(60, 95)
            : _random.Next(45, 75);

        // Lunch bolusing - more realistic timing
        int lunchBolusOffset;
        var lunchTimingRoll = _random.NextDouble();
        if (lunchTimingRoll < 0.20)
            lunchBolusOffset = _random.Next(-10, 0);
        else if (lunchTimingRoll < 0.50)
            lunchBolusOffset = _random.Next(0, 10);
        else if (lunchTimingRoll < 0.75)
            lunchBolusOffset = _random.Next(10, 25);
        else if (lunchTimingRoll < 0.92)
            lunchBolusOffset = _random.Next(25, 45);
        else
            lunchBolusOffset = _random.Next(45, 75);

        meals.Add(
            new MealEvent(
                date.AddHours(lunchHour).AddMinutes(lunchMinute),
                lunchCarbs,
                "Lunch",
                lunchBolusOffset,
                0.6 + _random.NextDouble() * 0.9 // Restaurant food varies (0.6-1.5)
            )
        );

        // Dinner - variable but not extreme
        var dinnerHour = 17 + _random.Next(0, 4);
        var dinnerMinute = _random.Next(0, 12) * 5;
        var dinnerCarbs =
            scenario == DayScenario.LowDay ? _random.Next(35, 60)
            : scenario == DayScenario.HighDay ? _random.Next(65, 100)
            : _random.Next(50, 85);

        // Dinner timing - more realistic pre-bolusing
        int dinnerBolusOffset;
        var dinnerTimingRoll = _random.NextDouble();
        if (dinnerTimingRoll < 0.25)
            dinnerBolusOffset = _random.Next(-15, -3); // Pre-bolused
        else if (dinnerTimingRoll < 0.55)
            dinnerBolusOffset = _random.Next(0, 15);
        else if (dinnerTimingRoll < 0.80)
            dinnerBolusOffset = _random.Next(15, 35);
        else
            dinnerBolusOffset = _random.Next(35, 60); // Distracted, late

        meals.Add(
            new MealEvent(
                date.AddHours(dinnerHour).AddMinutes(dinnerMinute),
                dinnerCarbs,
                "Dinner",
                dinnerBolusOffset,
                0.5 + _random.NextDouble() * 1.0 // GI (0.5-1.5)
            )
        );

        // Snacks - sometimes bolused, sometimes not
        if (_random.NextDouble() < 0.4) // Reduced frequency of snacks
        {
            var snackBolus =
                _random.NextDouble() < 0.5 ? _random.Next(15, 45) : _random.Next(0, 15);
            meals.Add(
                new MealEvent(
                    date.AddHours(10 + _random.NextDouble() * 1.5),
                    _random.Next(10, 20),
                    "Snack",
                    snackBolus,
                    1.0 + _random.NextDouble() * 0.4
                )
            );
        }

        if (_random.NextDouble() < 0.35) // Afternoon snack
        {
            var snackBolus =
                _random.NextDouble() < 0.6 ? _random.Next(10, 40) : _random.Next(0, 10);
            meals.Add(
                new MealEvent(
                    date.AddHours(15 + _random.NextDouble() * 1.5),
                    _random.Next(10, 25),
                    "Snack",
                    snackBolus,
                    1.0 + _random.NextDouble() * 0.5
                )
            );
        }

        // Late night snacking - rare
        if (_random.NextDouble() < 0.15)
        {
            meals.Add(
                new MealEvent(
                    date.AddHours(21 + _random.NextDouble() * 2),
                    _random.Next(8, 20),
                    "Snack",
                    _random.Next(5, 30),
                    1.0 + _random.NextDouble() * 0.4
                )
            );
        }

        // Random unplanned eating - rare
        if (_random.NextDouble() < 0.1)
        {
            var randomHour = 8 + _random.Next(0, 12);
            meals.Add(
                new MealEvent(
                    date.AddHours(randomHour + _random.NextDouble()),
                    _random.Next(8, 15),
                    "Snack",
                    _random.Next(10, 35),
                    1.2 // Usually high GI impulsive foods
                )
            );
        }

        return meals;
    }

    private List<(DateTime Time, double Rate, int Duration)> GenerateBasalAdjustments(
        DateTime date,
        DayScenario scenario
    )
    {
        var adjustments = new List<(DateTime Time, double Rate, int Duration)>();

        if (scenario == DayScenario.Exercise)
        {
            var exerciseHour = _random.Next(16, 20);
            adjustments.Add(
                (date.AddHours(exerciseHour - 1), NormalizeBasalRate(_config.BasalRate * 0.5), 120)
            );
        }

        if (scenario == DayScenario.LowDay && _random.NextDouble() < 0.5)
        {
            var lowHour = _random.Next(10, 16);
            adjustments.Add(
                (date.AddHours(lowHour), NormalizeBasalRate(_config.BasalRate * 0.6), 60)
            );
        }

        if (scenario == DayScenario.HighDay && _random.NextDouble() < 0.5)
        {
            var highHour = _random.Next(10, 18);
            adjustments.Add(
                (date.AddHours(highHour), NormalizeBasalRate(_config.BasalRate * 1.3), 120)
            );
        }

        return adjustments;
    }


    /// <summary>
    /// A forgotten meal bolus, caught once the rise shows (an hour or two
    /// later) and dosed short. Its likelihood tracks the day's scenario, so
    /// high, sick and stressful days carry the post-meal excursions that a
    /// well-behaved AID would otherwise flatten away.
    /// </summary>
    private (int ExtraMinutes, double Scale) MealBolusLapse(DayScenario scenario)
    {
        var chance = scenario switch
        {
            DayScenario.HighDay => 0.35,
            DayScenario.SickDay or DayScenario.StressDay => 0.25,
            DayScenario.PoorSleep => 0.2,
            DayScenario.Normal => 0.1,
            _ => 0.1,
        };
        if (_random.NextDouble() >= chance)
            return (0, 1.0);
        return (_random.Next(75, 180), 0.5 + _random.NextDouble() * 0.3);
    }

    private double CalculateMealBolus(
        double carbs,
        double currentGlucose,
        ScenarioParameters @params
    )
    {
        // Carb counting - mostly accurate with some variation (typical AID user)
        var carbCountingError = _random.NextDouble();
        double estimatedCarbs;
        if (carbCountingError < 0.40)
            estimatedCarbs = carbs * (0.6 + _random.NextDouble() * 0.25); // 60-85% - underestimate
        else if (carbCountingError < 0.90)
            estimatedCarbs = carbs * (0.9 + _random.NextDouble() * 0.15); // 90-105% - near enough
        else
            estimatedCarbs = carbs * (1.0 + _random.NextDouble() * 0.1); // 100-110% - slight overestimate

        var carbBolus = estimatedCarbs / @params.CarbRatio;

        // Add correction bolus when glucose is elevated (AID systems are aggressive about this)
        var correctionBolus = 0.0;
        if (currentGlucose > _config.TargetGlucose + 10 && _random.NextDouble() < 0.85) // 85% add correction
        {
            // Use ISF adjusted by scenario's insulin sensitivity multiplier
            var effectiveIsf =
                _config.InsulinSensitivityFactor * @params.InsulinSensitivityMultiplier;
            correctionBolus = (currentGlucose - _config.TargetGlucose) / effectiveIsf;
            // Correction aggressiveness - AID systems deliver more of the calculated correction
            correctionBolus *= 0.8 + _random.NextDouble() * 0.2; // 80-100% of calculated
            correctionBolus = Math.Min(correctionBolus, 5.0); // Cap at 5 units
        }

        var totalBolus = carbBolus + correctionBolus;

        // Occasional errors (less frequent with AID - system helps prevent mistakes)
        if (_random.NextDouble() < 0.02)
            totalBolus *= 0.5 + _random.NextDouble() * 0.3; // Forgot some of bolus (50-80%) - rare
        else if (_random.NextDouble() < 0.01)
            totalBolus *= 1.3 + _random.NextDouble() * 0.2; // Over-bolused slightly (130-150%) - rare

        return NormalizeBolus(totalBolus);
    }

    private Entry CreateEntry(DateTime time, double glucose, double? delta)
    {
        var mills = new DateTimeOffset(time).ToUnixTimeMilliseconds();
        var direction = CalculateDirection(delta ?? 0);

        return new Entry
        {
            Type = "sgv",
            Device = _config.Device,
            Mills = mills,
            Date = time,
            DateString = time.ToString("o"),
            Mgdl = Math.Round(glucose, 0),
            Sgv = Math.Round(glucose, 0),
            Direction = direction.ToString(),
            Delta = delta.HasValue ? Math.Round(delta.Value, 1) : null,
            DataSource = DataSources.DemoService,
            Filtered = Math.Round(glucose + (_random.NextDouble() - 0.5) * 2, 0),
            Unfiltered = Math.Round(glucose + (_random.NextDouble() - 0.5) * 5, 0),
            Rssi = _random.Next(0, 101),
            Noise = _random.Next(0, 3),
            CreatedAt = time.ToString("o"),
            ModifiedAt = time,
        };
    }

    private Treatment CreateCarbTreatment(DateTime time, double carbs, string foodType)
    {
        return new Treatment
        {
            EventType = "Carbs",
            Carbs = carbs,
            FoodType = foodType,
            Mills = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
            Created_at = time.ToString("o"),
            EnteredBy = "demo-user",
            DataSource = DataSources.DemoService,
        };
    }

    private Treatment CreateBolusTreatment(DateTime time, double insulin, string eventType)
    {
        return new Treatment
        {
            EventType = eventType,
            Insulin = insulin,
            Mills = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
            Created_at = time.ToString("o"),
            EnteredBy = "demo-user",
            DataSource = DataSources.DemoService,
        };
    }

    private Treatment CreateManualCorrectionBolusTreatment(DateTime time, double insulin)
    {
        return new Treatment
        {
            EventType = "Correction Bolus",
            Insulin = insulin,
            Mills = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
            Created_at = time.ToString("o"),
            EnteredBy = "demo-user", // User manually corrects with exact ISF calculation
            Notes = "Manual correction",
            DataSource = DataSources.DemoService,
        };
    }

    private Treatment CreateAlgorithmBolusTreatment(DateTime time, double insulin)
    {
        return new Treatment
        {
            EventType = "SMB", // Super Micro Bolus - algorithm-delivered bolus
            Insulin = insulin,
            Mills = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
            Created_at = time.ToString("o"),
            EnteredBy = "demo-pump",
            DataSource = DataSources.DemoService,
        };
    }

    private Treatment CreateCarbCorrectionTreatment(DateTime time, double carbs)
    {
        return new Treatment
        {
            EventType = "Carb Correction",
            Carbs = carbs,
            Mills = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
            Created_at = time.ToString("o"),
            EnteredBy = "demo-user",
            Notes = "Low treatment",
            DataSource = DataSources.DemoService,
        };
    }

    private Treatment CreateTempBasalTreatment(DateTime time, double rate, int duration)
    {
        return new Treatment
        {
            EventType = "Temp Basal",
            Rate = rate,
            Duration = duration,
            Mills = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
            Created_at = time.ToString("o"),
            EnteredBy = DemoDeviceStatusGenerator.DeviceName,
            DataSource = DataSources.DemoService,
        };
    }

    private Treatment MarkTempBasal(DateTime time, double rate, int duration)
    {
        _lastTempBasalIssuedAt = time;
        return CreateTempBasalTreatment(time, rate, duration);
    }

    private bool CanIssueTempBasal(DateTime time, int durationMinutes)
    {
        if (durationMinutes <= 0)
        {
            return false;
        }

        return !_lastTempBasalIssuedAt.HasValue
            || (time - _lastTempBasalIssuedAt.Value).TotalMinutes >= durationMinutes;
    }

    /// <summary>
    /// Fingerstick times for a day: a usual morning check plus a frequent
    /// pre-dinner one. Deterministic per date so re-seeds are idempotent.
    /// </summary>
    private static List<DateTime> PlanFingersticks(DateTime day)
    {
        var rng = DayScenarios.RngFor(day, "fingerstick");
        var times = new List<DateTime>();
        if (rng.NextDouble() < 0.85)
            times.Add(day.AddHours(6).AddMinutes(40 + rng.Next(0, 55)));
        if (rng.NextDouble() < 0.6)
            times.Add(day.AddHours(17).AddMinutes(15 + rng.Next(0, 75)));
        return times;
    }

    private sealed record CalibrationPlan(DateTime Time, double Slope, double Intercept);

    /// <summary>
    /// Two calibrations roughly two hours after a sensor start, mirroring a
    /// sensor warmup ritual. Empty on non-sensor-change days.
    /// </summary>
    private static List<CalibrationPlan> PlanCalibrations(DateTime day)
    {
        var sensorStart = DemoDeviceLifecycle.ChangeTimeOn(day, "Sensor Start");
        if (sensorStart is null)
            return [];

        var rng = DayScenarios.RngFor(day, "calibration");
        var first = sensorStart.Value.AddMinutes(115 + rng.Next(0, 20));
        return
        [
            new CalibrationPlan(first, 1000 + rng.Next(0, 120), 30000 + rng.Next(0, 4000)),
            new CalibrationPlan(
                first.AddMinutes(12 + rng.Next(0, 10)),
                1000 + rng.Next(0, 120),
                30000 + rng.Next(0, 4000)),
        ];
    }

    private static readonly string[] NotePool =
    [
        "Site a bit sore today, watching absorption",
        "Long walk after lunch",
        "Slept in, skipped breakfast",
        "Coffee seemed to spike me more than usual",
        "Feeling a cold coming on",
        "New sensor reading close to fingerstick",
        "Pizza night - extended the bolus",
        "Forgot to prebolus, chasing the spike",
        "Gym session went well, no lows",
        "Stressful workday, running high",
        "Tried a lower-carb lunch today",
        "Pod alarm went off during meeting",
        "Swapped infusion site to left side",
        "Grazing at a family BBQ, lots of small boluses",
    ];

    private static readonly string[] AnnouncementPool =
    [
        "Endo appointment booked for next month",
        "Started new insulin cartridge batch",
        "Reviewing basal rates with the care team this week",
        "Travelling next weekend - remember spare supplies",
    ];

    /// <summary>
    /// Occasional diary notes plus a weekly announcement, as standalone
    /// treatments the decomposer turns into Note records.
    /// </summary>
    private static List<(DateTime Time, Treatment Treatment)> PlanNotes(DateTime day)
    {
        var rng = DayScenarios.RngFor(day, "notes");
        var planned = new List<(DateTime, Treatment)>();

        if (rng.NextDouble() < 0.35)
        {
            var time = day.AddHours(8 + rng.Next(0, 13)).AddMinutes(rng.Next(0, 60));
            planned.Add((time, CreateNoteTreatment(time, NotePool[rng.Next(NotePool.Length)], announcement: false)));
        }

        if (day.DayOfWeek == DayOfWeek.Sunday && rng.NextDouble() < 0.5)
        {
            var time = day.AddHours(10).AddMinutes(rng.Next(0, 40));
            planned.Add((time, CreateNoteTreatment(time, AnnouncementPool[rng.Next(AnnouncementPool.Length)], announcement: true)));
        }

        return planned;
    }

    /// <summary>Fingerstick (mbg) entry near the CGM value, with meter-scale noise.</summary>
    private Entry CreateFingerstickEntry(DateTime time, double cgmGlucose)
    {
        var value = Math.Round(cgmGlucose * (0.92 + _random.NextDouble() * 0.16) + (_random.NextDouble() - 0.5) * 6, 0);
        value = Math.Max(40, value);

        return new Entry
        {
            Type = "mbg",
            Device = "Contour Next One",
            Mills = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
            Date = time,
            DateString = time.ToString("o"),
            Mbg = value,
            Mgdl = value,
            DataSource = DataSources.DemoService,
            CreatedAt = time.ToString("o"),
            ModifiedAt = time,
        };
    }

    /// <summary>Sensor calibration (cal) entry in xDrip-style slope/intercept form.</summary>
    private Entry CreateCalibrationEntry(CalibrationPlan plan)
    {
        return new Entry
        {
            Type = "cal",
            Device = _config.Device,
            Mills = new DateTimeOffset(plan.Time).ToUnixTimeMilliseconds(),
            Date = plan.Time,
            DateString = plan.Time.ToString("o"),
            Slope = plan.Slope,
            Intercept = plan.Intercept,
            Scale = 1,
            DataSource = DataSources.DemoService,
            CreatedAt = plan.Time.ToString("o"),
            ModifiedAt = plan.Time,
        };
    }

    /// <summary>Fingerstick BG Check treatment confirming a CGM low.</summary>
    private Treatment CreateBGCheckTreatment(DateTime time, double glucose)
    {
        return new Treatment
        {
            EventType = "BG Check",
            Glucose = Math.Max(40, Math.Round(glucose + (_random.NextDouble() - 0.5) * 8, 0)),
            GlucoseType = "Finger",
            Units = "mg/dl",
            Mills = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
            Created_at = time.ToString("o"),
            EnteredBy = "demo-user",
            DataSource = DataSources.DemoService,
        };
    }

    private static Treatment CreateNoteTreatment(DateTime time, string text, bool announcement)
    {
        return new Treatment
        {
            EventType = announcement ? "Announcement" : "Note",
            Notes = text,
            Mills = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
            Created_at = time.ToString("o"),
            EnteredBy = "demo-user",
            DataSource = DataSources.DemoService,
        };
    }

    /// <summary>
    /// Realtime device status: IOB/COB continuity comes from a persistent
    /// simulator fed with the treatments issued by the realtime ticks;
    /// consumable levels are the same wall-clock functions the historical
    /// stream uses, so the two streams agree across the seed/realtime boundary.
    /// </summary>
    public DeviceStatus GenerateCurrentDeviceStatus(Entry entry, IReadOnlyList<Treatment> treatments)
    {
        lock (_lock)
        {
            var nowUtc = entry.Date ?? DateTime.UtcNow;
            var nowLocal = nowUtc.Kind == DateTimeKind.Utc ? nowUtc.ToLocalTime() : nowUtc;

            _realtimeSimulator ??= new OrefPhysiologySimulator(
                _loggerFactory.CreateLogger<OrefPhysiologySimulator>(),
                CreateOrefProfile(new ScenarioParameters
                {
                    FastingGlucose = _config.TargetGlucose,
                    CarbRatio = _config.CarbRatio,
                    BasalMultiplier = 1.0,
                    InsulinSensitivityMultiplier = 1.0,
                    DawnPhenomenonStrength = 0.2,
                })
            );

            double? tempRate = null;
            int? tempDuration = null;
            foreach (var treatment in treatments)
            {
                if (treatment.Insulin is > 0)
                    _realtimeSimulator.AddInsulinDose(nowUtc, treatment.Insulin.Value);
                if (treatment.Carbs is > 0)
                    _realtimeSimulator.AddCarbs(nowUtc, treatment.Carbs.Value);
                if (treatment.EventType == "Temp Basal" && treatment.Rate is { } rate)
                {
                    tempRate = rate;
                    tempDuration = treatment.Duration is > 0 ? (int)treatment.Duration.Value : null;
                    var extra = (rate - _config.BasalRate) * (treatment.Duration ?? 0) / 60.0;
                    if (Math.Abs(extra) > 0.001)
                    {
                        _realtimeSimulator.AddInsulinDose(
                            nowUtc, extra, isTempBasal: true, duration: treatment.Duration ?? 0);
                    }
                }
            }

            _realtimeSimulator.CleanupExpired(nowUtc);
            var iob = Math.Max(0, _realtimeSimulator.CalculateIob(nowUtc));
            var cob = Math.Max(0, _realtimeSimulator.CalculateCob(nowUtc));

            var glucose = entry.Sgv ?? entry.Mgdl;
            double? requestedRate = tempRate > _config.BasalRate
                ? HighTempRate(glucose - _config.TargetGlucose).Requested
                : null;

            return DemoDeviceStatusGenerator.Create(
                nowLocal,
                glucose,
                iob,
                cob,
                tempRate,
                tempDuration,
                _config.InsulinSensitivityFactor,
                _config.CarbRatio,
                _config.TargetGlucose,
                DemoTherapyProfile.ScheduledRateAt(nowLocal, _config.BasalRate),
                DayScenarios.For(nowLocal.Date),
                requestedRate,
                MaxSafeBasalRate);
        }
    }

    private double GetNextTrendGlucose()
    {
        if (_trendStepsRemaining <= 0)
        {
            SetNewTrendTarget();
            _trendStepsRemaining = _random.Next(TrendStepsMin, TrendStepsMax + 1);
        }

        var step = CalculateTrendStep(_currentGlucose, _trendTargetGlucose, _trendStepsRemaining);
        _trendStepsRemaining--;

        if (_trendStepsRemaining == 0)
        {
            SetNewTrendTarget();
            _trendStepsRemaining = _random.Next(TrendStepsMin, TrendStepsMax + 1);
        }

        return Math.Clamp(_currentGlucose + step, _config.MinGlucose, _config.MaxGlucose);
    }

    private void SetNewTrendTarget()
    {
        if (_trendTargetGlucose <= 0)
        {
            _trendTargetGlucose = GetInitialTrendTarget();
            return;
        }

        var difference = _random.NextDouble() < 0.5
            ? -_random.Next(20, 51)
            : _random.Next(20, 51);
        var nextTarget = _trendTargetGlucose + difference;
        _trendTargetGlucose = Math.Clamp(nextTarget, _config.MinGlucose, _config.MaxGlucose);
    }

    private double GetInitialTrendTarget()
    {
        var minTarget = Math.Max((double)_config.MinGlucose, 80);
        var maxTarget = Math.Min((double)_config.MaxGlucose, 110);
        if (minTarget >= maxTarget)
        {
            minTarget = _config.MinGlucose;
            maxTarget = _config.MaxGlucose;
        }

        return minTarget + _random.NextDouble() * (maxTarget - minTarget);
    }

    private double CalculateTrendStep(double currentGlucose, double targetGlucose, int stepsRemaining)
    {
        var stepBase = (targetGlucose - currentGlucose) / Math.Max(1, stepsRemaining);
        var multiplier = TrendStepMultipliers[_random.Next(TrendStepMultipliers.Length)];
        var step = stepBase * multiplier;

        if (Math.Abs(step) < 0.5 && Math.Abs(targetGlucose - currentGlucose) >= 1)
        {
            step = Math.Sign(targetGlucose - currentGlucose);
        }

        return step;
    }

    private double RoundToIncrement(double value, double increment)
    {
        if (increment <= 0)
        {
            return value;
        }

        return Math.Round(value / increment, MidpointRounding.AwayFromZero) * increment;
    }

    private double NormalizeBolus(double units)
    {
        if (units <= 0)
        {
            return 0;
        }

        var clamped = Math.Min(units, PumpMaxBolusUnits);
        var rounded = RoundToIncrement(clamped, PumpBolusIncrementUnits);
        return Math.Clamp(rounded, PumpBolusIncrementUnits, PumpMaxBolusUnits);
    }

    /// <summary>
    /// The dawn phenomenon on a strong morning: a rise from 4 to 8 am that the loop's temp basals
    /// slow but do not stop, to about 200 mg/dL by 7 am, which the person corrects on waking
    /// (<see cref="LevelCorrection"/>).
    /// </summary>
    private const double DawnHighStrength = 1.9;

    /// <summary>The weekdays with a strong dawn rise: four, so every week of the sample has them.</summary>
    private static readonly DayOfWeek[] DawnHighDays =
        [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Saturday];

    /// <summary>
    /// Whether the morning has a strong dawn rise and a late breakfast, so a correction on waking
    /// has the hours before food that <see cref="LevelCorrection"/> needs. A sick day is left out:
    /// the illness mask takes its hours out of the analysis.
    /// </summary>
    private static bool HasDawnHigh(DateTime day, DayScenario scenario) =>
        scenario != DayScenario.SickDay && DawnHighDays.Contains(day.DayOfWeek);

    /// <summary>
    /// The meal plan with breakfast at 10:00 to 10:45 at the earliest, no other food before it, and
    /// lunch at least three hours after it: a late breakfast moves the next meal later, and no
    /// morning snack is eaten between them.
    /// </summary>
    private static List<MealEvent> LateBreakfast(DateTime day, List<MealEvent> meals)
    {
        var breakfastAt = day.AddHours(10).AddMinutes(5 * DayScenarios.Roll(day, "late-breakfast", 10));
        var lunchFrom = breakfastAt.AddHours(3);
        return
        [
            .. meals
                .Where(m => m.FoodType is "Breakfast" or "Lunch" || m.MealTime >= lunchFrom)
                .Select(m => m.FoodType switch
                {
                    "Breakfast" when m.MealTime < breakfastAt => m with { MealTime = breakfastAt },
                    "Lunch" when m.MealTime < lunchFrom => m with { MealTime = lunchFrom },
                    _ => m,
                }),
        ];
    }

    /// <summary>
    /// How long after carbs the loop may give automatic boluses, as Trio's enableSMB_after_carbs:
    /// outside it, and whenever the loop is not closed, the loop corrects a high with temp basals
    /// alone.
    /// </summary>
    private static readonly TimeSpan SmbAfterCarbsTime = TimeSpan.FromHours(6);

    /// <summary>
    /// The share of the bolus calculator's dose the person gives for a level high: a cautious
    /// correction, since the loop has more to add and a full dose with the dawn rise ending
    /// would run low.
    /// </summary>
    private const double LevelCorrectionShare = 0.8;

    /// <summary>How long after a level-high correction the person eats nothing.</summary>
    private static readonly TimeSpan LevelCorrectionCarbFreeTime = TimeSpan.FromHours(3);

    /// <summary>How long after a level-high correction the person leaves it to act before correcting again.</summary>
    private static readonly TimeSpan LevelCorrectionQuietTime = TimeSpan.FromHours(4);

    /// <summary>
    /// The person's correction of a high that has levelled off, away from food, other boluses and
    /// loop interruptions for the hours it acts, while awake: <see cref="LevelCorrectionShare"/> of
    /// the bolus calculator's dose, the need less the insulin on board, given once what is on board
    /// is no more than the dose. These are the corrections the Settings evidence page reads an ISF
    /// from. A sick day is left out: the illness mask takes its hours out of the analysis.
    /// </summary>
    /// <param name="glucoseBefore">Glucose 15 minutes earlier; a high still moving faster is still rising or falling.</param>
    private double? LevelCorrection(
        DateTime time,
        double glucose,
        double glucoseBefore,
        double iob,
        double effectiveIsf,
        DayScenario scenario,
        IEnumerable<MealEvent> meals,
        IEnumerable<(DateTime Time, Treatment Treatment)> pending,
        DateTime seedEnd)
    {
        if (scenario == DayScenario.SickDay
            || time.Hour is >= 1 and < 6
            || glucose <= _config.TargetGlucose + 40
            || Math.Abs(glucose - glucoseBefore) > 12)
            return null;

        var foodFreeUntil = time + LevelCorrectionCarbFreeTime;
        if (meals.Any(m => m.MealTime > time.AddMinutes(-90) && m.MealTime < foodFreeUntil)
            || pending.Any(p => p.Treatment.Insulin > 0 && p.Time < foodFreeUntil)
            || DemoLoopInterruptions.On(time, seedEnd).Concat(DemoLoopInterruptions.On(time.AddDays(1), seedEnd))
                .Any(i => i.Start < time + LevelCorrectionQuietTime && i.End > time))
            return null;

        var dose = NormalizeBolus(Math.Min(
            LevelCorrectionShare * ((glucose - _config.TargetGlucose) / effectiveIsf - Math.Max(0, iob)), 5.0));
        return dose >= 0.6 && iob <= dose ? dose : null;
    }

    /// <summary>
    /// The temp the loop sets while glucose is in range: the scheduled rate nudged towards target,
    /// as oref sets a small adjustment most cycles rather than leaving the schedule to run for hours.
    /// </summary>
    private double InRangeTempRate(DateTime localTime, double glucoseAboveTarget) =>
        NormalizeBasalRate(DemoTherapyProfile.ScheduledRateAt(localTime, _config.BasalRate) * (1 + glucoseAboveTarget / 150.0));

    private double MaxSafeBasalRate => NormalizeBasalRate(_config.BasalRate * MaxSafeBasalMultiplier);

    /// <summary>
    /// The high temp the loop asks for at <paramref name="glucoseAboveTarget"/>, and what its
    /// maxSafeBasal cuts that to.
    /// </summary>
    private (double Requested, double Capped) HighTempRate(double glucoseAboveTarget)
    {
        var requested = NormalizeBasalRate(_config.BasalRate * (1.1 + glucoseAboveTarget / 150.0));
        return (requested, Math.Min(requested, MaxSafeBasalRate));
    }

    private double NormalizeBasalRate(double unitsPerHour)
    {
        var clamped = Math.Clamp(unitsPerHour, 0, PumpMaxBasalRateUnitsPerHour);
        return RoundToIncrement(clamped, PumpBasalIncrementUnits);
    }

    private double GenerateRandomWalk(double variance = 0)
    {
        var v = variance > 0 ? variance : _config.WalkVariance;
        var u1 = _random.NextDouble();
        var u2 = _random.NextDouble();
        var z0 = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        return z0 * v;
    }

    private Direction CalculateDirection(double change)
    {
        return change switch
        {
            > 10 => Direction.DoubleUp,
            > 5 => Direction.SingleUp,
            > 2 => Direction.FortyFiveUp,
            > -2 => Direction.Flat,
            > -5 => Direction.FortyFiveDown,
            > -10 => Direction.SingleDown,
            _ => Direction.DoubleDown,
        };
    }

    private class ScenarioParameters
    {
        public double FastingGlucose { get; set; }
        public double CarbRatio { get; set; }
        public double BasalMultiplier { get; set; }
        public double InsulinSensitivityMultiplier { get; set; }
        public double DawnPhenomenonStrength { get; set; }
        public bool HasExercise { get; set; }
    }
}
