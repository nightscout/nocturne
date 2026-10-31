using Nocturne.Core.Models.Services;

namespace Nocturne.Core.Models.Connectors;

/// <summary>
/// The data types the source-owned data module counts and deletes under. Each is the name of a
/// <c>SyncDataType</c> member, which Core cannot reference.
/// </summary>
public static class SourceDataTypes
{
    public const string Glucose = "Glucose";
    public const string ManualBG = "ManualBG";
    public const string Calibrations = "Calibrations";
    public const string Boluses = "Boluses";
    public const string CarbIntake = "CarbIntake";
    public const string BGChecks = "BGChecks";
    public const string BolusCalculations = "BolusCalculations";
    public const string Notes = "Notes";
    public const string DeviceEvents = "DeviceEvents";
    public const string StateSpans = "StateSpans";
    public const string TempBasals = "TempBasals";
    public const string Profiles = "Profiles";
    public const string DeviceStatus = "DeviceStatus";
    public const string Steps = "Steps";
    public const string HeartRate = "HeartRate";
    public const string BodyWeight = "BodyWeight";
}

/// <summary>
/// One table's rows inside a discovery window that share a source key, <c>DataSource ?? Device</c>
/// (a state span's <c>Source</c>).
/// </summary>
/// <param name="DataType">A <see cref="SourceDataTypes"/> name.</param>
/// <param name="Handle">Which handle <paramref name="Key"/> is, or null when the table cannot tell.</param>
public sealed record SourceBucket(
    string DataType,
    string? Key,
    long Count,
    int CountLast24Hours,
    DateTime Latest,
    DateTime? Oldest,
    SourceHandle? Handle);

/// <summary>Sensor glucose inside a discovery window from one <c>Device</c>.</summary>
/// <param name="DataSource">The greatest data source among its rows, or null when none carries one.</param>
public sealed record GlucoseDeviceStats(
    string Device,
    string? DataSource,
    long Count,
    int CountLast24Hours,
    DateTime Latest,
    DateTime Oldest);

/// <summary>The latest APS snapshot inside a discovery window from one <c>Device</c>.</summary>
/// <param name="DataSource">The greatest data source among its rows, or null when none carries one.</param>
public sealed record ApsDeviceLastSeen(string Device, string? DataSource, DateTime Latest);

/// <summary>One data type's rows of a source.</summary>
/// <param name="DataType">A <see cref="SourceDataTypes"/> name.</param>
/// <param name="Latest">Null when the read does not report it, or nothing is stored.</param>
/// <param name="Oldest">Null when the read does not report it, or nothing is stored.</param>
public sealed record SourceTypeStats(
    string DataType,
    long Count,
    int CountLast24Hours,
    DateTime? Latest = null,
    DateTime? Oldest = null);

/// <summary>What a soft delete of a source's rows removed.</summary>
/// <param name="DeletedCounts">Rows deleted by <see cref="SourceDataTypes"/> name, non-zero only.</param>
/// <param name="ProfileNames">The distinct profile names of the deleted schedules and therapy settings.</param>
public sealed record SourcePurgeResult(
    IReadOnlyDictionary<string, long> DeletedCounts,
    IReadOnlyCollection<string> ProfileNames);
