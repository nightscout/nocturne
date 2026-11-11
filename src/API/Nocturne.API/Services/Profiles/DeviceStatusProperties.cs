using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.API.Configuration;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Profiles;

/// <summary>
/// The <c>pump</c>, <c>loop</c> and <c>openaps</c> properties, as Nightscout's <c>pump.js</c>,
/// <c>loop.js</c> and <c>openaps.js</c> offer them, read from the device statuses as
/// <c>/api/v1/devicestatus</c> serves them.
/// </summary>
/// <remarks>
/// Nightscout's plugins stamp <c>moment</c> fields onto the statuses they read, and the pump
/// property returns a whole status, so it carries the stamps the loop and openaps plugins left on
/// it. <see cref="Build"/> reads one set of documents in Nightscout's plugin order and clones each
/// result only at the end, so every stamp lands where Nightscout's does.
/// </remarks>
internal static class DeviceStatusProperties
{
    private static readonly JsonSerializerOptions DocumentJson = NightscoutJsonOptions.Create();

    private const long Minute = 60_000;
    private const long RecentMills = 6 * 60 * Minute;
    private const int LoopWarnMinutes = 30;
    private const int OpenApsWarnMinutes = 30;

    public sealed record Result(JsonObject Pump, JsonObject Loop, JsonObject OpenAps);

    /// <param name="statuses">The device statuses to read; those older than the recent window are left out.</param>
    /// <param name="now">The time the properties are built for, in Unix milliseconds.</param>
    /// <param name="prefs">The pump preferences; its urgent clock threshold can widen the recent window.</param>
    /// <param name="offline">Whether an <c>OpenAPS Offline</c> treatment covers <paramref name="now"/>.</param>
    public static Result Build(IEnumerable<DeviceStatus> statuses, long now, PumpPreferences prefs, bool offline)
    {
        var oldest = now - Math.Max(RecentMills, prefs.UrgentClock * 2 * Minute);
        var documents = statuses
            .Where(s => s.Mills >= oldest)
            .OrderBy(s => s.Mills)
            .Select(Document)
            .ToList();

        var loop = Loop(documents, now);
        var openAps = OpenAps(documents, now);
        var pump = Pump(documents, now, prefs, offline);
        return new Result(pump, loop, openAps);
    }

    /// <summary>
    /// The status as <c>/api/v1/devicestatus</c> serves it, with the battery fields Nightscout's
    /// <c>upbat.js</c> stamps onto its uploader (see <see cref="PropertiesService.UpbatLevel"/>).
    /// </summary>
    private static JsonObject Document(DeviceStatus status)
    {
        var document = JsonSerializer.SerializeToNode(status, DocumentJson)!.AsObject();
        if (status.Uploader?.Battery is not null
            && document["uploader"] is JsonObject uploader
            && PropertiesService.ToBatteryReading(status) is var reading
            && PropertiesService.UpbatLevel(reading) is { } level)
        {
            uploader["value"] = status.Uploader.Battery;
            uploader["display"] = PropertiesService.UpbatDisplay(reading);
            uploader["level"] = level;
        }
        return document;
    }

