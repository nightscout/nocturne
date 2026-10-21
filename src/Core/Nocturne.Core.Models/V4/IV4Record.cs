namespace Nocturne.Core.Models.V4;

/// <summary>
/// Shared interface for all V4 domain models, providing common traceability and metadata properties.
/// </summary>
/// <remarks>
/// All V4 record types implement this interface to guarantee a uniform set of audit,
/// timestamp, and lineage columns. The <see cref="Mills"/> property is always computed
/// from <see cref="Timestamp"/> (mills-first pattern) so that v1/v3 API consumers
/// receive Unix-millisecond timestamps without additional conversion.
/// <para>
/// <see cref="CorrelationId"/> links records that were decomposed from the same legacy
/// <see cref="Treatment"/> or <see cref="Profile"/> during migration, while
/// <see cref="LegacyId"/> preserves the original MongoDB <c>_id</c> for traceability.
/// </para>
/// </remarks>
/// <seealso cref="SensorGlucose"/>
/// <seealso cref="Bolus"/>
/// <seealso cref="CarbIntake"/>
/// <seealso cref="BGCheck"/>
/// <seealso cref="Note"/>
/// <seealso cref="DeviceEvent"/>
/// <seealso cref="Calibration"/>
/// <seealso cref="MeterGlucose"/>
/// <seealso cref="ApsSnapshot"/>
/// <seealso cref="PumpSnapshot"/>
/// <seealso cref="UploaderSnapshot"/>
/// <seealso cref="BolusCalculation"/>
/// <seealso cref="BasalSchedule"/>
/// <seealso cref="CarbRatioSchedule"/>
/// <seealso cref="SensitivitySchedule"/>
/// <seealso cref="TargetRangeSchedule"/>
/// <seealso cref="TherapySettings"/>
public interface IV4Record
{
    /// <summary>
    /// UUID v7 primary key.
    /// </summary>
    Guid Id { get; set; }

    /// <summary>
    /// Canonical timestamp as UTC <see cref="DateTime"/>.
    /// </summary>
    DateTime Timestamp { get; set; }

    /// <summary>
    /// Unix milliseconds, computed from <see cref="Timestamp"/>.
    /// </summary>
    /// <remarks>
    /// This is a computed property: the Unix milliseconds of <see cref="Timestamp"/>.
    /// It exists for v1/v3 API compatibility where <c>mills</c> / <c>date</c> fields are expected.
    /// </remarks>
    long Mills { get; }

    /// <summary>
    /// UTC offset in minutes from the originating device's local time.
    /// </summary>
    int? UtcOffset { get; set; }

    /// <summary>
    /// Device identifier string that produced or uploaded this record.
    /// </summary>
    string? Device { get; set; }

    /// <summary>
    /// Application name that uploaded this record (e.g., "xDrip", "AAPS", "Loop").
    /// </summary>
    string? App { get; set; }

    /// <summary>
    /// Origin data source identifier (e.g., connector name such as "dexcom", "glooko").
    /// </summary>
    string? DataSource { get; set; }

    /// <summary>
    /// Links records that were decomposed from the same legacy record during migration.
    /// </summary>
    /// <remarks>
    /// When a legacy <see cref="Treatment"/> is decomposed into a <see cref="Bolus"/> and a
    /// <see cref="CarbIntake"/>, both share the same <see cref="CorrelationId"/>.
    /// </remarks>
    Guid? CorrelationId { get; set; }

    /// <summary>
    /// Original v1/v3 record ID (MongoDB <c>_id</c>) preserved for migration traceability.
    /// </summary>
    string? LegacyId { get; set; }

    /// <summary>
    /// When this record was first created (UTC).
    /// </summary>
    DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this record was last modified (UTC).
    /// </summary>
    DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Catch-all dictionary for fields not mapped to dedicated columns.
    /// </summary>
    Dictionary<string, object?>? AdditionalProperties { get; set; }
}
