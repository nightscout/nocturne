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
    /// Generates historical entries using streaming/yield pattern to minimize memory usage.
    /// </summary>
    IEnumerable<Entry> GenerateHistoricalEntries();

    /// <summary>
    /// Generates historical treatments using streaming/yield pattern to minimize memory usage.
    /// </summary>
    IEnumerable<Treatment> GenerateHistoricalTreatments();

    /// <summary>
    /// Generates historical data for the configured time period.
    /// </summary>
    [Obsolete(
        "Use GenerateHistoricalEntries() and GenerateHistoricalTreatments() for streaming pattern"
    )]
    (List<Entry> Entries, List<Treatment> Treatments) GenerateHistoricalData();
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
    private readonly Random _random = new();
    private double _currentGlucose;
    private readonly object _lock = new();
    private const double PumpBolusIncrementUnits = 0.1;
    private const double PumpBasalIncrementUnits = 0.05;
    private const double PumpMaxBolusUnits = 25.0;
    private const double PumpMaxBasalRateUnitsPerHour = 5.0;
    private const int TrendStepsMin = 3;
    private const int TrendStepsMax = 8;
    private static readonly double[] TrendStepMultipliers = { 0.3, 0.6, 1.0, 1.3, 1.6, 2.0 };
    private double _trendTargetGlucose;
    private int _trendStepsRemaining;
    private DateTime? _lastTempBasalIssuedAt;

    private enum DayScenario
    {
        Normal,
        HighDay,
        LowDay,
        Exercise,
        SickDay,
        StressDay,
        PoorSleep,
    }

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
            CarbRatio = _config.CarbRatio / scenarioParams.InsulinSensitivityMultiplier,
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
            var glucoseAboveTarget = glucose - targetGlucose;
            var tempBasalMultiplier = 1.1 + Math.Min(0.3, glucoseAboveTarget / 150.0);
            var highTempRate = NormalizeBasalRate(_config.BasalRate * tempBasalMultiplier);
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

    public (List<Entry> Entries, List<Treatment> Treatments) GenerateHistoricalData()
    {
        var endDate = DateTime.UtcNow;
        var startDate = endDate.AddDays(-_config.BackfillDays);

        var entries = new List<Entry>();
        var treatments = new List<Treatment>();

        _logger.LogInformation(
            "Generating historical demo data from {StartDate} to {EndDate}",
            startDate,
            endDate
        );

        var currentDay = startDate.Date;
        double? previousDayEndingGlucose = null;
        double previousDayMomentum = 0;

        while (currentDay <= endDate.Date)
        {
            var dayScenario = SelectDayScenario(currentDay);
            var (dayEntries, dayTreatments, endingGlucose, endingMomentum) = GenerateDayData(
                currentDay,
                dayScenario,
                previousDayEndingGlucose,
                previousDayMomentum
            );

            entries.AddRange(dayEntries);
            treatments.AddRange(dayTreatments);

            previousDayEndingGlucose = endingGlucose;
            previousDayMomentum = endingMomentum;
            currentDay = currentDay.AddDays(1);
        }

        _logger.LogInformation(
            "Generated {EntryCount} entries and {TreatmentCount} treatments",
            entries.Count,
            treatments.Count
        );

        return (entries, treatments);
    }

    /// <summary>
    /// Generates historical entries using streaming/yield pattern to minimize memory usage.
    /// Each entry is yielded immediately after generation, avoiding large in-memory collections.
    /// </summary>
    public IEnumerable<Entry> GenerateHistoricalEntries()
    {
        var endDate = DateTime.UtcNow;
        var startDate = endDate.AddDays(-_config.BackfillDays);

        _logger.LogInformation(
            "Streaming historical entries from {StartDate} to {EndDate}",
            startDate,
            endDate
        );

        var currentDay = startDate.Date;
        double? previousDayEndingGlucose = null;
        double previousDayMomentum = 0;
        var totalEntries = 0;

        while (currentDay <= endDate.Date)
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

            // Pre-populate events into simulator
            foreach (var meal in mealPlan)
            {
                var absorptionHours =
                    _config.CarbAbsorptionDurationMinutes / 60.0 / meal.GlycemicIndex;
                simulator.AddCarbs(meal.MealTime, meal.Carbs, absorptionHours);
                var bolusTime = meal.MealTime.AddMinutes(meal.BolusOffsetMinutes);
                var bolus = CalculateMealBolus(meal.Carbs, glucose, scenarioParams);
                simulator.AddInsulinDose(bolusTime, bolus);
            }

            double glucoseMomentum = previousDayMomentum * 0.5;
            double lastGlucose = glucose;
            double estimatedIob = 0;
            var targetGlucose = _config.TargetGlucose;
            var currentTime = currentDay;
            // Cap endTime to now to prevent generating future data
            var endTime = currentDay.Date == endDate.Date
                ? endDate
                : currentDay.AddDays(1);

            while (currentTime < endTime)
            {
                var basalAdj = basalAdjustments.FirstOrDefault(b =>
                    Math.Abs((b.Time - currentTime).TotalMinutes) < 2.5
                );
                var adjustedRate = NormalizeBasalRate(basalAdj.Rate);
                if (adjustedRate > 0 || basalAdj.Duration > 0)
                {
                    simulator.AddInsulinDose(
                        currentTime,
                        adjustedRate * basalAdj.Duration / 60.0,
                        isTempBasal: true,
                        duration: basalAdj.Duration
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

                glucose = Math.Max(40, Math.Min(_config.MaxGlucose, glucose));

                var iobDecayRate = 1.0 - (5.0 / _config.InsulinDurationMinutes);
                estimatedIob *= iobDecayRate;

                var hour = currentTime.Hour;
                var isWakingHours = hour >= 7 && hour < 22;

                // Handle reactive glucose management (simplified for entry generation - treatments handled separately)
                if (glucose < 70)
                {
                    var correctionCarbs =
                        glucose < 55 ? _random.Next(15, 25) : _random.Next(10, 18);
                    simulator.AddCarbs(currentTime, correctionCarbs, 0.4);
                }
                else if (glucose > targetGlucose + 10)
                {
                    var glucoseAboveTarget = glucose - targetGlucose;
                    var effectiveIsf =
                        _config.InsulinSensitivityFactor
                        * scenarioParams.InsulinSensitivityMultiplier;
                    var insulinNeeded = glucoseAboveTarget / effectiveIsf;
                    var insulinToDeliver = Math.Max(0, insulinNeeded - estimatedIob * 0.6);

                    if (currentTime.Minute == 0 || currentTime.Minute == 30)
                    {
                        var tempBasalMultiplier = 1.1 + Math.Min(0.3, glucoseAboveTarget / 150.0);
                        var highTempRate = NormalizeBasalRate(_config.BasalRate * tempBasalMultiplier);
                        var extraInsulin =
                            Math.Max(0, highTempRate - _config.BasalRate) * (30 / 60.0);
                        if (extraInsulin > 0)
                        {
                            simulator.AddInsulinDose(
                                currentTime,
                                extraInsulin,
                                isTempBasal: true,
                                duration: 30
                            );
                            estimatedIob += extraInsulin;
                        }
                    }

                    if (
                        glucose > targetGlucose + 15
                        && currentTime.Minute % 5 == 0
                        && insulinToDeliver > 0.1
                    )
                    {
                        var correctionBolus = insulinToDeliver * (0.5 + _random.NextDouble() * 0.2);
                        correctionBolus = NormalizeBolus(Math.Clamp(correctionBolus, 0.1, 4.0));
                        simulator.AddInsulinDose(currentTime, correctionBolus);
                        estimatedIob += correctionBolus;
                    }
                }

                var delta = glucose - lastGlucose;
                yield return CreateEntry(currentTime, glucose, delta);
                totalEntries++;

                lastGlucose = glucose;
                currentTime = currentTime.AddMinutes(5);
                simulator.CleanupExpired(currentTime);
            }

            previousDayEndingGlucose = glucose;
            previousDayMomentum = glucoseMomentum;
            currentDay = currentDay.AddDays(1);
        }

        _logger.LogInformation("Streamed {EntryCount} entries", totalEntries);
    }

    /// <summary>
    /// Generates historical treatments using streaming/yield pattern to minimize memory usage.
    /// Each treatment is yielded immediately after generation, avoiding large in-memory collections.
    /// </summary>
    public IEnumerable<Treatment> GenerateHistoricalTreatments()
    {
        var endDate = DateTime.UtcNow;
        var startDate = endDate.AddDays(-_config.BackfillDays);

        _logger.LogInformation(
            "Streaming historical treatments from {StartDate} to {EndDate}",
            startDate,
            endDate
        );

        var currentDay = startDate.Date;
        double? previousDayEndingGlucose = null;
        double previousDayMomentum = 0;
        var totalTreatments = 0;

        while (currentDay <= endDate.Date)
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

            // Yield meal treatments
            foreach (var meal in mealPlan)
            {
                var absorptionHours =
                    _config.CarbAbsorptionDurationMinutes / 60.0 / meal.GlycemicIndex;
                simulator.AddCarbs(meal.MealTime, meal.Carbs, absorptionHours);

                var bolusTime = meal.MealTime.AddMinutes(meal.BolusOffsetMinutes);
                var bolus = CalculateMealBolus(meal.Carbs, glucose, scenarioParams);
                simulator.AddInsulinDose(bolusTime, bolus);

                yield return CreateCarbTreatment(meal.MealTime, meal.Carbs, meal.FoodType);
                totalTreatments++;

                yield return CreateBolusTreatment(
                    bolusTime,
                    bolus,
                    meal.FoodType == "Snack" ? "Snack Bolus" : "Meal Bolus"
                );
                totalTreatments++;
            }

            double glucoseMomentum = previousDayMomentum * 0.5;
            double lastGlucose = glucose;
            double estimatedIob = 0;
            var targetGlucose = _config.TargetGlucose;
            var currentTime = currentDay;
            // Cap endTime to now to prevent generating future data
            var endTime = currentDay.Date == endDate.Date
                ? endDate
                : currentDay.AddDays(1);

            while (currentTime < endTime)
            {
                var basalAdj = basalAdjustments.FirstOrDefault(b =>
                    Math.Abs((b.Time - currentTime).TotalMinutes) < 2.5
                );
                var adjustedRate = NormalizeBasalRate(basalAdj.Rate);
                if (adjustedRate > 0 || basalAdj.Duration > 0)
                {
                    yield return CreateTempBasalTreatment(
                        currentTime,
                        adjustedRate,
                        basalAdj.Duration
                    );
                    totalTreatments++;
                    simulator.AddInsulinDose(
                        currentTime,
                        adjustedRate * basalAdj.Duration / 60.0,
                        isTempBasal: true,
                        duration: basalAdj.Duration
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

                glucose = Math.Max(40, Math.Min(_config.MaxGlucose, glucose));

                var iobDecayRate = 1.0 - (5.0 / _config.InsulinDurationMinutes);
                estimatedIob *= iobDecayRate;

                var hour = currentTime.Hour;
                var isWakingHours = hour >= 7 && hour < 22;

                // Handle LOW glucose - yield carb correction treatment
                if (glucose < 70)
                {
                    var correctionCarbs =
                        glucose < 55 ? _random.Next(15, 25) : _random.Next(10, 18);
                    yield return CreateCarbCorrectionTreatment(currentTime, correctionCarbs);
                    totalTreatments++;
                    simulator.AddCarbs(currentTime, correctionCarbs, 0.4);
                }
                // Handle HIGH glucose - yield insulin treatments
                else if (glucose > targetGlucose + 10)
                {
                    var glucoseAboveTarget = glucose - targetGlucose;
                    var effectiveIsf =
                        _config.InsulinSensitivityFactor
                        * scenarioParams.InsulinSensitivityMultiplier;
                    var insulinNeeded = glucoseAboveTarget / effectiveIsf;
                    var insulinToDeliver = Math.Max(0, insulinNeeded - estimatedIob * 0.6);

                    if (currentTime.Minute == 0 || currentTime.Minute == 30)
                    {
                        var tempBasalMultiplier = 1.1 + Math.Min(0.3, glucoseAboveTarget / 150.0);
                        var highTempRate = NormalizeBasalRate(_config.BasalRate * tempBasalMultiplier);
                        yield return CreateTempBasalTreatment(
                            currentTime,
                            highTempRate,
                            30
                        );
                        totalTreatments++;
                        var extraInsulin =
                            Math.Max(0, highTempRate - _config.BasalRate) * (30 / 60.0);
                        if (extraInsulin > 0)
                        {
                            simulator.AddInsulinDose(
                                currentTime,
                                extraInsulin,
                                isTempBasal: true,
                                duration: 30
                            );
                            estimatedIob += extraInsulin;
                        }
                    }

                    if (
                        isWakingHours
                        && glucose > targetGlucose + 30
                        && _random.NextDouble() < 0.25
                    )
                    {
                        var manualCorrectionBolus = glucoseAboveTarget / effectiveIsf;
                        manualCorrectionBolus = NormalizeBolus(
                            Math.Clamp(manualCorrectionBolus, 0.5, 6.0)
                        );
                        yield return CreateManualCorrectionBolusTreatment(
                            currentTime,
                            manualCorrectionBolus
                        );
                        totalTreatments++;
                        simulator.AddInsulinDose(currentTime, manualCorrectionBolus);
                        estimatedIob += manualCorrectionBolus;
                    }
                    else if (
                        glucose > targetGlucose + 15
                        && currentTime.Minute % 5 == 0
                        && insulinToDeliver > 0.1
                    )
                    {
                        var correctionBolus = insulinToDeliver * (0.5 + _random.NextDouble() * 0.2);
                        correctionBolus = NormalizeBolus(Math.Clamp(correctionBolus, 0.1, 4.0));
                        yield return CreateCorrectionBolusTreatment(
                            currentTime,
                            correctionBolus
                        );
                        totalTreatments++;
                        simulator.AddInsulinDose(currentTime, correctionBolus);
                        estimatedIob += correctionBolus;
                    }
                    else if (glucose > targetGlucose + 10 && insulinToDeliver > 0.05)
                    {
                        var algorithmBolus = NormalizeBolus(
                            Math.Clamp(insulinToDeliver * 0.25, 0.05, 1.2)
                        );
                        if (algorithmBolus >= PumpBolusIncrementUnits)
                        {
                            yield return CreateAlgorithmBolusTreatment(
                                currentTime,
                                algorithmBolus
                            );
                            totalTreatments++;
                        }
                        simulator.AddInsulinDose(currentTime, algorithmBolus);
                        estimatedIob += algorithmBolus;
                    }
                }
                else if (glucose < 90 || (glucose < 100 && glucoseMomentum < -0.3))
                {
                    var reductionFactor =
                        glucose < 75 ? 0.0
                        : glucose < 85 ? 0.2
                        : 0.4;
                    var reducedRate = NormalizeBasalRate(_config.BasalRate * reductionFactor);

                    if (currentTime.Minute == 0 || currentTime.Minute == 30)
                    {
                        yield return CreateTempBasalTreatment(
                            currentTime,
                            reducedRate,
                            30
                        );
                        totalTreatments++;
                    }
                }

                lastGlucose = glucose;
                currentTime = currentTime.AddMinutes(5);
                simulator.CleanupExpired(currentTime);
            }

            // Yield scheduled basal treatments for the day
            foreach (var basalTreatment in GenerateScheduledBasal(currentDay, scenarioParams))
            {
                yield return basalTreatment;
                totalTreatments++;
            }

            previousDayEndingGlucose = glucose;
            previousDayMomentum = glucoseMomentum;
            currentDay = currentDay.AddDays(1);
        }

        _logger.LogInformation("Streamed {TreatmentCount} treatments", totalTreatments);
    }

    private DayScenario SelectDayScenario(DateTime date)
    {
        var roll = _random.Next(100);
        var isWeekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;

        // T1D management with modern AID - more normal days than challenging days
        if (isWeekend)
        {
            return roll switch
            {
                < 40 => DayScenario.Normal, // 40% normal weekends
                < 55 => DayScenario.HighDay, // 15% high
                < 70 => DayScenario.Exercise, // 15% exercise
                < 80 => DayScenario.PoorSleep, // 10% poor sleep
                < 90 => DayScenario.LowDay, // 10% low
                < 97 => DayScenario.StressDay, // 7% stress
                _ => DayScenario.SickDay, // 3% sick
            };
        }

        return roll switch
        {
            < 50 => DayScenario.Normal, // 50% truly "normal" days with AID
            < 65 => DayScenario.HighDay, // 15% high days
            < 78 => DayScenario.LowDay, // 13% low days
            < 88 => DayScenario.Exercise, // 10% exercise
            < 94 => DayScenario.StressDay, // 6% stress
            < 98 => DayScenario.PoorSleep, // 4% poor sleep
            _ => DayScenario.SickDay, // 2% sick
        };
    }

    private (
        List<Entry> Entries,
        List<Treatment> Treatments,
        double EndingGlucose,
        double EndingMomentum
    ) GenerateDayData(
        DateTime date,
        DayScenario scenario,
        double? previousDayEndingGlucose = null,
        double previousDayMomentum = 0
    )
    {
        var entries = new List<Entry>();
        var treatments = new List<Treatment>();

        var scenarioParams = GetScenarioParameters(scenario);

        // Create oref profile for this scenario
        var orefProfile = CreateOrefProfile(scenarioParams);

        // Create oref physiology simulator for this day
        var simulator = new OrefPhysiologySimulator(
            _loggerFactory.CreateLogger<OrefPhysiologySimulator>(),
            orefProfile
        );

        // Start from previous day's ending glucose if available, otherwise use fasting glucose
        double glucose;
        if (previousDayEndingGlucose.HasValue)
        {
            glucose = previousDayEndingGlucose.Value;
        }
        else
        {
            glucose = scenarioParams.FastingGlucose + (_random.NextDouble() - 0.5) * 20;
        }

        var currentTime = date;
        // Cap endTime to now on the final day to prevent generating future data
        var now = DateTime.UtcNow;
        var endTime = date.Date == now.Date
            ? now
            : date.AddDays(1);

        var mealPlan = GenerateMealPlan(date, scenario);
        var basalAdjustments = GenerateBasalAdjustments(date, scenario);

        // Pre-populate insulin and carb events from meal plan into oref simulator
        foreach (var meal in mealPlan)
        {
            // Add carbs to simulator with absorption time based on glycemic index
            var absorptionHours = _config.CarbAbsorptionDurationMinutes / 60.0 / meal.GlycemicIndex;
            simulator.AddCarbs(meal.MealTime, meal.Carbs, absorptionHours);

            var bolusTime = meal.MealTime.AddMinutes(meal.BolusOffsetMinutes);
            var bolus = CalculateMealBolus(meal.Carbs, glucose, scenarioParams);
            simulator.AddInsulinDose(bolusTime, bolus);

            treatments.Add(CreateCarbTreatment(meal.MealTime, meal.Carbs, meal.FoodType));
            treatments.Add(
                CreateBolusTreatment(
                    bolusTime,
                    bolus,
                    meal.FoodType == "Snack" ? "Snack Bolus" : "Meal Bolus"
                )
            );
        }

        // Use previous day's momentum for continuity if available
        double glucoseMomentum = previousDayMomentum * 0.5;
        double lastGlucose = glucose;

        // Track IOB to prevent insulin stacking
        double estimatedIob = 0;
        var targetGlucose = _config.TargetGlucose;

        while (currentTime < endTime)
        {
            var basalAdj = basalAdjustments.FirstOrDefault(b =>
                Math.Abs((b.Time - currentTime).TotalMinutes) < 2.5
            );
            var adjustedRate = NormalizeBasalRate(basalAdj.Rate);
            if (adjustedRate > 0 || basalAdj.Duration > 0)
            {
                treatments.Add(
                    CreateTempBasalTreatment(currentTime, adjustedRate, basalAdj.Duration)
                );
                // Add temp basal to simulator
                simulator.AddInsulinDose(
                    currentTime,
                    adjustedRate * basalAdj.Duration / 60.0,
                    isTempBasal: true,
                    duration: basalAdj.Duration
                );
            }

            // Use oref simulator for glucose prediction
            glucose = SimulateGlucoseWithOref(
                glucose,
                currentTime,
                simulator,
                scenarioParams,
                scenario,
                ref glucoseMomentum
            );

            // Clamp to realistic CGM range
            glucose = Math.Max(40, Math.Min(_config.MaxGlucose, glucose));

            // Decay estimated IOB based on configured DIA
            // For 4-hour (240 min) DIA: decay ~2% per 5 min interval
            var iobDecayRate = 1.0 - (5.0 / _config.InsulinDurationMinutes);
            estimatedIob *= iobDecayRate;

            // === REACTIVE GLUCOSE MANAGEMENT (simulates AID/closed-loop behavior) ===

            var hour = currentTime.Hour;
            var isWakingHours = hour >= 7 && hour < 22; // 7am to 10pm
            var tempBasalDuration = _config.TempBasalDurationMinutes;

            // Handle LOW glucose - treat with fast carbs
            if (glucose < 70)
            {
                var correctionCarbs = glucose < 55 ? _random.Next(15, 25) : _random.Next(10, 18);
                treatments.Add(CreateCarbCorrectionTreatment(currentTime, correctionCarbs));
                simulator.AddCarbs(currentTime, correctionCarbs, 0.4); // Very fast carbs
            }
            // Handle HIGH glucose - aggressive AID-style insulin delivery
            // Real AID systems are aggressive about bringing glucose down
            else if (glucose > targetGlucose + 10)
            {
                // Calculate how much insulin would be needed to bring glucose to target
                var glucoseAboveTarget = glucose - targetGlucose;
                // Use ISF adjusted by scenario's insulin sensitivity multiplier
                var effectiveIsf =
                    _config.InsulinSensitivityFactor * scenarioParams.InsulinSensitivityMultiplier;
                var insulinNeeded = glucoseAboveTarget / effectiveIsf;

                // Account for IOB - don't stack insulin too much
                var insulinToDeliver = Math.Max(0, insulinNeeded - estimatedIob * 0.6);

                // HIGH TEMP BASAL - modest increase (typical AID systems use 1.0-1.5x)
                // Only add temp basal every 30 minutes to reduce basal contribution
                if (currentTime.Minute == 0 || currentTime.Minute == 30)
                {
                    // Scale from 1.1x to 1.4x basal rate based on how high glucose is
                    var tempBasalMultiplier = 1.1 + Math.Min(0.3, glucoseAboveTarget / 150.0);
                    var highTempRate = NormalizeBasalRate(_config.BasalRate * tempBasalMultiplier);

                    treatments.Add(
                        CreateTempBasalTreatment(currentTime, highTempRate, 30)
                    );
                    // Add to simulator for glucose effect (only the extra insulin above scheduled)
                    var extraInsulin = Math.Max(0, highTempRate - _config.BasalRate) * (30 / 60.0);
                    if (extraInsulin > 0)
                    {
                        simulator.AddInsulinDose(
                            currentTime,
                            extraInsulin,
                            isTempBasal: true,
                            duration: 30
                        );
                        estimatedIob += extraInsulin;
                    }
                }

                // MANUAL CORRECTION BOLUS - during waking hours, user may manually correct
                if (isWakingHours && glucose > targetGlucose + 30 && _random.NextDouble() < 0.25)
                {
                    // Manual correction uses exact ISF formula
                    var manualCorrectionBolus = glucoseAboveTarget / effectiveIsf;
                    manualCorrectionBolus = NormalizeBolus(
                        Math.Clamp(manualCorrectionBolus, 0.5, 6.0)
                    );

                    treatments.Add(
                        CreateManualCorrectionBolusTreatment(
                            currentTime,
                            manualCorrectionBolus
                        )
                    );
                    simulator.AddInsulinDose(currentTime, manualCorrectionBolus);
                    estimatedIob += manualCorrectionBolus;
                }
                // AID correction bolus every 5 minutes when significantly high
                else if (
                    glucose > targetGlucose + 15
                    && currentTime.Minute % 5 == 0
                    && insulinToDeliver > 0.1
                )
                {
                    // Deliver 50-70% of needed correction as a bolus
                    var correctionBolus = insulinToDeliver * (0.5 + _random.NextDouble() * 0.2);
                    correctionBolus = NormalizeBolus(Math.Clamp(correctionBolus, 0.1, 4.0));

                    treatments.Add(
                        CreateCorrectionBolusTreatment(currentTime, correctionBolus)
                    );
                    simulator.AddInsulinDose(currentTime, correctionBolus);
                    estimatedIob += correctionBolus;
                }
                // SMBs every 5 minutes for fine-tuning when moderately high
                else if (glucose > targetGlucose + 10 && insulinToDeliver > 0.05)
                {
                    var algorithmBolus = NormalizeBolus(
                        Math.Clamp(insulinToDeliver * 0.25, 0.05, 1.2)
                    );

                    if (algorithmBolus >= PumpBolusIncrementUnits)
                    {
                        treatments.Add(
                            CreateAlgorithmBolusTreatment(currentTime, algorithmBolus)
                        );
                    }
                    simulator.AddInsulinDose(currentTime, algorithmBolus);
                    estimatedIob += algorithmBolus;
                }
            }
            // Reduce basal when trending low or already low (predictive low glucose suspend)
            else if (glucose < 90 || (glucose < 100 && glucoseMomentum < -0.3))
            {
                // Basal reduction when lower
                var reductionFactor =
                    glucose < 75 ? 0.0
                    : glucose < 85 ? 0.2
                    : 0.4;
                var reducedRate = NormalizeBasalRate(_config.BasalRate * reductionFactor);

                // Apply reduced temp basal every 30 minutes
                if (currentTime.Minute == 0 || currentTime.Minute == 30)
                {
                    treatments.Add(
                        CreateTempBasalTreatment(currentTime, reducedRate, 30)
                    );
                    // Add to simulator for reduced basal effect (negative = less insulin)
                    var insulinReduction =
                        -(Math.Max(0, _config.BasalRate - reducedRate)) * (30 / 60.0);
                    simulator.AddInsulinDose(
                        currentTime,
                        insulinReduction,
                        isTempBasal: true,
                        duration: 30
                    );
                }
            }

            var delta = glucose - lastGlucose;
            entries.Add(CreateEntry(currentTime, glucose, delta));
            lastGlucose = glucose;

            currentTime = currentTime.AddMinutes(5);

            // Clean up expired doses in the simulator
            simulator.CleanupExpired(currentTime);
        }

        treatments.AddRange(GenerateScheduledBasal(date, scenarioParams));

        return (entries, treatments, glucose, glucoseMomentum);
    }

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
                InsulinSensitivityMultiplier = 0.85 + _random.NextDouble() * 0.1, // More moderate resistance
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
                InsulinSensitivityMultiplier = 0.75 + _random.NextDouble() * 0.1,
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
                scenario == DayScenario.LowDay ? _random.Next(15, 30)
                : scenario == DayScenario.HighDay ? _random.Next(35, 55)
                : _random.Next(25, 45);

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
            scenario == DayScenario.LowDay ? _random.Next(20, 40)
            : scenario == DayScenario.HighDay ? _random.Next(40, 65)
            : _random.Next(30, 50);

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
            scenario == DayScenario.LowDay ? _random.Next(25, 45)
            : scenario == DayScenario.HighDay ? _random.Next(45, 70)
            : _random.Next(35, 60);

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

    private List<Treatment> GenerateScheduledBasal(DateTime date, ScenarioParameters @params)
    {
        var basalTreatments = new List<Treatment>();

        for (var hour = 0; hour < 24; hour++)
        {
            var baseRate = _config.BasalRate * @params.BasalMultiplier;
            var circadianMultiplier = hour switch
            {
                >= 3 and < 8 => 1.0
                    + (@params.DawnPhenomenonStrength * (1 - Math.Abs(hour - 5.5) / 2.5)),
                >= 12 and < 14 => 1.1,
                >= 22 or < 3 => 0.9,
                _ => 1.0,
            };

            var rate = NormalizeBasalRate(baseRate * circadianMultiplier);
            var time = date.AddHours(hour);
            var mills = new DateTimeOffset(time).ToUnixTimeMilliseconds();

            basalTreatments.Add(
                new Treatment
                {
                    EventType = "Scheduled Basal",
                    Rate = rate,
                    Duration = 60,
                    Mills = mills,
                    Created_at = time.ToString("o"),
                    EnteredBy = "demo-pump",
                    DataSource = DataSources.DemoService,
                }
            );
        }

        return basalTreatments;
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
        if (carbCountingError < 0.10)
            estimatedCarbs = carbs * (0.85 + _random.NextDouble() * 0.05); // 85-90% - slight underestimate
        else if (carbCountingError < 0.90)
            estimatedCarbs = carbs * (0.95 + _random.NextDouble() * 0.1); // 95-105% - accurate
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

    private Treatment CreateCorrectionBolusTreatment(DateTime time, double insulin)
    {
        return new Treatment
        {
            EventType = "Correction Bolus",
            Insulin = insulin,
            Mills = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
            Created_at = time.ToString("o"),
            EnteredBy = "demo-pump", // AID pump delivers partial corrections
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
            EnteredBy = "demo-pump",
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
