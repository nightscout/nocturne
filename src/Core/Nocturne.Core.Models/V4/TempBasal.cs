using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Temporary basal rate change record -- a time-ranged event
/// representing a deviation from the scheduled basal rate.
/// </summary>
/// <remarks>
/// <para>
/// This is the V4 equivalent of legacy <see cref="Treatment"/> records with event type
/// "Temp Basal". Unlike the other <see cref="IV4Record"/> types, <see cref="TempBasal"/> has
/// both a <see cref="StartTimestamp"/> and an <see cref="EndTimestamp"/> (span-based); its
/// <see cref="IV4Record.Timestamp"/> is the span start, implemented explicitly so the wire shape
/// keeps only the start/end pair.
/// </para>
/// <para>
/// <see cref="Origin"/> indicates whether the temp basal was set by an APS algorithm,
/// manually by the user, or inferred from pump data.
/// </para>
/// </remarks>
/// <seealso cref="Treatment"/>
/// <seealso cref="TempBasalOrigin"/>
/// <seealso cref="Bolus"/>
/// <seealso cref="ApsSnapshot"/>
/// <seealso cref="BasalSchedule"/>
/// <seealso cref="Device"/>
public class TempBasal : IV4Record, IDeviceAttributed
{
    /// <summary>
    /// Attribution timestamp for device matching — the span start.
    /// </summary>
    DateTime IDeviceAttributed.Timestamp => StartTimestamp;

    /// <summary>The span start.</summary>
    DateTime IV4Record.Timestamp
    {
        get => StartTimestamp;
        set => StartTimestamp = value;
    }

    /// <summary>The span start in Unix milliseconds.</summary>
    long IV4Record.Mills => StartMills;

    /// <summary>
    /// UUID v7 primary key
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Start timestamp as UTC DateTime
    /// </summary>
    public DateTime StartTimestamp { get; set; }

    /// <summary>
    /// End timestamp as UTC DateTime (null if still active)
    /// </summary>
    public DateTime? EndTimestamp { get; set; }

    /// <summary>
    /// Start timestamp in Unix milliseconds, computed from <see cref="StartTimestamp"/>.
    /// </summary>
    public long StartMills => StartTimestamp.ToMills();

    /// <summary>
    /// End timestamp in Unix milliseconds, computed from <see cref="EndTimestamp"/>.
    /// Returns <c>null</c> when <see cref="EndTimestamp"/> is not set.
    /// </summary>
    public long? EndMills => EndTimestamp.HasValue ? EndTimestamp.Value.ToMills() : null;

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    public int? UtcOffset { get; set; }

    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this record
    /// </summary>
    public string? App { get; set; }

    public string? DataSource { get; set; }

    /// <summary>
    /// Links records that were split from the same legacy Treatment
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Original v1/v3 record ID for migration traceability
    /// </summary>
    public string? LegacyId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Temporary basal rate in units per hour. Null only for a temp set by <see cref="Percent"/>
    /// whose rate is unknown: none was uploaded and no schedule was in force at its start. Its
    /// delivery is unknown, not zero.
    /// </summary>
    public double? Rate { get; set; }

    /// <summary>
    /// Scheduled basal rate that this temp basal overrides
    /// </summary>
    public double? ScheduledRate { get; set; }

    /// <summary>
    /// For a temp basal set relative to the schedule, the change from the scheduled rate in percent
    /// (Nightscout's <c>percent</c>: -50 halves it, 50 adds half again); null for an absolute temp.
    /// <see cref="Rate"/> holds the rate the uploader sent with it, else what it resolves to against
    /// the schedule in force at its start.
    /// </summary>
    public double? Percent { get; set; }

    /// <summary>
    /// Whether Nocturne resolved <see cref="Rate"/> from <see cref="Percent"/> against the schedule,
    /// rather than the uploader sending it. A resolved rate is not served back as the upload's
    /// <c>rate</c>, and is resolved again when the percent changes.
    /// </summary>
    public bool RateFromSchedule { get; set; }

    /// <summary>
    /// The rate <paramref name="percent"/> sets over <paramref name="scheduledRate"/>, as Nightscout's
    /// <c>getTempBasal</c> computes it: <c>basal * (100 + percent) / 100</c>.
    /// </summary>
    public static double RateRelativeTo(double scheduledRate, double percent) =>
        Math.Max(0, scheduledRate * (100 + percent) / 100);

    private long? _recordStartMills;

    /// <summary>
    /// The start of the stored record this is a piece of, which is <see cref="StartMills"/> for a
    /// record that has not been cut by <see cref="WithSpan"/>.
    /// </summary>
    [JsonIgnore]
    public long RecordStartMills => _recordStartMills ?? StartMills;

    /// <summary>
    /// A shallow copy over <paramref name="startMills"/> to <paramref name="endMills"/>. Reference
    /// members such as <see cref="AdditionalProperties"/> are shared with this record.
    /// </summary>
    public TempBasal WithSpan(long startMills, long endMills)
    {
        var copy = (TempBasal)MemberwiseClone();
        copy._recordStartMills = RecordStartMills;
        copy.StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMills).UtcDateTime;
        copy.EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(endMills).UtcDateTime;
        return copy;
    }

    /// <summary>
    /// Origin of this temp basal: <see cref="TempBasalOrigin.Algorithm"/>,
    /// <see cref="TempBasalOrigin.Scheduled"/>, <see cref="TempBasalOrigin.Manual"/>,
    /// <see cref="TempBasalOrigin.Suspended"/>, or <see cref="TempBasalOrigin.Inferred"/>.
    /// </summary>
    public TempBasalOrigin Origin { get; set; }

    /// <summary>
    /// How <see cref="Origin"/> was decided. Null on a record stored before it was recorded; see
    /// <see cref="TempBasalOriginProvenance.EvidenceOf(TempBasal)"/>.
    /// </summary>
    public TempBasalOriginEvidence? OriginEvidence { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="Device"/> table.
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="PatientDevice"/> table.
    /// </summary>
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// Pump-specific record identifier for deduplication.
    /// </summary>
    public string? PumpRecordId { get; set; }

    /// <summary>
    /// Stable per-source identifier. Records matched on (DataSource, SyncIdentifier) are updated in
    /// place on re-upload instead of duplicated, so uploader retries are idempotent.
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// FK to the <see cref="ApsSnapshot"/> whose algorithm decision set this temp basal.
    /// </summary>
    public Guid? ApsSnapshotId { get; set; }

    /// <summary>
    /// Snapshot of the insulin pharmacokinetic settings active when this temp basal was set.
    /// Used by the IOB calculator for per-insulin basal IOB decay curves.
    /// When null, falls back to profile-level DIA.
    /// </summary>
    public TreatmentInsulinContext? InsulinContext { get; set; }

    /// <summary>
    /// Catch-all for fields not mapped to dedicated columns
    /// </summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
