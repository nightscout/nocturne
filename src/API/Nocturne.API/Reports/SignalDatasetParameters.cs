using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Nocturne.API.Reports;

/// <summary>A lane of device events the <c>device_events</c> dataset reads.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportDeviceEventGroup>))]
internal enum ReportDeviceEventGroup
{
    [Description("Sensor starts, changes and stops")]
    [JsonStringEnumMemberName("sensor")]
    Sensor,

    [Description("Site, cannula, pod, reservoir and insulin changes, and priming")]
    [JsonStringEnumMemberName("site")]
    Site,

    [Description("Pump suspends and resumes, battery changes, and pump clock and limit changes")]
    [JsonStringEnumMemberName("pump")]
    Pump,
}

[Description("device_events: each device event, as a tick in a lane: sensor changes, site and insulin changes, and pump events. Needs the devices permission.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DeviceEventsParameters : ReportDatasetParameters
{
    public const string Name = "device_events";

    [JsonIgnore]
    public override string DatasetName => Name;

    [Description("The lanes to read: sensor, site, pump. Optional; every lane when missing.")]
    [DisplayName("Lanes")]
    [Length(1, 3)]
    public IReadOnlyList<ReportDeviceEventGroup>? Groups { get; init; }
}

[Description("system_events: each alarm, hazard, warning and notice a pump, CGM or uploader reported, as a tick in a lane. Needs the devices permission.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SystemEventsParameters : ReportDatasetParameters
{
    public const string Name = "system_events";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("trackers: each tracked consumable (sensor, site, reservoir, battery and others) from its start to its end. Needs the alerts permission.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TrackersParameters : ReportDatasetParameters
{
    public const string Name = "trackers";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("sleep_stages: each sleep stage (awake, REM, light, deep) from its start to its end, a lane each. Needs the sleep permission.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SleepStagesParameters : ReportDatasetParameters
{
    public const string Name = "sleep_stages";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description(
    "sleep_nights: one row per sleep session on the night it ended: score, efficiency, time to fall asleep and restless periods, "
    + "and with the heart rate permission its heart rate, HRV, blood oxygen and breathing rate. Needs the sleep permission.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SleepNightsParameters : ReportDatasetParameters
{
    public const string Name = "sleep_nights";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description(
    "sleep_biometrics: the heart rate, HRV, blood oxygen and breathing rate a wearable recorded during sleep, over time. "
    + "Needs the sleep and heart rate permissions.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SleepBiometricsParameters : ReportDatasetParameters
{
    public const string Name = "sleep_biometrics";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("lab_hba1c: each HbA1c result from a lab, in percent, on the day it was measured.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LabHbA1cParameters : ReportDatasetParameters
{
    public const string Name = "lab_hba1c";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description(
    "ehba1c: the estimated HbA1c of each day, in percent, from a recency-weighted mean of the glucose of the 90 days before it, "
    + "with that weighted mean. A day with less than a month of readings in its window has no row.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EHbA1cParameters : ReportDatasetParameters
{
    public const string Name = "ehba1c";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("body_weight: each body weight record: weight, body fat and lean mass. Needs the therapy permission.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BodyWeightParameters : ReportDatasetParameters
{
    public const string Name = "body_weight";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("alert_excursions: each time an alert's condition held, from its start to its end, a lane for each alert. Needs the alerts permission.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AlertExcursionsParameters : ReportDatasetParameters
{
    public const string Name = "alert_excursions";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("alert_instances: each time an alert was raised, as a tick in a lane for each alert, with its snoozes. Needs the alerts permission.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AlertInstancesParameters : ReportDatasetParameters
{
    public const string Name = "alert_instances";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("dnd_windows: each do-not-disturb period, from its start to its end, a lane for the alerts it held. Needs the alerts permission.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DndWindowsParameters : ReportDatasetParameters
{
    public const string Name = "dnd_windows";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("sensor_detail: what each sensor reading carries beside its glucose: its rate of change and its noise.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SensorDetailParameters : ReportDatasetParameters
{
    public const string Name = "sensor_detail";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("meter_glucose: each blood glucose reading a meter sent through the pump or uploader.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MeterGlucoseParameters : ReportDatasetParameters
{
    public const string Name = "meter_glucose";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("compression_lows: each period at night that looks like a false low from pressure on the sensor, as Nocturne found it.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CompressionLowsParameters : ReportDatasetParameters
{
    public const string Name = "compression_lows";

    [JsonIgnore]
    public override string DatasetName => Name;
}

[Description("glucose_episodes: each low and high episode (15 minutes or more past a limit), from its start to its end, as the statistics count them.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GlucoseEpisodesParameters : ReportDatasetParameters
{
    public const string Name = "glucose_episodes";

    [JsonIgnore]
    public override string DatasetName => Name;
}