    private static JsonObject Loop(List<JsonObject> documents, long now)
    {
        var recent = now - LoopWarnMinutes / 2 * Minute;
        JsonObject? lastLoop = null, lastEnacted = null, lastPredicted = null, lastOverride = null, newestLoopOverride = null;
        long lastLoopMoment = 0, lastEnactedMoment = 0, lastOverrideMoment = 0;
        long? lastOkMoment = null;

        foreach (var status in Recent(documents, "loop", now, RecentMills))
        {
            if (status["loop"] is not JsonObject loop || Moment(loop["timestamp"]) is not { } moment)
                continue;
            loop["moment"] = Iso(moment);

            if (loop["enacted"] is JsonObject enacted && Moment(enacted["timestamp"]) is { } enactedMoment)
            {
                enacted["moment"] = Iso(enactedMoment);
                if (lastEnacted is null || enactedMoment > lastEnactedMoment)
                    (lastEnacted, lastEnactedMoment) = (enacted, enactedMoment);
            }

            if (lastLoop is null || moment > lastLoopMoment)
                (lastLoop, lastLoopMoment, newestLoopOverride) = (loop, moment, status["override"] as JsonObject);

            if (loop["predicted"] is JsonObject predicted && Truthy(predicted["startDate"]))
                lastPredicted = predicted;

            if (status["override"] is JsonObject loopOverride
                && Truthy(loopOverride["timestamp"])
                && Moment(loopOverride["timestamp"]) is { } overrideMoment)
            {
                loopOverride["moment"] = Iso(overrideMoment);
                if (lastOverride is null || overrideMoment > lastOverrideMoment)
                    (lastOverride, lastOverrideMoment) = (loopOverride, overrideMoment);
            }

            if (!Truthy(loop["failureReason"]) && (lastOkMoment is null || moment > lastOkMoment))
                lastOkMoment = moment;
        }

        var result = new JsonObject
        {
            ["lastLoop"] = lastLoop?.DeepClone(),
            ["lastEnacted"] = lastEnacted?.DeepClone(),
            ["lastPredicted"] = lastPredicted?.DeepClone(),
            ["lastOkMoment"] = lastOkMoment is { } ok ? Iso(ok) : null,
        };
        // The projection never serves an ended (active: false) override, so an override seen only on an older
        // status may have ended since; serve it only while the newest Loop status still carries it.
        if (lastOverride is not null && ReferenceEquals(lastOverride, newestLoopOverride))
            result["lastOverride"] = lastOverride.DeepClone();
        result["display"] = LoopDisplay(lastLoop, lastLoopMoment, recent);
        return result;
    }

    private static JsonObject LoopDisplay(JsonObject? loop, long moment, long recent)
    {
        var (symbol, code, label) =
            loop is null ? ("⚠", "warning", "Warning")
            : Truthy(loop["failureReason"])
                || (loop["enacted"] is JsonObject enacted && !Truthy(enacted["received"]))
                ? ("x", "error", "Error")
            : loop["enacted"] is not null && Moment(loop["timestamp"]) > recent ? ("⌁", "enacted", "Enacted")
            : loop["recommendedTempBasal"] is JsonObject recommended && Moment(recommended["timestamp"]) > recent
                // NIGHTSCOUT-COMPAT: Nightscout's spelling.
                ? ("⏀", "recommendation", "Recomendation")
            : moment > recent ? ("↻", "looping", "Looping")
            : ("⚠", "warning", "Warning");

        return new JsonObject { ["symbol"] = symbol, ["code"] = code, ["label"] = label };
    }

    private sealed class Stamped(JsonObject node, long moment)
    {
        public JsonObject Node { get; } = node;
        public long Moment { get; } = moment;
    }

