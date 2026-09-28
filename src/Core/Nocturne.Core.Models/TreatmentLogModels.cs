using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// The record kind the Treatment Log is filtered to, or <see cref="All"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TreatmentLogCategory>))]
public enum TreatmentLogCategory
{
    /// <summary>Every record kind.</summary>
    All,

    /// <summary>Bolus deliveries.</summary>
    Bolus,

    /// <summary>Carb intakes.</summary>
    Carbs,

    /// <summary>Blood glucose checks.</summary>
    BgCheck,

    /// <summary>Notes.</summary>
    Note,

    /// <summary>Device events.</summary>
    DeviceEvent,

    /// <summary>Long-acting insulin injections.</summary>
    BasalInjection,
}

/// <summary>
/// Records per kind among those a Treatment Log filter keeps.
/// </summary>
public class TreatmentLogCounts
{
    /// <summary>Every kept record.</summary>
    public int All { get; set; }

    /// <summary>Kept bolus deliveries.</summary>
    public int Bolus { get; set; }

    /// <summary>Kept carb intakes.</summary>
    public int Carbs { get; set; }

    /// <summary>Kept blood glucose checks.</summary>
    public int BgCheck { get; set; }

    /// <summary>Kept notes.</summary>
    public int Note { get; set; }

    /// <summary>Kept device events.</summary>
    public int DeviceEvent { get; set; }

    /// <summary>Kept long-acting insulin injections.</summary>
    public int BasalInjection { get; set; }
}

/// <summary>
/// The Treatment Log stats card's figures, all taken from the records its filter keeps.
/// </summary>
public class TreatmentLogStats
{
    /// <summary>Kept records per kind.</summary>
    public TreatmentLogCounts Counts { get; set; } = new();

    /// <summary>
    /// Summary over the kept boluses and carb intakes, or <c>null</c> when the filter keeps neither.
    /// </summary>
    public TreatmentSummary? TreatmentSummary { get; set; }
}
