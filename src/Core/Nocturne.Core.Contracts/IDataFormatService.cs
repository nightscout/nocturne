using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for formatting data into different output formats (CSV, TSV, etc.)
/// </summary>
public interface IDataFormatService
{
    /// <summary>
    /// Format entries into the specified format
    /// </summary>
    /// <param name="entries">Entries to format</param>
    /// <param name="format">Format type (csv, tsv, txt)</param>
    /// <returns>Formatted data as string</returns>
    /// <exception cref="ArgumentException">Thrown when the format is not supported.</exception>
    string FormatEntries(Entry[] entries, string format);

    /// <summary>
    /// Format treatments into the specified format
    /// </summary>
    /// <param name="treatments">Treatments to format</param>
    /// <param name="format">Format type (csv, tsv, txt)</param>
    /// <returns>Formatted data as string</returns>
    /// <exception cref="ArgumentException">Thrown when the format is not supported.</exception>
    string FormatTreatments(Treatment[] treatments, string format);

    /// <summary>
    /// Format device status into the specified format
    /// </summary>
    /// <param name="deviceStatus">Device status to format</param>
    /// <param name="format">Format type (csv, tsv, txt)</param>
    /// <returns>Formatted data as string</returns>
    /// <exception cref="ArgumentException">Thrown when the format is not supported.</exception>
    string FormatDeviceStatus(DeviceStatus[] deviceStatus, string format);

    /// <summary>
    /// Get the content type for the specified format
    /// </summary>
    /// <param name="format">Format type</param>
    /// <returns>Content type string</returns>
    string GetContentType(string format);
}