    private static JsonObject OpenAps(List<JsonObject> documents, long now)
    {
        var recent = now - OpenApsWarnMinutes / 2 * Minute;
        var seenDevices = new JsonObject();
        var deviceMoments = new Dictionary<string, long>();
        Stamped? lastEnacted = null, lastNotEnacted = null, lastSuggested = null, lastIob = null, lastPredBgs = null;

        foreach (var status in Recent(documents, "openaps", now, RecentMills))
        {
            if (status["openaps"] is not JsonObject openAps)
                continue;
            if (openAps["iob"] is JsonObject storedIob)
            {
                if (Truthy(storedIob["time"]))
                    storedIob["timestamp"] = storedIob["time"]!.DeepClone();
                // NIGHTSCOUT-COMPAT: a stored status keeps its OpenAPS IOB without the time the
                // uploader gave it, which is the status's own.
                else if (!Truthy(storedIob["timestamp"]) && !Truthy(storedIob["mills"]))
                    storedIob["timestamp"] = status["created_at"]?.DeepClone();
            }

            var enactedNode = openAps["enacted"] as JsonObject;
            var received = Truthy(enactedNode?["recieved"]) || Truthy(enactedNode?["received"]);
            var commandMoment = enactedNode is not null && Truthy(enactedNode["timestamp"])
                ? Moment(Truthy(enactedNode["mills"]) ? enactedNode["mills"] : enactedNode["timestamp"])
                : null;
            var enactedMoment = received ? commandMoment : null;
            var notEnactedMoment = received ? null : commandMoment;
            var suggestedNode = openAps["suggested"] as JsonObject;
            var suggestedMoment = CommandMoment(suggestedNode);
            var iobNode = openAps["iob"] as JsonObject;
            var iobMoment = CommandMoment(iobNode);
            var when = Moment(status["mills"]) ?? 0;

            var uri = status["device"] is JsonValue device && Truthy(device) ? device.ToString() : "device";
            if (seenDevices[uri] is not JsonObject seen)
            {
                seen = new JsonObject { ["name"] = DeviceName(uri), ["uri"] = uri };
                seenDevices[uri] = seen;
            }
            if (seen["status"] is null || when > deviceMoments[uri])
            {
                var loopStatus = OpenApsStatus(enactedMoment, notEnactedMoment, suggestedMoment, recent, noWarning: true);
                loopStatus["when"] = Iso(when);
                seen["status"] = loopStatus;
                deviceMoments[uri] = when;
            }

            if (enactedNode is not null && enactedMoment is { } enacted
                && (lastEnacted is null || enacted > lastEnacted.Moment))
            {
                enactedNode["moment"] = Iso(enacted);
                lastEnacted = new Stamped(enactedNode, enacted);
                lastPredBgs = PredBgs(enactedNode, enacted, lastPredBgs);
            }

            if (enactedNode is not null && notEnactedMoment is { } notEnacted
                && (lastNotEnacted is null || notEnacted > lastNotEnacted.Moment))
            {
                enactedNode["moment"] = Iso(notEnacted);
                lastNotEnacted = new Stamped(enactedNode, notEnacted);
            }

            if (suggestedNode is not null && suggestedMoment is { } suggested
                && (lastSuggested is null || suggested > lastSuggested.Moment))
            {
                suggestedNode["moment"] = Iso(suggested);
                lastSuggested = new Stamped(suggestedNode, suggested);
                lastPredBgs = PredBgs(suggestedNode, suggested, lastPredBgs);
            }

            if (iobNode is not null && iobMoment is { } iob
                && (lastIob is null || Moment(iobNode["timestamp"]) > lastIob.Moment))
            {
                iobNode["moment"] = Iso(iob);
                lastIob = new Stamped(iobNode, iob);
            }

            if (status["mmtune"] is JsonObject mmtune && Moment(mmtune["timestamp"]) is { } tuned)
            {
                mmtune["moment"] = Iso(tuned);
                if (seen["mmtune"] is not JsonObject known || when > Moment(known["moment"]))
                    seen["mmtune"] = mmtune.DeepClone();
            }
        }

        var result = new JsonObject
        {
            ["seenDevices"] = seenDevices,
            ["lastEnacted"] = lastEnacted?.Node.DeepClone(),
            ["lastNotEnacted"] = lastNotEnacted?.Node.DeepClone(),
            ["lastSuggested"] = lastSuggested?.Node.DeepClone(),
            ["lastIOB"] = lastIob?.Node.DeepClone(),
            ["lastMMTune"] = null,
            ["lastPredBGs"] = lastPredBgs?.Node.DeepClone(),
        };

        var lastLoop = lastEnacted is not null && lastSuggested is not null
            ? lastEnacted.Moment > lastSuggested.Moment ? lastEnacted : lastSuggested
            : lastEnacted ?? lastSuggested;
        if (lastLoop is not null)
        {
            result["lastLoopMoment"] = Iso(lastLoop.Moment);
            if (lastLoop.Node["eventualBG"] is { } eventualBg)
                result["lastEventualBG"] = eventualBg.DeepClone();
        }

        result["status"] = OpenApsStatus(
            lastEnacted?.Moment, lastNotEnacted?.Moment, lastSuggested?.Moment, recent, noWarning: false);
        return result;
    }

