using Nocturne.Core.Models;

namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
/// Service for submitting data directly to the Nocturne API via HTTP
/// </summary>
public interface IApiDataSubmitter
{
    /// <summary>
    /// Submit glucose entries to the API
    /// </summary>
    /// <param name="entries">Glucose entries to submit</param>
    /// <param name="source">Source connector identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if submission was successful</returns>
    Task<bool> SubmitEntriesAsync(
        IEnumerable<Entry> entries,
        string source,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Submit treatments to the API
    /// </summary>
    /// <param name="treatments">Treatments to submit</param>
    /// <param name="source">Source connector identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if submission was successful</returns>
    Task<bool> SubmitTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string source,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Submit device status to the API
    /// </summary>
    /// <param name="deviceStatuses">Device statuses to submit</param>
    /// <param name="source">Source connector identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if submission was successful</returns>
    Task<bool> SubmitDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        string source,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Submit profiles to the API
    /// </summary>
    /// <param name="profiles">Profiles to submit</param>
    /// <param name="source">Source connector identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if submission was successful</returns>
    Task<bool> SubmitProfilesAsync(
        IEnumerable<Profile> profiles,
        string source,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Submit food items to the API
    /// </summary>
    /// <param name="foods">Food items to submit</param>
    /// <param name="source">Source connector identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if submission was successful</returns>
    Task<bool> SubmitFoodAsync(
        IEnumerable<Food> foods,
        string source,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Submit activity events to the API
    /// </summary>
    /// <param name="activities">Activity events to submit</param>
    /// <param name="source">Source connector identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if submission was successful</returns>
    Task<bool> SubmitActivityAsync(
        IEnumerable<Activity> activities,
        string source,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the timestamp of the latest entry for a specific data source
    /// This enables "catch up" sync functionality to fetch only new data since the last upload
    /// </summary>
    /// <param name="source">Source connector identifier (e.g., "dexcom-connector", "libre-connector")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The timestamp of the latest entry, or null if no entries exist for this source</returns>
    Task<DateTime?> GetLatestEntryTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the timestamp of the latest treatment for a specific data source
    /// This enables "catch up" sync functionality to fetch only new data since the last upload
    /// </summary>
    /// <param name="source">Source connector identifier (e.g., "dexcom-connector", "libre-connector")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The timestamp of the latest treatment, or null if no treatments exist for this source</returns>
    Task<DateTime?> GetLatestTreatmentTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Submit state spans to the API (pump modes, connectivity, profiles, overrides)
    /// </summary>
    /// <param name="stateSpans">State spans to submit</param>
    /// <param name="source">Source connector identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if submission was successful</returns>
    Task<bool> SubmitStateSpansAsync(
        IEnumerable<StateSpan> stateSpans,
        string source,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Submit system events to the API (alarms, warnings, info)
    /// </summary>
    /// <param name="systemEvents">System events to submit</param>
    /// <param name="source">Source connector identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if submission was successful</returns>
    Task<bool> SubmitSystemEventsAsync(
        IEnumerable<SystemEvent> systemEvents,
        string source,
        CancellationToken cancellationToken = default
    );
}
