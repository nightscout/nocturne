namespace Nocturne.Core.Contracts.Devices;

/// <summary>
/// Service interface for oref (OpenAPS Reference Implementation) algorithms.
/// Provides unified access to IOB, COB, autosens, and determine-basal calculations
/// via the Rust oref library compiled to WebAssembly.
/// </summary>
/// <seealso cref="Treatments.IIobCalculator"/>
/// <seealso cref="IOpenApsService"/>
public interface IOrefService
{
    /// <summary>
    /// Calculate Insulin on Board (IOB) from treatment history.
    /// </summary>
    /// <param name="profile">User profile settings</param>
    /// <param name="treatments">Treatment history (boluses, temp basals)</param>
    /// <param name="time">Time for calculation (Unix milliseconds). Defaults to now.</param>
    /// <param name="currentOnly">If true, only calculate current IOB (faster)</param>
    /// <returns>IOB calculation results</returns>
    Task<OrefIobResult[]> CalculateIobAsync(
        OrefProfile profile,
        IEnumerable<OrefTreatment> treatments,
        long? time = null,
        bool currentOnly = true
    );

    /// <summary>
    /// Calculate Carbs on Board (COB) from glucose and treatment history.
    /// </summary>
    /// <param name="profile">User profile settings</param>
    /// <param name="glucose">Recent glucose readings (most recent first)</param>
    /// <param name="treatments">Treatment history</param>
    /// <param name="time">Time for calculation (Unix milliseconds). Defaults to now.</param>
    /// <returns>COB calculation result</returns>
    Task<OrefCobResult> CalculateCobAsync(
        OrefProfile profile,
        IEnumerable<OrefGlucoseReading> glucose,
        IEnumerable<OrefTreatment> treatments,
        long? time = null
    );

    /// <summary>
    /// Calculate autosens ratio from glucose history.
    /// Detects changes in insulin sensitivity over time.
    /// </summary>
    /// <param name="profile">User profile settings</param>
    /// <param name="glucose">24-hour glucose history</param>
    /// <param name="treatments">Treatment history</param>
    /// <param name="time">Time for calculation (Unix milliseconds). Defaults to now.</param>
    /// <returns>Autosens result with sensitivity ratio</returns>
    Task<OrefAutosensResult> CalculateAutosensAsync(
        OrefProfile profile,
        IEnumerable<OrefGlucoseReading> glucose,
        IEnumerable<OrefTreatment> treatments,
        long? time = null
    );

    /// <summary>
    /// Run the full determine-basal algorithm.
    /// Returns temp basal and SMB recommendations.
    /// </summary>
    /// <param name="inputs">Complete inputs for the algorithm</param>
    /// <returns>Basal recommendation result</returns>
    Task<OrefDetermineBasalResult> DetermineBasalAsync(OrefDetermineBasalInputs inputs);

    /// <summary>
    /// Calculate glucose status (deltas) from readings.
    /// </summary>
    /// <param name="glucose">Recent glucose readings (most recent first)</param>
    /// <returns>Glucose status with deltas</returns>
    Task<OrefGlucoseStatus> CalculateGlucoseStatusAsync(IEnumerable<OrefGlucoseReading> glucose);

    /// <summary>
    /// Check if the oref service is available and functioning.
    /// </summary>
    /// <returns>True if service is healthy</returns>
    Task<bool> HealthCheckAsync();

    /// <summary>
    /// Get the oref library version.
    /// </summary>
    string Version { get; }
}
