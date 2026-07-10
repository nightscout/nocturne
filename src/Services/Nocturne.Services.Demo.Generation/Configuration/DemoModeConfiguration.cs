namespace Nocturne.Services.Demo.Configuration;

/// <summary>
/// Configuration for demo data generation.
/// </summary>
public class DemoModeConfiguration
{
    public const string SectionName = "DemoMode";

    /// <summary>
    /// Whether demo mode is enabled. Default is true for the demo service.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval between generating new glucose readings in minutes.
    /// </summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Interval in minutes between full demo data resets. Set to 0 to disable.
    /// </summary>
    public int ResetIntervalMinutes { get; set; } = 0;

    /// <summary>
    /// Initial glucose value in mg/dL.
    /// </summary>
    public int InitialGlucose { get; set; } = 120;

    /// <summary>
    /// Maximum variance in glucose change per interval.
    /// </summary>
    public int WalkVariance { get; set; } = 15;

    /// <summary>
    /// Minimum glucose value in mg/dL.
    /// </summary>
    public int MinGlucose { get; set; } = 70;

    /// <summary>
    /// Maximum glucose value in mg/dL.
    /// </summary>
    public int MaxGlucose { get; set; } = 250;

    /// <summary>
    /// Device identifier for demo entries.
    /// </summary>
    public string Device { get; set; } = "demo-cgm";

    /// <summary>
    /// Number of days of historical data to generate on startup.
    /// </summary>
    public int BackfillDays { get; set; } = 90;

    /// <summary>
    /// Base basal rate for insulin calculations (U/hr).
    /// </summary>
    public double BasalRate { get; set; } = 1.0;

    /// <summary>
    /// Target glucose level in mg/dL for the AID system to aim for.
    /// </summary>
    public double TargetGlucose { get; set; } = 110.0;

    /// <summary>
    /// Insulin-to-carb ratio (grams of carbs per 1 unit of insulin).
    /// Average adult T1D: 8-12 grams per unit.
    /// </summary>
    public double CarbRatio { get; set; } = 10.0;

    // Pharmacokinetic parameters for realistic simulation

    /// <summary>
    /// Time in minutes when rapid-acting insulin peaks.
    /// </summary>
    public double InsulinPeakMinutes { get; set; } = 75.0;

    /// <summary>
    /// Total duration of insulin action (DIA) in minutes.
    /// </summary>
    public double InsulinDurationMinutes { get; set; } = 180.0;

    /// <summary>
    /// Time in minutes when carb absorption peaks.
    /// </summary>
    public double CarbAbsorptionPeakMinutes { get; set; } = 45.0;

    /// <summary>
    /// Total carb absorption time in minutes.
    /// </summary>
    public double CarbAbsorptionDurationMinutes { get; set; } = 180.0;

    /// <summary>
    /// Insulin sensitivity factor (ISF) - mg/dL drop per unit of insulin.
    /// This is the single source of truth for correction calculations.
    /// Average adult T1D: 30-50 mg/dL per unit.
    /// </summary>
    public double InsulinSensitivityFactor { get; set; } = 40.0;

    /// <summary>
    /// Duration of temp basal adjustments in minutes.
    /// AID systems typically use 5-30 minute intervals.
    /// </summary>
    public int TempBasalDurationMinutes { get; set; } = 10;

    /// <summary>
    /// Whether to clear existing demo data on service startup.
    /// </summary>
    public bool ClearOnStartup { get; set; } = true;

    /// <summary>
    /// Whether to regenerate historical data on service startup.
    /// </summary>
    public bool RegenerateOnStartup { get; set; } = true;
}
