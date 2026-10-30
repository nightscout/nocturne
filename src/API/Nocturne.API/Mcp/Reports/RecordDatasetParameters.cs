using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// A kind of treatment record the <c>treatments</c> dataset reads: the Treatment Log's kinds that are
/// treatments (<see cref="Nocturne.Core.Models.TreatmentLogCategory"/>, without BG checks and device
/// events, which are other categories), and the temp basals and overrides the dashboard chart draws.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportTreatmentType>))]
internal enum ReportTreatmentType
{
    [JsonStringEnumMemberName("bolus")]
    Bolus,

    [JsonStringEnumMemberName("carbs")]
    Carbs,

    [JsonStringEnumMemberName("temp_basal")]
    TempBasal,

    [JsonStringEnumMemberName("note")]
    Note,

    [JsonStringEnumMemberName("override")]
    Override,

    [JsonStringEnumMemberName("basal_injection")]
    BasalInjection,
}

[Description(
    "treatments: the treatment records in the range, one row each: boluses, carb entries, temp basals, notes, "
    + "overrides and long-acting injections.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TreatmentsParameters : ReportDatasetParameters
{
    public const string Name = "treatments";

    [JsonIgnore]
    public override string DatasetName => Name;

    [Description("The kinds to read: bolus, carbs, temp_basal, note, override, basal_injection. Optional; default every kind.")]
    public IReadOnlyList<ReportTreatmentType>? Types { get; init; }

    [Description("Keep only carb entries of at least this many grams. Optional; the other kinds are not affected.")]
    [Range(0, 1000)]
    public double? MinCarbs { get; init; }

    [Description("Keep only boluses and long-acting injections of at least this many units. Optional; the other kinds are not affected.")]
    [Range(0, 100)]
    public double? MinInsulin { get; init; }
}

[Description("basal_delivery: the basal rate delivered over time beside the rate the schedule called for, as the dashboard's basal track draws them.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BasalDeliveryParameters : ReportDatasetParameters
{
    public const string Name = "basal_delivery";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("device_status: pump reservoir and battery and uploader battery over time, and the sensor age now.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DeviceStatusParameters : ReportDatasetParameters
{
    public const string Name = "device_status";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("glucose_distribution: the percent of the range's time in each clinical glucose band: very low, low, in range, high, very high.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GlucoseDistributionParameters : ReportDatasetParameters
{
    public const string Name = "glucose_distribution";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("daily_summary: one row per local day: time in range, below and above, mean glucose, and with treatments.read the day's insulin and carbs.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DailySummaryParameters : ReportDatasetParameters
{
    public const string Name = "daily_summary";

    [JsonIgnore]
    public override string DatasetName => Name;
}
