namespace Nocturne.Core.Models;

/// <summary>
/// AR2 forecast properties with 1:1 legacy compatibility.
/// AR2 (Auto-Regressive model of order 2) predicts future glucose values from the two most recent readings.
/// </summary>
/// <seealso cref="Ar2Forecast"/>
/// <seealso cref="Ar2State"/>
/// <seealso cref="ForecastPoint"/>
public class Ar2Properties
{
    /// <summary>
    /// Forecast results containing predictions and average loss
    /// </summary>
    public Ar2Forecast? Forecast { get; set; }

    /// <summary>
    /// Alert level based on prediction loss
    /// </summary>
    public string? Level { get; set; }

    /// <summary>
    /// Event name for notifications (High, Low, or empty)
    /// </summary>
    public string? EventName { get; set; }

    /// <summary>
    /// Display line showing 15-minute prediction
    /// </summary>
    public string? DisplayLine { get; set; }
}

/// <summary>
/// AR2 forecast calculation results containing predicted glucose points and an average loss metric.
/// </summary>
/// <seealso cref="Ar2Properties"/>
/// <seealso cref="ForecastPoint"/>
public class Ar2Forecast
{
    /// <summary>
    /// Predicted glucose points
    /// </summary>
    public List<ForecastPoint> Predicted { get; set; } = new();

    /// <summary>
    /// Average loss calculation for alarm thresholds
    /// </summary>
    public double AvgLoss { get; set; }
}

public class ForecastPoint
{
    /// <summary>
    /// Timestamp in milliseconds
    /// </summary>
    public long Mills { get; set; }

    /// <summary>
    /// Predicted glucose value in mg/dL
    /// </summary>
    public int Mgdl { get; set; }

    public string Color { get; set; } = "cyan";
}

/// <summary>
/// AR2 calculation state
/// </summary>
public class Ar2State
{
    /// <summary>
    /// Forecast time in milliseconds
    /// </summary>
    public long ForecastTime { get; set; }

    public List<ForecastPoint> Points { get; set; } = new();

    /// <summary>
    /// Previous AR2 value
    /// </summary>
    public double Prev { get; set; }

    /// <summary>
    /// Current AR2 value
    /// </summary>
    public double Curr { get; set; }
}
