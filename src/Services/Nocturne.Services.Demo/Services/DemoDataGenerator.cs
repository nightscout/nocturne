using Microsoft.Extensions.Options;
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
    /// Generates historical data for the configured time period.
    /// </summary>
    (List<Entry> Entries, List<Treatment> Treatments) GenerateHistoricalData();
}

/// <summary>
/// Generates realistic demo CGM and treatment data using pharmacokinetic models.
/// </summary>
public class DemoDataGenerator : IDemoDataGenerator
{
    private readonly ILogger<DemoDataGenerator> _logger;
    private readonly DemoModeConfiguration _config;
    private readonly Random _random = new();
    private double _currentGlucose;
    private readonly object _lock = new();

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
        ILogger<DemoDataGenerator> logger
    )
    {
        _logger = logger;
        _config = config.Value;
        _currentGlucose = _config.InitialGlucose;
    }

    public DemoModeConfiguration GetConfiguration() => _config;

    public Entry GenerateCurrentEntry()
    {
        lock (_lock)
        {
            var change = GenerateRandomWalk();
            _currentGlucose = Math.Max(
                _config.MinGlucose,
                Math.Min(_config.MaxGlucose, _currentGlucose + change)
            );

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
                IsDemo = true,
                Filtered = Math.Round(_currentGlucose + (_random.NextDouble() - 0.5) * 2, 0),
                Unfiltered = Math.Round(_currentGlucose + (_random.NextDouble() - 0.5) * 5, 0),
                Rssi = _random.Next(0, 101),
                Noise = _random.Next(0, 5),
                CreatedAt = now.ToString("o"),
                ModifiedAt = now,
            };
        }
    }

    public (List<Entry> Entries, List<Treatment> Treatments) GenerateHistoricalData()
    {
        var endDate = DateTime.UtcNow;
        var startDate = endDate.AddDays(-_config.HistoryDays);

        var entries = new List<Entry>();
        var treatments = new List<Treatment>();

        _logger.LogInformation(
            "Generating historical demo data from {StartDate} to {EndDate}",
            startDate,
            endDate
        );

        var currentDay = startDate.Date;
        while (currentDay <= endDate.Date)
        {
            var dayScenario = SelectDayScenario(currentDay);
            var (dayEntries, dayTreatments) = GenerateDayData(currentDay, dayScenario);

            entries.AddRange(dayEntries);
            treatments.AddRange(dayTreatments);

            currentDay = currentDay.AddDays(1);
        }

        _logger.LogInformation(
            "Generated {EntryCount} entries and {TreatmentCount} treatments",
            entries.Count,
            treatments.Count
        );

        return (entries, treatments);
    }

    private DayScenario SelectDayScenario(DateTime date)
    {
        var roll = _random.Next(100);
        var isWeekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;

        if (isWeekend)
        {
            return roll switch
            {
                < 40 => DayScenario.Normal,
                < 55 => DayScenario.HighDay,
                < 65 => DayScenario.Exercise,
                < 75 => DayScenario.PoorSleep,
                < 85 => DayScenario.LowDay,
                < 95 => DayScenario.StressDay,
                _ => DayScenario.SickDay,
            };
        }

        return roll switch
        {
            < 60 => DayScenario.Normal,
            < 70 => DayScenario.HighDay,
            < 78 => DayScenario.LowDay,
            < 86 => DayScenario.Exercise,
            < 92 => DayScenario.StressDay,
            < 97 => DayScenario.PoorSleep,
            _ => DayScenario.SickDay,
        };
    }

    private (List<Entry> Entries, List<Treatment> Treatments) GenerateDayData(
        DateTime date,
        DayScenario scenario
    )
    {
        var entries = new List<Entry>();
        var treatments = new List<Treatment>();

        var scenarioParams = GetScenarioParameters(scenario);
        var glucose = scenarioParams.FastingGlucose + (_random.NextDouble() - 0.5) * 20;

        var currentTime = date;
        var endTime = date.AddDays(1);

        var mealPlan = GenerateMealPlan(date, scenario);
        var basalAdjustments = GenerateBasalAdjustments(date, scenario);

        var insulinEvents = new List<(DateTime Time, double Units)>();
        var carbEvents = new List<(DateTime Time, double Carbs, double GlycemicIndex)>();

        // Pre-populate insulin and carb events from meal plan
        foreach (var meal in mealPlan)
        {
            carbEvents.Add((meal.MealTime, meal.Carbs, meal.GlycemicIndex));

            var bolusTime = meal.MealTime.AddMinutes(meal.BolusOffsetMinutes);
            var bolus = CalculateMealBolus(meal.Carbs, glucose, scenarioParams);
            insulinEvents.Add((bolusTime, bolus));

            treatments.Add(CreateCarbTreatment(meal.MealTime, meal.Carbs, meal.FoodType));
            treatments.Add(
                CreateBolusTreatment(
                    bolusTime,
                    bolus,
                    meal.FoodType == "Snack" ? "Snack Bolus" : "Meal Bolus"
                )
            );
        }

        double glucoseMomentum = 0;
        double lastGlucose = glucose;

        while (currentTime < endTime)
        {
            var basalAdj = basalAdjustments.FirstOrDefault(b =>
                Math.Abs((b.Time - currentTime).TotalMinutes) < 2.5
            );
            if (basalAdj.Rate > 0 || basalAdj.Duration > 0)
            {
                treatments.Add(
                    CreateTempBasalTreatment(currentTime, basalAdj.Rate, basalAdj.Duration)
                );
            }

            glucose = SimulateGlucosePhysiological(
                glucose,
                currentTime,
                insulinEvents,
                carbEvents,
                scenarioParams,
                scenario,
                ref glucoseMomentum
            );

            glucose = Math.Max(40, Math.Min(400, glucose));

            // Handle low glucose correction
            if (glucose < 70 && _random.NextDouble() < 0.7)
            {
                var correctionCarbs = _random.Next(12, 20);
                treatments.Add(CreateCarbCorrectionTreatment(currentTime, correctionCarbs));
                carbEvents.Add((currentTime, correctionCarbs, 1.5));
            }

            // Handle high glucose correction
            if (glucose > 200 && _random.NextDouble() < 0.4)
            {
                var correctionBolus = Math.Round(
                    (glucose - 120) / scenarioParams.CorrectionFactor,
                    1
                );
                if (correctionBolus >= 0.5)
                {
                    treatments.Add(CreateCorrectionBolusTreatment(currentTime, correctionBolus));
                    insulinEvents.Add((currentTime, correctionBolus));
                }
            }

            var delta = glucose - lastGlucose;
            entries.Add(CreateEntry(currentTime, glucose, delta));
            lastGlucose = glucose;

            currentTime = currentTime.AddMinutes(5);

            // Clean up old events
            insulinEvents.RemoveAll(e =>
                (currentTime - e.Time).TotalMinutes > _config.InsulinDurationMinutes + 30
            );
            carbEvents.RemoveAll(e =>
                (currentTime - e.Time).TotalMinutes > _config.CarbAbsorptionDurationMinutes + 30
            );
        }

        treatments.AddRange(GenerateScheduledBasal(date, scenarioParams));

        return (entries, treatments);
    }

    private double SimulateGlucosePhysiological(
        double currentGlucose,
        DateTime time,
        List<(DateTime Time, double Units)> insulinEvents,
        List<(DateTime Time, double Carbs, double GlycemicIndex)> carbEvents,
        ScenarioParameters @params,
        DayScenario scenario,
        ref double momentum
    )
    {
        var glucose = currentGlucose;
        var hour = time.Hour + time.Minute / 60.0;

        var insulinActivity = CalculateInsulinActivity(time, insulinEvents, @params);
        var (_, carbAbsorptionRate) = CalculateCarbEffect(time, carbEvents, @params);

        var basalEffect = @params.BasalMultiplier * 0.02;
        var homeostasis = (100 - glucose) * 0.002;

        var dawnEffect = 0.0;
        if (hour >= 3 && hour < 8)
        {
            dawnEffect = @params.DawnPhenomenonStrength * 0.8 * Math.Sin((hour - 3) * Math.PI / 5);
        }

        var exerciseEffect = 0.0;
        if (@params.HasExercise)
        {
            if (hour >= 16 && hour < 18)
                exerciseEffect = -0.4;
            else if (hour >= 18 && hour < 22)
                exerciseEffect = -0.1;
        }

        var netChange =
            carbAbsorptionRate
            - insulinActivity
            + basalEffect
            + homeostasis
            + dawnEffect
            + exerciseEffect;
        var noise = (_random.NextDouble() - 0.5) * 1.5;

        var targetChange = netChange + noise;
        momentum = momentum * 0.7 + targetChange * 0.3;
        glucose += momentum;

        if (scenario == DayScenario.SickDay)
            glucose += (_random.NextDouble() - 0.3) * 1.5;
        else if (scenario == DayScenario.StressDay && _random.NextDouble() < 0.05)
            glucose += _random.Next(5, 15);

        return glucose;
    }

    private double CalculateInsulinActivity(
        DateTime currentTime,
        List<(DateTime Time, double Units)> insulinEvents,
        ScenarioParameters @params
    )
    {
        double totalActivity = 0;
        var peakTime = _config.InsulinPeakMinutes;
        var dia = _config.InsulinDurationMinutes;

        foreach (var (eventTime, units) in insulinEvents)
        {
            var minutesSince = (currentTime - eventTime).TotalMinutes;
            if (minutesSince < 0 || minutesSince > dia)
                continue;

            var tau = peakTime / 1.4;
            var S = 1 / (1 - tau / dia + (1 + tau / dia) * Math.Exp(-dia / tau));
            var activity = S * (minutesSince / (tau * tau)) * Math.Exp(-minutesSince / tau);
            var glucoseEffect =
                activity
                * units
                * @params.InsulinSensitivityMultiplier
                * (_config.InsulinSensitivityFactor / 60.0);
            totalActivity += glucoseEffect;
        }

        return totalActivity;
    }

    private (double COB, double AbsorptionRate) CalculateCarbEffect(
        DateTime currentTime,
        List<(DateTime Time, double Carbs, double GlycemicIndex)> carbEvents,
        ScenarioParameters @params
    )
    {
        double totalCOB = 0;
        double totalAbsorptionRate = 0;
        var peakTime = _config.CarbAbsorptionPeakMinutes;
        var duration = _config.CarbAbsorptionDurationMinutes;

        foreach (var (eventTime, carbs, gi) in carbEvents)
        {
            var minutesSince = (currentTime - eventTime).TotalMinutes;
            if (minutesSince < 0 || minutesSince > duration * gi)
                continue;

            var adjustedPeak = peakTime / gi;
            var adjustedDuration = duration / gi;
            var k = 2.0;

            if (minutesSince > 0)
            {
                var normalizedTime = minutesSince / adjustedPeak;
                var absorptionRate =
                    Math.Pow(normalizedTime, k - 1) * Math.Exp(-normalizedTime * (k - 1));
                var absorbed = carbs * (1 - Math.Exp(-minutesSince / (adjustedDuration / 3)));
                var remaining = Math.Max(0, carbs - absorbed);

                totalCOB += remaining;
                var glucosePerCarb = 3.5 / @params.InsulinSensitivityMultiplier;
                totalAbsorptionRate +=
                    absorptionRate * (carbs / adjustedDuration) * glucosePerCarb * gi;
            }
        }

        return (totalCOB, totalAbsorptionRate);
    }

    private ScenarioParameters GetScenarioParameters(DayScenario scenario)
    {
        return scenario switch
        {
            DayScenario.Normal => new ScenarioParameters
            {
                FastingGlucose = 100 + _random.Next(-15, 20),
                CarbRatio = _config.CarbRatio,
                CorrectionFactor = _config.CorrectionFactor,
                BasalMultiplier = 1.0,
                InsulinSensitivityMultiplier = 1.0,
                DawnPhenomenonStrength = 0.3,
            },
            DayScenario.HighDay => new ScenarioParameters
            {
                FastingGlucose = 140 + _random.Next(0, 40),
                CarbRatio = _config.CarbRatio * 0.8,
                CorrectionFactor = _config.CorrectionFactor * 0.8,
                BasalMultiplier = 1.2,
                InsulinSensitivityMultiplier = 0.7,
                DawnPhenomenonStrength = 0.5,
            },
            DayScenario.LowDay => new ScenarioParameters
            {
                FastingGlucose = 80 + _random.Next(-10, 15),
                CarbRatio = _config.CarbRatio * 1.3,
                CorrectionFactor = _config.CorrectionFactor * 1.3,
                BasalMultiplier = 0.7,
                InsulinSensitivityMultiplier = 1.4,
                DawnPhenomenonStrength = 0.1,
            },
            DayScenario.Exercise => new ScenarioParameters
            {
                FastingGlucose = 95 + _random.Next(-10, 15),
                CarbRatio = _config.CarbRatio * 1.2,
                CorrectionFactor = _config.CorrectionFactor * 1.3,
                BasalMultiplier = 0.6,
                InsulinSensitivityMultiplier = 1.5,
                DawnPhenomenonStrength = 0.2,
                HasExercise = true,
            },
            DayScenario.SickDay => new ScenarioParameters
            {
                FastingGlucose = 160 + _random.Next(0, 60),
                CarbRatio = _config.CarbRatio * 0.6,
                CorrectionFactor = _config.CorrectionFactor * 0.6,
                BasalMultiplier = 1.5,
                InsulinSensitivityMultiplier = 0.5,
                DawnPhenomenonStrength = 0.6,
            },
            DayScenario.StressDay => new ScenarioParameters
            {
                FastingGlucose = 120 + _random.Next(0, 30),
                CarbRatio = _config.CarbRatio * 0.85,
                CorrectionFactor = _config.CorrectionFactor * 0.85,
                BasalMultiplier = 1.15,
                InsulinSensitivityMultiplier = 0.8,
                DawnPhenomenonStrength = 0.4,
            },
            DayScenario.PoorSleep => new ScenarioParameters
            {
                FastingGlucose = 130 + _random.Next(-10, 30),
                CarbRatio = _config.CarbRatio * 0.9,
                CorrectionFactor = _config.CorrectionFactor * 0.9,
                BasalMultiplier = 1.1,
                InsulinSensitivityMultiplier = 0.85,
                DawnPhenomenonStrength = 0.5,
            },
            _ => new ScenarioParameters
            {
                FastingGlucose = 100,
                CarbRatio = _config.CarbRatio,
                CorrectionFactor = _config.CorrectionFactor,
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

        // Breakfast
        var breakfastHour = 6 + _random.Next(0, 4);
        var breakfastMinute = _random.Next(0, 12) * 5;
        var breakfastCarbs =
            scenario == DayScenario.LowDay ? _random.Next(20, 40)
            : scenario == DayScenario.HighDay ? _random.Next(50, 80)
            : _random.Next(30, 60);
        meals.Add(
            new MealEvent(
                date.AddHours(breakfastHour).AddMinutes(breakfastMinute),
                breakfastCarbs,
                "Breakfast",
                _random.Next(-15, 5),
                1.0 + (_random.NextDouble() - 0.5) * 0.4
            )
        );

        // Lunch
        var lunchHour = 11 + _random.Next(0, 3);
        var lunchMinute = _random.Next(0, 12) * 5;
        var lunchCarbs =
            scenario == DayScenario.LowDay ? _random.Next(30, 50)
            : scenario == DayScenario.HighDay ? _random.Next(60, 100)
            : _random.Next(40, 70);
        meals.Add(
            new MealEvent(
                date.AddHours(lunchHour).AddMinutes(lunchMinute),
                lunchCarbs,
                "Lunch",
                _random.Next(-10, 20),
                1.0 + (_random.NextDouble() - 0.5) * 0.3
            )
        );

        // Dinner
        var dinnerHour = 17 + _random.Next(0, 4);
        var dinnerMinute = _random.Next(0, 12) * 5;
        var dinnerCarbs =
            scenario == DayScenario.LowDay ? _random.Next(35, 55)
            : scenario == DayScenario.HighDay ? _random.Next(70, 120)
            : _random.Next(50, 90);
        meals.Add(
            new MealEvent(
                date.AddHours(dinnerHour).AddMinutes(dinnerMinute),
                dinnerCarbs,
                "Dinner",
                _random.NextDouble() < 0.4 ? _random.Next(-10, 5) : _random.Next(5, 25),
                1.0 + (_random.NextDouble() - 0.5) * 0.5
            )
        );

        // Snacks
        if (_random.NextDouble() < 0.5)
        {
            meals.Add(
                new MealEvent(
                    date.AddHours(10 + _random.NextDouble()),
                    _random.Next(10, 25),
                    "Snack",
                    _random.Next(0, 15),
                    1.2
                )
            );
        }

        if (_random.NextDouble() < 0.5)
        {
            meals.Add(
                new MealEvent(
                    date.AddHours(15 + _random.NextDouble()),
                    _random.Next(10, 25),
                    "Snack",
                    _random.Next(0, 15),
                    1.2
                )
            );
        }

        if (_random.NextDouble() < 0.3)
        {
            meals.Add(
                new MealEvent(
                    date.AddHours(21 + _random.NextDouble()),
                    _random.Next(10, 20),
                    "Snack",
                    _random.Next(0, 10),
                    1.0
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
            adjustments.Add((date.AddHours(exerciseHour - 1), _config.BasalRate * 0.5, 120));
        }

        if (scenario == DayScenario.LowDay && _random.NextDouble() < 0.5)
        {
            var lowHour = _random.Next(10, 16);
            adjustments.Add((date.AddHours(lowHour), _config.BasalRate * 0.6, 60));
        }

        if (scenario == DayScenario.HighDay && _random.NextDouble() < 0.5)
        {
            var highHour = _random.Next(10, 18);
            adjustments.Add((date.AddHours(highHour), _config.BasalRate * 1.3, 120));
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

            var rate = Math.Round(baseRate * circadianMultiplier, 2);
            var time = date.AddHours(hour);
            var mills = new DateTimeOffset(time).ToUnixTimeMilliseconds();

            basalTreatments.Add(
                new Treatment
                {
                    EventType = "Temp Basal",
                    Rate = rate,
                    Duration = 60,
                    Mills = mills,
                    Created_at = time.ToString("o"),
                    EnteredBy = "demo-pump",
                    IsDemo = true,
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
        var carbBolus = carbs / @params.CarbRatio;
        var correctionBolus =
            currentGlucose > 120 ? (currentGlucose - 120) / @params.CorrectionFactor : 0.0;
        var variation = 1.0 + (_random.NextDouble() - 0.5) * 0.2;
        return Math.Round((carbBolus + correctionBolus) * variation, 1);
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
            IsDemo = true,
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
            IsDemo = true,
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
            IsDemo = true,
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
            EnteredBy = "demo-user",
            IsDemo = true,
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
            IsDemo = true,
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
            IsDemo = true,
        };
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
        public double CorrectionFactor { get; set; }
        public double BasalMultiplier { get; set; }
        public double InsulinSensitivityMultiplier { get; set; }
        public double DawnPhenomenonStrength { get; set; }
        public bool HasExercise { get; set; }
    }
}
