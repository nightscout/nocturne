using System.Globalization;
using System.Text.Json;

namespace Nocturne.Connectors.Glooko.Xt;

/// <summary>
///     The <c>EXPORT_RECORDS</c> answer, parsed. The export is the only place Glooko XT surfaces
///     what a synced pump reported beyond doses and readings: the automation mode and its
///     duration, boost and ease-off overrides, safety alerts, consumable changes and the pump's
///     settings snapshot. None of it appears in <c>GET_COLLECTED_DATA</c>.
/// </summary>
public sealed class GlookoXtExport
{
    /// <summary>The IANA zone the export's timestamps are written in, from its <c>TIMEZONE</c> line.</summary>
    public string? TimeZoneId { get; init; }

    public IReadOnlyList<GlookoXtExportRow> Rows { get; init; } = [];
}

/// <summary>One export line, with only the columns the connector reads.</summary>
public sealed class GlookoXtExportRow
{
    /// <summary>The row's minute, converted to UTC from the export's zone.</summary>
    public DateTime TimestampUtc { get; init; }

    public string? PumpDevice { get; init; }
    public string? SerialNumber { get; init; }

    /// <summary>The <c>Event</c> column, e.g. <c>pumpMode - Closed loop</c>, <c>pumpSettingsOverride - boost</c>.</summary>
    public string? Event { get; init; }

    /// <summary>The <c>Duration (ms)</c> column, when the row is a span.</summary>
    public long? DurationMs { get; init; }

    /// <summary>The <c>Settings</c> column: a JSON pump-settings snapshot.</summary>
    public string? SettingsJson { get; init; }
}

/// <summary>
///     Parses the semicolon-separated export. The file opens with a title, a <c>PERIOD</c> line,
///     a <c>TIMEZONE</c> line and a patient line, then the header row starting with <c>Date;</c>.
///     Dates are <c>dd/MM/yyyy HH:mm</c> in the export's zone. The settings column holds JSON
///     quoted the CSV way, with doubled inner quotes.
/// </summary>
public static class GlookoXtExportParser
{
    private const string DateFormat = "dd/MM/yyyy HH:mm";

    public static GlookoXtExport Parse(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new GlookoXtExport();

        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var tzLine = lines.FirstOrDefault(l => l.StartsWith("TIMEZONE;", StringComparison.OrdinalIgnoreCase));
        var tzId = tzLine?.Split(';').Skip(1).FirstOrDefault()?.Trim();
        var zone = ResolveZone(tzId);

        var headerIndex = lines.FindIndex(l => l.StartsWith("Date;", StringComparison.OrdinalIgnoreCase));
        if (headerIndex < 0) return new GlookoXtExport { TimeZoneId = tzId };

        var header = SplitRow(lines[headerIndex]);
        int Col(string name) => header.FindIndex(h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        var dateCol = Col("Date");
        var pumpCol = Col("Pump device");
        var serialCol = Col("Serial number");
        var durationCol = Col("Duration (ms)");
        var eventCol = Col("Event");
        var settingsCol = Col("Settings");

        var rows = new List<GlookoXtExportRow>();
        foreach (var line in lines.Skip(headerIndex + 1))
        {
            if (line.Length == 0) continue;
            var cells = SplitRow(line);
            if (dateCol < 0 || dateCol >= cells.Count) continue;

            if (!DateTime.TryParseExact(cells[dateCol].Trim(), DateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var local))
                continue;

            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);

            rows.Add(new GlookoXtExportRow
            {
                TimestampUtc = utc,
                PumpDevice = Cell(cells, pumpCol),
                SerialNumber = Cell(cells, serialCol),
                Event = Cell(cells, eventCol),
                DurationMs = long.TryParse(Cell(cells, durationCol), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : null,
                SettingsJson = Cell(cells, settingsCol),
            });
        }

        return new GlookoXtExport { TimeZoneId = tzId, Rows = rows };
    }

    private static string? Cell(List<string> cells, int index)
    {
        if (index < 0 || index >= cells.Count) return null;
        var v = cells[index].Trim();
        return v.Length == 0 ? null : v;
    }

    /// <summary>
    ///     Splits one CSV line on semicolons, honouring double-quoted cells with doubled inner
    ///     quotes — the settings JSON is written that way.
    /// </summary>
    public static List<string> SplitRow(string line)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ';') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }

        cells.Add(sb.ToString());
        return cells;
    }

    /// <summary>The zone named, or UTC when the name is missing or unknown to this host.</summary>
    public static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}

