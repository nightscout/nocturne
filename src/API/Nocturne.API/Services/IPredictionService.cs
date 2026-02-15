using Nocturne.API.Controllers.V4;

namespace Nocturne.API.Services;

/// <summary>
/// Service interface for glucose predictions using oref algorithms.
/// </summary>
public interface IPredictionService
{
    /// <summary>
    /// Get glucose predictions based on current data.
    /// </summary>
    /// <param name="profileId">Optional profile ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Glucose prediction response</returns>
    /// <exception cref="InvalidOperationException">Thrown when no glucose readings or device status data are available.</exception>
    Task<GlucosePredictionResponse> GetPredictionsAsync(
        string? profileId = null,
        CancellationToken cancellationToken = default);
}
