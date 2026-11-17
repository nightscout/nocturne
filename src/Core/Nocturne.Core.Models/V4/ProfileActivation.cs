using System.Text.Json.Serialization;
using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>Whether a <see cref="ProfileActivation"/> asks for a profile or reports the one running.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProfileActivationPhase>))]
public enum ProfileActivationPhase
{
    /// <summary>A profile switch as asked for: a Nightscout or AAPS Profile Switch.</summary>
    Requested,

    /// <summary>
    /// The profile the pump runs: an AAPS EffectiveProfileSwitch, or a connector's reading of the
    /// active program.
    /// </summary>
    Effective,
}

/// <summary>What wrote a <see cref="ProfileActivation"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProfileActivationOrigin>))]
public enum ProfileActivationOrigin
{
    /// <summary>An AID uploader (AAPS, Loop, Trio) posting treatments.</summary>
    Uploader,

    /// <summary>A Nocturne connector reading the pump's active program.</summary>
    Connector,

    /// <summary>A person, through the careportal or another treatment form.</summary>
    User,
}

/// <summary>
/// One activation of a stored profile, scaled by <see cref="Percentage"/> and shifted by
/// <see cref="TimeshiftMs"/>, from <see cref="StartTimestamp"/> until <see cref="EndTimestamp"/>.
/// </summary>
/// <remarks>
/// <see cref="IV4Record.Timestamp"/> is the start. <see cref="EndTimestamp"/> is where the activation
/// stopped applying: the end of a temporary one, or the start of what superseded it; it is not the
/// requested length, which <see cref="RequestedDurationMs"/> keeps. <see cref="IV4Record.LegacyId"/> is
/// the treatment an uploaded activation was decomposed from, and only those rows are served as legacy
/// treatments; a connector's rows carry their upstream key in <see cref="SyncIdentifier"/> instead.
/// </remarks>
[JsonSchemaFlatten]
public class ProfileActivation : V4RecordBase, IDeviceAttributed
{
    /// <summary>
    /// The <see cref="IV4Record.AdditionalProperties"/> key an activation's notes are kept under, the
    /// <c>notes</c> field of the treatment it may be served as.
    /// </summary>
    public const string NotesKey = "notes";

    [JsonIgnore]
    public DateTime StartTimestamp
    {
        get => Timestamp;
        set => Timestamp = value;
    }

    public DateTime? EndTimestamp { get; set; }

    [JsonIgnore]
    public long? EndMills => EndTimestamp is { } end
        ? end.ToMills()
        : null;

    public ProfileActivationPhase Phase { get; set; }

    public ProfileActivationOrigin Origin { get; set; }

    /// <summary>The stored profile the activation runs, never a customized name such as <c>"Rhys (110%)"</c>.</summary>
    public string BaseProfileName { get; set; } = "Default";

    public int Percentage { get; set; } = 100;

    /// <summary>The shift in milliseconds, as uploaded once converted from its unit; not reduced to a day.</summary>
    public long TimeshiftMs { get; set; }

    /// <summary>The length asked for; null or zero for a permanent activation.</summary>
    public long? RequestedDurationMs { get; set; }

    /// <summary>The requested activation an effective one confirms; several may confirm the same one.</summary>
    public Guid? RequestedActivationId { get; set; }

    public ProfileActivationSnapshot? Snapshot { get; set; }

    /// <summary>
    /// Whether <see cref="Snapshot"/> already has <see cref="Percentage"/> and <see cref="TimeshiftMs"/>
    /// applied, as an AAPS effective switch's <c>profileJson</c> has; a Profile Switch's is the base profile.
    /// </summary>
    public bool SnapshotIsCustomized { get; set; }

    public string? EnteredBy { get; set; }

    /// <summary>When a person ended the activation early; part of the end it brings with it.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Who ended it, when <see cref="EndedAt"/> is set.</summary>
    public string? EndedBy { get; set; }

    /// <summary>A connector's upstream key; see the remarks.</summary>
    public string? SyncIdentifier { get; set; }

    public Guid? DeviceId { get; set; }

    public Guid? PatientDeviceId { get; set; }

    [JsonIgnore]
    public bool IsTemporary => RequestedDurationMs is > 0;

    /// <summary>Whether it ends on its own: temporary, or ended by a person.</summary>
    [JsonIgnore]
    public bool IsBounded => IsTemporary || EndedAt is not null;
}

/// <summary>
/// The therapy settings a <see cref="ProfileActivation"/> was uploaded with, held so the activation
/// resolves without the profile store.
/// </summary>
/// <remarks>Sensitivity and targets are mg/dL, as the decomposed profile schedules are.</remarks>
public sealed class ProfileActivationSnapshot
{
    public List<ScheduleEntry>? Basal { get; set; }

    public List<ScheduleEntry>? CarbRatio { get; set; }

    public List<ScheduleEntry>? Sensitivity { get; set; }

    public List<TargetRangeEntry>? Targets { get; set; }

    public string? Units { get; set; }

    public double? Dia { get; set; }

    public int? CarbsHr { get; set; }

    public string? Timezone { get; set; }

    /// <summary>The insulin AAPS sends in <c>icfg</c>.</summary>
    public TreatmentInsulinContext? Insulin { get; set; }

    [JsonIgnore]
    public bool HasSchedules =>
        Basal is { Count: > 0 } || CarbRatio is { Count: > 0 } || Sensitivity is { Count: > 0 } || Targets is { Count: > 0 };

    [JsonIgnore]
    public bool HasEverySchedule =>
        Basal is { Count: > 0 } && CarbRatio is { Count: > 0 } && Sensitivity is { Count: > 0 } && Targets is { Count: > 0 };
}