    private static long? CommandMoment(JsonObject? command) =>
        command is null ? null
        : Truthy(command["mills"]) ? Moment(command["mills"])
        : Truthy(command["timestamp"]) ? Moment(command["timestamp"])
        : null;

    private static Stamped? PredBgs(JsonObject command, long moment, Stamped? last)
    {
        if (command["predBGs"] is not JsonObject predBgs || (last is not null && moment <= last.Moment))
            return last;

        predBgs["moment"] = Iso(moment);
        return new Stamped(predBgs, moment);
    }

    private static JsonObject OpenApsStatus(
        long? enacted, long? notEnacted, long? suggested, long recent, bool noWarning)
    {
        var (symbol, code, label) =
            notEnacted is { } failed && (enacted is { } done ? failed > done : failed > recent)
                ? ("x", "notenacted", "Not Enacted")
            : enacted > recent ? ("⌁", "enacted", "Enacted")
            : suggested > recent ? ("↻", "looping", "Looping")
            : noWarning ? ("◉", "waiting", "Waiting")
            : ("⚠", "warning", "Warning");

        return new JsonObject { ["symbol"] = symbol, ["code"] = code, ["label"] = label };
    }

    private static string DeviceName(string uri) => uri.Split("://")[^1].Split('/')[0];

    private static JsonObject Pump(List<JsonObject> documents, long now, PumpPreferences prefs, bool offline)
    {
        JsonObject? latest = null;
        long latestClock = 0;
        foreach (var status in Recent(documents, "pump", now, prefs.UrgentClock * 2 * Minute))
        {
            var clockMills = Truthy(status["pump"]?["clock"])
                ? Moment(status["pump"]!["clock"]) ?? 0
                : Moment(status["mills"]) ?? 0;
            status["clockMills"] = clockMills;
            if (latest is null || clockMills > latestClock)
                (latest, latestClock) = (status, clockMills);
        }

        var result = latest?.DeepClone().AsObject() ?? new JsonObject();
        result["data"] = PumpData(latest, now, prefs, offline);
        return result;
    }

