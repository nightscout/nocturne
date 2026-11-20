using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// A live target range or sensitivity schedule whose values still look like mmol/L stored as mg/dL,
/// which the mmol repair migration could not prove and left for the owner to correct.
/// </summary>
public class RawMmolSchedule
{
    /// <summary>Which schedule table the row is in.</summary>
    public RawMmolScheduleKind Kind { get; set; }

    /// <summary>The schedule's id, for <c>PUT target-range/{id}</c> or <c>PUT sensitivity/{id}</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>The profile the schedule belongs to.</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>When the schedule takes effect.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>The store's legacy id, one half of its identity.</summary>
    public string? LegacyId { get; set; }

    /// <summary>The store's correlation id, which with the profile name is the other half.</summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>Why the schedule is listed.</summary>
    public RawMmolReason Reason { get; set; }

    /// <summary>
    /// Whether the owner can say the values are already mg/dL. Only an ISF can be, and only when its
    /// store has a therapy settings row to hold the answer.
    /// </summary>
    public bool CanDismiss { get; set; }

    /// <summary>The schedule's entries as stored.</summary>
    public List<RawMmolScheduleEntry> Entries { get; set; } = [];
}

/// <summary>One stored entry of a <see cref="RawMmolSchedule"/>; a value that is not a number is null.</summary>
public class RawMmolScheduleEntry
{
    /// <summary>Start time of the entry, <c>HH:mm</c>.</summary>
    public string? Time { get; set; }

    /// <summary>Low target, for a target range schedule.</summary>
    public double? Low { get; set; }

    /// <summary>High target, for a target range schedule.</summary>
    public double? High { get; set; }

    /// <summary>ISF, for a sensitivity schedule.</summary>
    public double? Value { get; set; }
}

/// <summary>The schedule table a <see cref="RawMmolSchedule"/> is in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RawMmolScheduleKind>))]
public enum RawMmolScheduleKind
{
    /// <summary>A <see cref="TargetRangeSchedule"/>.</summary>
    TargetRange,

    /// <summary>A <see cref="SensitivitySchedule"/>.</summary>
    Sensitivity,
}

/// <summary>Why a <see cref="RawMmolSchedule"/> is listed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RawMmolReason>))]
public enum RawMmolReason
{
    /// <summary>A low or high target is at or under 22.2, which no mg/dL target is.</summary>
    TargetInMmolBand,

    /// <summary>
    /// An ISF from 0.5 to 22.2 that nothing proves converted, or a mmol store's ISF under 9. Such a value
    /// can be a real mg/dL/U one, so the owner can dismiss it.
    /// </summary>
    IsfMayBeMmol,
}
