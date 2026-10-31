using System.Globalization;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// The device status the v4 device-status endpoints serve: pump snapshots
/// (<c>GET /api/v4/device-status/pump</c>, <see cref="IPumpSnapshotRepository"/>) and uploader
/// snapshots (<c>GET /api/v4/device-status/uploader</c>, <see cref="IUploaderSnapshotRepository"/>),
/// both behind devices.read, and for the tiles the sensor age (<c>GET /api/v4/deviceage/sensor</c>).
/// </summary>
internal sealed class DeviceStatusDataset : IReportDataset
{
    public const int Budget = 2000;

    /// <summary>The most snapshots of each kind read for a panel.</summary>
    internal const int RecordLimit = 50_000;

    public string Name => DeviceStatusParameters.Name;

    public string Label => "Device status";

    public string Description => "The pump reservoir and the batteries over time, and the sensor age.";

    public Type ParametersType => typeof(DeviceStatusParameters);

    public string RequiredScope => Scope.DevicesRead;

    private static readonly IReadOnlyList<ReportField> SeriesFields =
    [
        new("time", "Time", ReportFieldKind.Time, null, "When the device reported, Unix milliseconds."),
        new("reservoir", "Reservoir", ReportFieldKind.Number, "U", "Insulin left in the pump's reservoir.", Color: ChartColor.InsulinBolus, Step: true),
        new("pumpBattery", "Pump battery", ReportFieldKind.Percent, "%", "The pump's battery.", Step: true),
        new("uploaderBattery", "Uploader battery", ReportFieldKind.Percent, "%", "The uploader's (phone's) battery.", Step: true),
    ];

    private static readonly IReadOnlyList<ReportField> TileFields =
    [
        new("reservoir", "Reservoir", ReportFieldKind.Number, "U", "The latest reservoir in the range."),
        new("pumpBattery", "Pump battery", ReportFieldKind.Percent, "%", "The latest pump battery in the range."),
        new("uploaderBattery", "Uploader battery", ReportFieldKind.Percent, "%", "The latest uploader battery in the range."),
        new("sensorAgeHours", "Sensor age now", ReportFieldKind.Number, "h", "Hours since the latest sensor start or change, now."),
    ];

    public IReadOnlyList<ReportField> Fields { get; } = [.. SeriesFields, TileFields[^1]];

    public IReadOnlyList<ReportField> FieldsFor(ReportPrimitive primitive) =>
        primitive == ReportPrimitive.StatTiles ? TileFields : SeriesFields;

    public ReportBindings? DefaultBindings(ReportPrimitive primitive) => primitive == ReportPrimitive.TimeSeries
        ? new ReportBindings { X = "time", Y = ["pumpBattery", "uploaderBattery"] }
        : null;