    private static JsonObject PumpData(JsonObject? status, long now, PumpPreferences prefs, bool offline)
    {
        var pump = status?["pump"] as JsonObject ?? new JsonObject();
        var data = new JsonObject
        {
            ["level"] = DeviceAgeLevel.None,
            ["clock"] = null,
            ["reservoir"] = null,
            ["reservoir_display_override"] = Truthy(pump["reservoir_display_override"])
                ? pump["reservoir_display_override"]!.DeepClone()
                : null,
            ["reservoir_level_override"] = null,
        };
        if (pump["manufacturer"] is { } manufacturer)
            data["manufacturer"] = manufacturer.DeepClone();
        if (pump["model"] is { } model)
            data["model"] = model.DeepClone();
        data["extended"] = Truthy(pump["extended"]) ? pump["extended"]!.DeepClone() : null;

        if (Truthy(pump["clock"]) && Moment(pump["clock"]) is { } clock)
            data["clock"] = Clock(clock, now, prefs);

        if (pump["reservoir"] is JsonValue reservoir && reservoir.TryGetValue(out double units))
            data["reservoir"] = Reservoir(units, prefs);
        else if (pump["manufacturer"]?.ToString() == "Insulet")
            data["reservoir"] = new JsonObject { ["label"] = "Reservoir", ["display"] = "50+ U" };
        if (data["reservoir"] is JsonObject shown && data["reservoir_display_override"] is { } display)
            shown["display"] = display.DeepClone();

        if (pump["status"] is JsonObject pumpStatus)
            data["status"] = Status(pumpStatus, prefs);

        if (pump["battery"] is JsonObject battery)
        {
            if (Truthy(battery["percent"]))
                data["battery"] = Battery(battery["percent"]!, "percent", "%", prefs.UrgentBattP, prefs.WarnBattP);
            else if (Truthy(battery["voltage"]))
                data["battery"] = Battery(battery["voltage"]!, "volts", "v", prefs.UrgentBattV, prefs.WarnBattV);
        }

        data["device"] = new JsonObject { ["label"] = "Device" };
        if (status?["device"] is { } device)
            data["device"]!["display"] = device.DeepClone();

        data["title"] = "Pump Status";
        if (!offline)
        {
            var level = DeviceAgeLevel.None;
            foreach (var field in new[] { "reservoir", "battery", "clock", "status", "device" })
            {
                if (data[field]?["level"] is JsonValue fieldLevel
                    && fieldLevel.TryGetValue(out int value)
                    && value > level)
                {
                    level = value;
                    data["title"] = data[field]!["message"]?.DeepClone();
                }
            }
            data["level"] = level;
        }

        if (data["level"]!.GetValue<int>() > DeviceAgeLevel.None)
        {
            var lines = new List<string>();
            if (data["battery"] is JsonObject batteryField)
                lines.Add($"Pump Battery: {batteryField["display"]}");
            if (data["reservoir"] is JsonObject reservoirField)
                lines.Add($"Pump Reservoir: {reservoirField["display"]}");
            data["message"] = string.Join("\n", lines);
        }

        return data;
    }

    private static JsonObject Clock(long clock, long now, PumpPreferences prefs)
    {
        var field = new JsonObject
        {
            ["value"] = Iso(clock),
            ["label"] = "Last Clock",
            ["display"] = TimeAgo(clock, now),
            ["level"] = DeviceAgeLevel.None,
        };
        if (clock < now - prefs.UrgentClock * Minute)
            Raise(field, DeviceAgeLevel.Urgent, "URGENT: Pump data stale");
        else if (clock < now - prefs.WarnClock * Minute)
            Raise(field, DeviceAgeLevel.Warn, "Warning, Pump data stale");
        return field;
    }

    private static JsonObject Reservoir(double units, PumpPreferences prefs)
    {
        var field = new JsonObject
        {
            ["value"] = units,
            ["label"] = "Reservoir",
            ["display"] = ToPrecision3(units) + "U",
            ["level"] = DeviceAgeLevel.None,
        };
        if (units < prefs.UrgentRes)
            Raise(field, DeviceAgeLevel.Urgent, "URGENT: Pump Reservoir Low");
        else if (units < prefs.WarnRes)
            Raise(field, DeviceAgeLevel.Warn, "Warning, Pump Reservoir Low");
        return field;
    }

    private static JsonObject Status(JsonObject pumpStatus, PumpPreferences prefs)
    {
        var value = Truthy(pumpStatus["bolusing"]) ? "bolusing"
            : Truthy(pumpStatus["suspended"]) ? "suspended"
            : Truthy(pumpStatus["status"]) ? pumpStatus["status"]!.ToString()
            : "normal";
        var field = new JsonObject { ["value"] = value, ["display"] = value, ["label"] = "Status" };
        if (prefs.WarnOnSuspend && Truthy(pumpStatus["suspended"]))
            Raise(field, DeviceAgeLevel.Warn, "Pump Suspended");
        return field;
    }

