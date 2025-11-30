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
    /// Initial glucose value in mg/dL.
    /// </summary>
    public int InitialGlucose { get; set; } = 120;

    /// <summary>
    /// Maximum variance in glucose change per interval.
    /// </summary>
    public int WalkVariance { get; set; } = 10;

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
    public int HistoryDays { get; set; } = 90;

    /// <summary>
    /// Base basal rate for insulin calculations.
    /// </summary>
    public double BasalRate { get; set; } = 1.0;

    /// <summary>
    /// Insulin-to-carb ratio.
    /// </summary>
    public double CarbRatio { get; set; } = 10.0;

    /// <summary>
    /// Correction factor (insulin sensitivity).
    /// </summary>
    public double CorrectionFactor { get; set; } = 50.0;

    // Pharmacokinetic parameters for realistic simulation

    /// <summary>
    /// Time in minutes when rapid-acting insulin peaks.
    /// </summary>
    public double InsulinPeakMinutes { get; set; } = 75.0;

    /// <summary>
    /// Total duration of insulin action (DIA) in minutes.
    /// </summary>
    public double InsulinDurationMinutes { get; set; } = 240.0;

    /// <summary>
    /// Time in minutes when carb absorption peaks.
    /// </summary>
    public double CarbAbsorptionPeakMinutes { get; set; } = 45.0;

    /// <summary>
    /// Total carb absorption time in minutes.
    /// </summary>
    public double CarbAbsorptionDurationMinutes { get; set; } = 180.0;

    /// <summary>
    /// Insulin sensitivity factor - mg/dL drop per unit of insulin.
    /// </summary>
    public double InsulinSensitivityFactor { get; set; } = 50.0;

    /// <summary>
    /// Whether to clear existing demo data on service startup.
    /// </summary>
    public bool ClearOnStartup { get; set; } = true;

    /// <summary>
    /// Whether to regenerate historical data on service startup.
    /// </summary>
    public bool RegenerateOnStartup { get; set; } = true;
}
