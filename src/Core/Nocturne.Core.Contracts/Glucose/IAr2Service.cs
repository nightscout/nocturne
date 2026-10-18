using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Glucose;

/// <summary>
/// Service interface for AR2 forecasting with 1:1 legacy JavaScript compatibility.
/// </summary>
/// <seealso cref="DData"/>
/// <seealso cref="Ar2Properties"/>
/// <seealso cref="ForecastPoint"/>
public interface IAr2Service
{
    /// <summary>
    /// Calculate AR2 forecast for glucose predictions
    /// </summary>
    /// <param name="ddata">Data containing recent glucose readings</param>
    /// <param name="deltaProperties">Delta properties including mean5MinsAgo</param>
    /// <param name="settings">User settings including thresholds</param>
    /// <returns>AR2 forecast properties</returns>
    Task<Ar2Properties> CalculateForecastAsync(
        DData ddata,
        Dictionary<string, object> bgNowProperties,
        Dictionary<string, object> deltaProperties,
        Dictionary<string, object> settings,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Generate forecast cone points for visualization
    /// </summary>
    /// <param name="ddata">Data containing recent glucose readings</param>
    /// <param name="deltaProperties">Delta properties including mean5MinsAgo</param>
    /// <param name="coneFactor">Cone factor for prediction uncertainty (default 2.0)</param>
    /// <returns>List of forecast cone points</returns>
    Task<List<ForecastPoint>> GenerateForecastConeAsync(
        DData ddata,
        Dictionary<string, object> bgNowProperties,
        Dictionary<string, object> deltaProperties,
        double coneFactor = 2.0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Check if conditions are suitable for forecasting
    /// </summary>
    /// <returns>True if forecasting is possible</returns>
    bool CanForecast(
        Dictionary<string, object> bgNowProperties,
        Dictionary<string, object> deltaProperties
    );
}
