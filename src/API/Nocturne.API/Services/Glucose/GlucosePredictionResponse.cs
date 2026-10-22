namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Response containing glucose predictions.
/// </summary>
public class GlucosePredictionResponse
{
    /// <summary>Timestamp when predictions were calculated</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Current blood glucose (mg/dL)</summary>
    public double CurrentBg { get; set; }

    /// <summary>Rate of glucose change (mg/dL per 5 min)</summary>
    public double Delta { get; set; }

    /// <summary>Eventual blood glucose if trend continues (mg/dL)</summary>
    public double EventualBg { get; set; }

    /// <summary>Current insulin on board (U)</summary>
    public double Iob { get; set; }

    /// <summary>Current carbs on board (g)</summary>
    public double Cob { get; set; }

    /// <summary>Sensitivity ratio used (1.0 = normal)</summary>
    public double? SensitivityRatio { get; set; }

    /// <summary>Prediction interval in minutes</summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>Prediction curves with different scenarios</summary>
    public PredictionCurves Predictions { get; set; } = new();
}

/// <summary>
/// Different prediction curves for visualization.
/// </summary>
public class PredictionCurves
{
    /// <summary>Main prediction curve (mg/dL values at 5-min intervals)</summary>
    public List<double>? Default { get; set; }

    /// <summary>IOB-only prediction (ignoring COB)</summary>
    public List<double>? IobOnly { get; set; }

    /// <summary>UAM (Unannounced Meal) prediction</summary>
    public List<double>? Uam { get; set; }

    /// <summary>COB-based prediction</summary>
    public List<double>? Cob { get; set; }

    /// <summary>Zero-temp prediction (what happens if basal stops)</summary>
    public List<double>? ZeroTemp { get; set; }
}