/// <summary>The pump-settings snapshot a <c>pump settings</c> export row carries.</summary>
public sealed class GlookoXtPumpSettings
{
    public DateTime? Time { get; init; }
    public string? DeviceId { get; init; }
    public string? Model { get; init; }
    public string? ActiveSchedule { get; init; }
    public string? BgUnit { get; init; }
    public bool? AutomatedDelivery { get; init; }

    /// <summary>Insulin action time, in seconds.</summary>
    public int? IobDurationSeconds { get; init; }

    public double? GlucoseTargetLow { get; init; }
    public double? GlucoseTargetHigh { get; init; }

    /// <summary>Schedule id to segments; each segment's start is milliseconds after midnight.</summary>
    public Dictionary<string, List<(int StartMs, double Value)>> BasalSchedules { get; init; } = new();
    public Dictionary<string, List<(int StartMs, double Value)>> CarbRatios { get; init; } = new();
    public Dictionary<string, List<(int StartMs, double Value)>> InsulinSensitivities { get; init; } = new();
    public Dictionary<string, List<(int StartMs, double Value)>> GlucoseTargets { get; init; } = new();

    public static GlookoXtPumpSettings? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;

            string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            return new GlookoXtPumpSettings
            {
                Time = DateTime.TryParse(Str("time"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null,
                DeviceId = Str("deviceId"),
                Model = Str("model"),
                ActiveSchedule = Str("activeSchedule"),
                BgUnit = r.TryGetProperty("units", out var units) && units.ValueKind == JsonValueKind.Object
                         && units.TryGetProperty("bg", out var bg) && bg.ValueKind == JsonValueKind.String ? bg.GetString() : null,
                AutomatedDelivery = r.TryGetProperty("automatedDelivery", out var ad) && ad.ValueKind is JsonValueKind.True or JsonValueKind.False ? ad.GetBoolean() : null,
                IobDurationSeconds = r.TryGetProperty("iobDuration", out var iob) && iob.ValueKind == JsonValueKind.Number ? (int)iob.GetDouble() : null,
                GlucoseTargetLow = Num(r, "glucoseTargetLow"),
                GlucoseTargetHigh = Num(r, "glucoseTargetHigh"),
                BasalSchedules = Schedules(r, "basalSchedules", "rate"),
                CarbRatios = Schedules(r, "carbRatios", "amount"),
                InsulinSensitivities = Schedules(r, "insulinSensitivities", "amount"),
                GlucoseTargets = Schedules(r, "glucoseTarget", "value"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>
    ///     A schedule's segments. A start is milliseconds after midnight on the basal, ratio and
    ///     sensitivity schedules and <c>HH:mm</c> on the glucose target; both read to milliseconds.
    /// </summary>
    private static Dictionary<string, List<(int, double)>> Schedules(JsonElement root, string name, string valueField)
    {
        var result = new Dictionary<string, List<(int, double)>>();
        if (!root.TryGetProperty(name, out var schedules) || schedules.ValueKind != JsonValueKind.Object) return result;

        foreach (var schedule in schedules.EnumerateObject())
        {
            if (schedule.Value.ValueKind != JsonValueKind.Array) continue;
            var segments = new List<(int, double)>();
            foreach (var seg in schedule.Value.EnumerateArray())
            {
                if (seg.ValueKind != JsonValueKind.Object) continue;
                var value = Num(seg, valueField);
                if (value is null || !seg.TryGetProperty("start", out var start)) continue;

                int? startMs = start.ValueKind switch
                {
                    JsonValueKind.Number => (int)start.GetDouble(),
                    JsonValueKind.String when TimeSpan.TryParseExact(start.GetString(), @"hh\:mm", CultureInfo.InvariantCulture, out var ts) => (int)ts.TotalMilliseconds,
                    JsonValueKind.String when int.TryParse(start.GetString(), out var ms) => ms,
                    _ => null,
                };
                if (startMs is null) continue;
                segments.Add((startMs.Value, value.Value));
            }

            result[schedule.Name] = segments.OrderBy(s => s.Item1).ToList();
        }

        return result;
    }
}
