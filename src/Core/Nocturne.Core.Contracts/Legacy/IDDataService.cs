using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Legacy;

/// <summary>
/// Service interface for <see cref="DData"/> operations.
/// Provides methods to construct and manipulate <see cref="DData"/> structures with 1:1 legacy compatibility.
/// </summary>
/// <seealso cref="DData"/>
/// <seealso cref="DDataResponse"/>
/// <seealso cref="Treatment"/>
/// <seealso cref="DeviceStatus"/>
public interface IDDataService
{
    /// <summary>
    /// Creates a DData structure for the specified timestamp
    /// </summary>
    /// <param name="timestamp">The timestamp to get data for (Unix milliseconds)</param>
    /// <returns>A DData structure containing all relevant data</returns>
    Task<DData> GetDDataAsync(long timestamp, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the DData frame Nightscout serves for a point in time, with glucose oldest first
    /// </summary>
    /// <param name="timestamp">The timestamp to get data for (Unix milliseconds)</param>
    Task<DDataFrame> GetDDataFrameAsync(long timestamp, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a DData structure for the current time
    /// </summary>
    /// <returns>A DData structure containing all relevant data</returns>
    Task<DData> GetCurrentDDataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets DData with recent device status entries (matching dataWithRecentStatuses)
    /// </summary>
    /// <param name="timestamp">The timestamp to get data for (Unix milliseconds)</param>
    Task<DDataResponse> GetDDataWithRecentStatusesAsync(
        long timestamp,
        CancellationToken cancellationToken = default
    );

    /// <returns>A DData structure with processed treatments</returns>
    DData ProcessTreatments(List<Treatment> treatments, bool preserveOriginalTreatments = false);

    /// <param name="deviceStatuses">All device status entries</param>
    /// <param name="time">The time to filter by (Unix milliseconds)</param>
    /// <returns>Recent device status entries</returns>
    List<DeviceStatus> GetRecentDeviceStatus(List<DeviceStatus> deviceStatuses, long time);

    /// <summary>
    /// Processes durations for treatments (cuts overlapping durations)
    /// </summary>
    /// <param name="keepZeroDuration">Whether to keep treatments with zero duration</param>
    /// <returns>Processed treatments</returns>
    List<Treatment> ProcessDurations(List<Treatment> treatments, bool keepZeroDuration);

    /// <summary>
    /// Converts temporary target units from mmol/L to mg/dL if needed
    /// </summary>
    /// <returns>Converted treatments</returns>
    List<Treatment> ConvertTempTargetUnits(List<Treatment> treatments);

    /// <summary>
    /// Processes raw data for runtime use (adds mills property, converts ObjectIds)
    /// </summary>
    /// <returns>Processed data</returns>
    T ProcessRawDataForRuntime<T>(T data)
        where T : class;

    /// <summary>
    /// Merges two lists based on _id, preferring new objects when collision is found
    /// </summary>
    /// <typeparam name="T">The type of data (must have Id property)</typeparam>
    /// <returns>Merged data list</returns>
    List<T> IdMergePreferNew<T>(List<T> oldData, List<T> newData)
        where T : class;

    /// <summary>
    /// Creates a deep clone of a DData object
    /// </summary>
    /// <returns>Cloned DData object</returns>
    DData Clone(DData ddata);
}
