namespace Nocturne.Core.Models.V4;

/// <summary>
/// A time-series record that can be attributed to a specific <see cref="PatientDevice"/>.
/// </summary>
/// <remarks>
/// Implemented by the record types whose data originates from a physical device the patient
/// wears or carries (CGM readings, boluses, temp basals, pen injections, meter readings).
/// <see cref="PatientDeviceId"/> is stamped at ingest time by matching <see cref="Timestamp"/>
/// against the device's usage window; it stays null when no registered device matches or the
/// match is ambiguous. <see cref="Timestamp"/> is the instant used for that matching — for
/// span-based records it is the span start.
/// </remarks>
/// <seealso cref="PatientDevice"/>
public interface IDeviceAttributed
{
    /// <summary>Timestamp used to match the record against a device's usage window.</summary>
    DateTime Timestamp { get; }

    /// <summary>
    /// UTC offset in minutes from the originating device's local time. Used to compare against the
    /// wearer's local date, since device usage windows are user-entered local dates.
    /// </summary>
    int? UtcOffset { get; }

    /// <summary>Device identifier string that produced or uploaded this record.</summary>
    string? Device { get; }

    /// <summary>Origin data source identifier (e.g. a connector name).</summary>
    string? DataSource { get; }

    /// <summary>Foreign key to the <see cref="PatientDevice"/> this record is attributed to.</summary>
    Guid? PatientDeviceId { get; set; }
}