    public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } = [ReportPrimitive.TimeSeries, ReportPrimitive.StatTiles];

    public TimeSpan DefaultRange => TimeSpan.FromHours(24);

    public TimeSpan MaxRange(ReportPrimitive primitive) => TimeSpan.FromDays(30);

    public int PointBudget => Budget;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var services = request.Services;
        var from = request.From.UtcDateTime;
        var to = request.To.UtcDateTime;
        var pump = (await services.GetRequiredService<IPumpSnapshotRepository>()
            .GetAsync(from, to, null, null, RecordLimit, 0, descending: false, cancellationToken)).OrderBy(p => p.Mills).ToList();
        var uploader = (await services.GetRequiredService<IUploaderSnapshotRepository>()
            .GetAsync(from, to, null, null, RecordLimit, 0, descending: false, cancellationToken)).OrderBy(u => u.Mills).ToList();
        if (pump.Count + uploader.Count > 0)
            request.ReadLog.Record("device_status", pump.Count + uploader.Count);

        var latestPump = pump.LastOrDefault(p => p.Reservoir is not null || p.BatteryPercent is not null);
        var latestUploader = uploader.LastOrDefault(u => u.Battery is not null);
        DeviceAgeInfo? sensor = null;
        if (request.Primitive == ReportPrimitive.StatTiles)
        {
            var age = await services.GetRequiredService<IDeviceAgeService>().GetSensorAgeAsync(new DeviceAgePreferences(), null, cancellationToken);
            var latest = age.Min == "Sensor Change" ? age.SensorChange : age.SensorStart;
            sensor = latest.Found ? latest : null;
            request.ReadLog.Record("device_events", sensor is null ? 0 : 1);
        }

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
        int sourceRows, interval;
        if (request.Primitive == ReportPrimitive.StatTiles)
        {
            rows =
            [
                new Dictionary<string, object?>
                {
                    ["reservoir"] = latestPump?.Reservoir is { } r ? Math.Round(r, 1) : null,
                    ["pumpBattery"] = latestPump?.BatteryPercent,
                    ["uploaderBattery"] = latestUploader?.Battery,
                    ["sensorAgeHours"] = sensor?.Age,
                },
            ];
            sourceRows = pump.Count + uploader.Count;
            interval = 0;
        }
        else
        {
            interval = GlucoseSeriesDataset.IntervalFor(request.From, request.To, 1, Budget / 2);
            var fromMills = request.From.ToUnixTimeMilliseconds();
            var pumpRows = Thin(pump.Where(p => p.Reservoir is not null || p.BatteryPercent is not null).ToList(), p => p.Mills, fromMills, interval)
                .Select(p => (p.Mills, Row: (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["time"] = p.Mills,
                    ["reservoir"] = p.Reservoir is { } r ? Math.Round(r, 1) : null,
                    ["pumpBattery"] = p.BatteryPercent,
                    ["uploaderBattery"] = null,
                }));
            var uploaderRows = Thin(uploader.Where(u => u.Battery is not null).ToList(), u => u.Mills, fromMills, interval)
                .Select(u => (u.Mills, Row: (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["time"] = u.Mills,
                    ["reservoir"] = null,
                    ["pumpBattery"] = null,
                    ["uploaderBattery"] = u.Battery,
                }));
            rows = [.. pumpRows.Concat(uploaderRows).OrderBy(r => r.Mills).Select(r => r.Row)];
            sourceRows = pump.Count + uploader.Count;
        }

        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = Summarise(pump, uploader, sensor, request.From, request.To,
                sensorAsked: request.Primitive == ReportPrimitive.StatTiles, clamped: request.HistoryClamped),
            Facts = FactsOf(pump, uploader, sensor),
            Sentence = pump.Count + uploader.Count == 0
                ? "No pump or uploader reports in this period."
                : "The latest report of each, and the lowest in the period as its note.",
            Context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(pump.Count + uploader.Count, "report")}.",
            Resolution = new ReportResolution(1, interval, sourceRows, rows.Count, rows.Count < sourceRows),
        };
    }

    /// <summary>The first snapshot in each slot of <paramref name="intervalMinutes"/>: real reports, none averaged.</summary>
    internal static IEnumerable<T> Thin<T>(IReadOnlyList<T> snapshots, Func<T, long> mills, long fromMills, int intervalMinutes)
    {
        var slot = intervalMinutes * 60_000L;
        return snapshots.GroupBy(s => Math.Max(0, mills(s) - fromMills) / slot).Select(g => g.First());
    }

    /// <summary>The latest reservoir and batteries, each with its lowest as the note, and the sensor age where read.</summary>
    internal static IReadOnlyList<ReportFact> FactsOf(IReadOnlyList<PumpSnapshot> pump, IReadOnlyList<UploaderSnapshot> uploader, DeviceAgeInfo? sensor)
    {
        var facts = new List<ReportFact>();
        var reservoir = pump.Where(p => p.Reservoir is not null).ToList();
        if (reservoir.Count > 0)
            facts.Add(new ReportFact("Reservoir", ReportFacts.Number(reservoir[^1].Reservoir!.Value), "U",
                $"lowest {ReportFacts.Number(reservoir.Min(p => p.Reservoir!.Value))} U"));
        var pumpBattery = pump.Where(p => p.BatteryPercent is not null).ToList();
        if (pumpBattery.Count > 0)
            facts.Add(new ReportFact("Pump battery", ReportFacts.Number(pumpBattery[^1].BatteryPercent!.Value, "0"), "%",
                $"lowest {ReportFacts.Number(pumpBattery.Min(p => p.BatteryPercent!.Value), "0")}%"));
        var uploaderBattery = uploader.Where(u => u.Battery is not null).ToList();
        if (uploaderBattery.Count > 0)
            facts.Add(new ReportFact("Uploader battery", ReportFacts.Number(uploaderBattery[^1].Battery!.Value, "0"), "%",
                $"lowest {ReportFacts.Number(uploaderBattery.Min(u => u.Battery!.Value), "0")}%"));
        if (sensor is not null)
            facts.Add(new ReportFact("Sensor age", $"{sensor.Days}d {sensor.Hours}h", null, "now"));
        return facts;
    }

    internal static string Summarise(
        IReadOnlyList<PumpSnapshot> pump,
        IReadOnlyList<UploaderSnapshot> uploader,
        DeviceAgeInfo? sensor,
        DateTimeOffset from,
        DateTimeOffset to,
        bool sensorAsked = false,
        bool clamped = false)
    {
        static string At(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        static string N(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Device status from {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} UTC."),
        };

        var reservoir = pump.Where(p => p.Reservoir is not null).ToList();
        lines.Add(reservoir.Count == 0
            ? "Reservoir: no reports in this period."
            : $"Reservoir: {N(reservoir[^1].Reservoir!.Value)} U at {At(reservoir[^1].Mills)}, lowest {N(reservoir.Min(p => p.Reservoir!.Value))} U.");
        var pumpBattery = pump.Where(p => p.BatteryPercent is not null).ToList();
        lines.Add(pumpBattery.Count == 0
            ? "Pump battery: no reports in this period."
            : $"Pump battery: {pumpBattery[^1].BatteryPercent}% at {At(pumpBattery[^1].Mills)}, lowest {pumpBattery.Min(p => p.BatteryPercent)}%.");
        var uploaderBattery = uploader.Where(u => u.Battery is not null).ToList();
        lines.Add(uploaderBattery.Count == 0
            ? "Uploader battery: no reports in this period."
            : $"Uploader battery: {uploaderBattery[^1].Battery}% at {At(uploaderBattery[^1].Mills)}, lowest {uploaderBattery.Min(u => u.Battery)}%.");
        if (sensor is not null)
            lines.Add($"Sensor age now: {sensor.Days} days {sensor.Hours} hours.");
        else if (sensorAsked)
            lines.Add(clamped
                ? "Sensor age now: not shown. This token reads only the last 24 hours, and the latest sensor start can be older."
                : "Sensor age now: no sensor start or change found.");
        return string.Join('\n', lines);
    }
}