    private static JsonObject Battery(JsonNode value, string unit, string suffix, double urgent, double warn)
    {
        var amount = value.GetValue<double>();
        var field = new JsonObject
        {
            ["value"] = value.DeepClone(),
            ["unit"] = unit,
            ["label"] = "Battery",
            ["display"] = amount.ToString(CultureInfo.InvariantCulture) + suffix,
            ["level"] = DeviceAgeLevel.None,
        };
        if (amount < urgent)
            Raise(field, DeviceAgeLevel.Urgent, "URGENT: Pump Battery Low");
        else if (amount < warn)
            Raise(field, DeviceAgeLevel.Warn, "Warning, Pump Battery Low");
        return field;
    }

    private static void Raise(JsonObject field, int level, string message)
    {
        field["level"] = level;
        field["message"] = message;
    }

    /// <summary>
    /// Nightscout's pump clock display: <c>timeago.calcDisplay</c>'s rounded value and short label,
    /// with " ago" after a one-letter label.
    /// </summary>
    private static string TimeAgo(long mills, long now)
    {
        var delta = now - mills;
        if (delta < -5 * Minute)
            return "future";
        if (delta < 0)
            return "1m ago";

        var (unit, label) = delta < 60 * Minute ? (Minute, "m")
            : delta < 24 * 60 * Minute ? (60 * Minute, "h")
            : delta < 7 * 24 * 60 * Minute ? (24 * 60 * Minute, "d")
            : (0L, "ago");
        if (unit == 0)
            return label;
        var value = Math.Max(1, (long)Math.Round((double)delta / unit, MidpointRounding.AwayFromZero));
        return $"{value}{label} ago";
    }

    /// <summary>JavaScript's <c>value.toPrecision(3)</c>.</summary>
    internal static string ToPrecision3(double value)
    {
        if (value == 0)
            return "0.00";

        var exponent = (int)Math.Floor(Math.Log10(Math.Abs(value)));
        if (exponent is >= -6 and < 3 && Math.Abs(ScaledRounding.Round(value, 2 - exponent, MidpointRounding.AwayFromZero)) >= Math.Pow(10, exponent + 1))
            exponent++;
        if (exponent is < -6 or >= 3)
            return $"{ToFixed(value / Math.Pow(10, exponent), 2)}e{(exponent < 0 ? "-" : "+")}{Math.Abs(exponent)}";
        return ToFixed(value, 2 - exponent);
    }

    private static string ToFixed(double value, int digits) =>
        RoundLikeJs(value, digits).ToString("F" + digits, CultureInfo.InvariantCulture);

    /// <summary>
    /// Rounds an exact tie away from zero, as JavaScript does, where .NET's formatting rounds it to
    /// even (see <see cref="JsNumber.ToFixed2"/>). A double can only hold a tie at
    /// <paramref name="digits"/> decimals as an odd multiple of 2^-(digits+1).
    /// </summary>
    private static double RoundLikeJs(double value, int digits) =>
        Math.IEEERemainder(value * Math.Pow(2, digits + 1), 2) is 1 or -1
            ? ScaledRounding.Round(value, digits, MidpointRounding.AwayFromZero)
            : value;

    private static IEnumerable<JsonObject> Recent(List<JsonObject> documents, string key, long now, long window) =>
        documents.Where(d =>
            d.ContainsKey(key) && Moment(d["mills"]) is { } mills && mills <= now && mills >= now - window);

    private static long? Moment(JsonNode? value)
    {
        if (value is not JsonValue json)
            return null;
        if (json.TryGetValue(out long mills))
            return mills;
        if (json.TryGetValue(out double fractional))
            return (long)fractional;
        return json.TryGetValue(out string? text) && UploaderTimestamp.TryParse(text, out var time)
            ? time.ToUnixTimeMilliseconds()
            : null;
    }

    private static string Iso(long mills) =>
        DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static bool Truthy(JsonNode? value) =>
        value switch
        {
            null => false,
            JsonValue v when v.TryGetValue(out bool b) => b,
            JsonValue v when v.TryGetValue(out double d) => d != 0 && !double.IsNaN(d),
            JsonValue v when v.TryGetValue(out string? s) => !string.IsNullOrEmpty(s),
            _ => true,
        };
}
