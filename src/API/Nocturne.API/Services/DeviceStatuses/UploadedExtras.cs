using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.DeviceStatuses;

/// <summary>
/// The keys an <c>openaps</c> or <c>pump</c> object holds beyond the ones the typed models name,
/// kept in the snapshot's <c>additional_properties</c> because no typed column holds them. The
/// modelled fields no column holds either (<see cref="OpenApsIobData.Activity"/>, the pump's
/// <see cref="PumpStatus.Serial"/>, ...) are stored the same way and restored to their properties. The
/// stored object carries the top-level unknown keys, and the unknown keys of a nested object under
/// that object's own (modelled) name, so the two cannot collide.
/// </summary>
/// <remarks>
/// <c>openaps.suggested</c> and <c>openaps.enacted</c> are not here: their JSON columns already
/// hold every key of those objects.
/// </remarks>
internal static class UploadedExtras
{
    private const string OpenApsKey = "uploaded:openaps";
    private const string PumpKey = "uploaded:pump";
    private const string IobName = "iob";
    private const string BatteryName = "battery";
    private const string StatusName = "status";
    private const string SerialName = "serial";
    private const string ActivityName = "activity";
    private const string TimeName = "time";
    private const string TimestampName = "timestamp";
    private const string MillsName = "mills";
    private const string BasalIobName = "basaliob";

    public static Dictionary<string, object?>? OfOpenAps(OpenApsStatus openAps) =>
        Wrap(OpenApsKey, Pack(openAps.ExtensionData, (IobName, With(
            openAps.Iob?.ExtensionData,
            (ActivityName, openAps.Iob?.Activity),
            (TimeName, openAps.Iob?.Time),
            (TimestampName, openAps.Iob?.Timestamp),
            (MillsName, openAps.Iob?.Mills)))));

    public static void RestoreOpenAps(OpenApsStatus openAps, Dictionary<string, object?>? stored)
    {
        var (top, nested) = Unpack(stored, OpenApsKey, IobName);
        openAps.ExtensionData = top;
        if (nested[IobName] is { } iob)
        {
            openAps.Iob ??= new OpenApsIobData();
            openAps.Iob.Activity = TakeDouble(iob, ActivityName);
            openAps.Iob.Time = TakeString(iob, TimeName);
            openAps.Iob.Timestamp = TakeString(iob, TimestampName);
            openAps.Iob.Mills = TakeLong(iob, MillsName);
            openAps.Iob.ExtensionData = iob.Count > 0 ? iob : null;
        }
    }

    /// <summary>
    /// <see cref="PumpStatus.Extended"/> is stored flat in the same column, so the unknown keys
    /// go under a key of their own.
    /// </summary>
    public static Dictionary<string, object?>? OfPump(PumpStatus pump)
    {
        var extras = Wrap(PumpKey, Pack(
            With(pump.ExtensionData, (SerialName, pump.Serial)),
            (BatteryName, pump.Battery?.ExtensionData),
            (StatusName, pump.Status?.ExtensionData),
            (IobName, With(
                pump.Iob?.ExtensionData,
                (BasalIobName, pump.Iob?.BasalIob),
                (TimestampName, pump.Iob?.Timestamp)))));
        if (pump.Extended is not { Count: > 0 })
            return extras;

        var stored = pump.Extended.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);
        if (extras is not null)
            stored[PumpKey] = extras[PumpKey];
        return stored;
    }

    public static void RestorePump(PumpStatus pump, Dictionary<string, object?>? stored)
    {
        var (top, nested) = Unpack(stored, PumpKey, BatteryName, StatusName, IobName);
        pump.Serial = top is null ? null : TakeString(top, SerialName);
        pump.ExtensionData = top is { Count: > 0 } ? top : null;
        if (nested[BatteryName] is { } battery)
        {
            pump.Battery ??= new PumpBattery();
            pump.Battery.ExtensionData = battery;
        }

        if (nested[StatusName] is { } status)
        {
            pump.Status ??= new PumpStatusDetails();
            pump.Status.ExtensionData = status;
        }

        if (nested[IobName] is { } iob)
        {
            pump.Iob ??= new PumpIob();
            pump.Iob.BasalIob = TakeDouble(iob, BasalIobName);
            pump.Iob.Timestamp = TakeString(iob, TimestampName);
            pump.Iob.ExtensionData = iob.Count > 0 ? iob : null;
        }

        var extended = stored?
            .Where(kvp => kvp.Key != PumpKey)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value ?? (object)string.Empty);
        pump.Extended = extended is { Count: > 0 } ? extended : null;
    }

    private static Dictionary<string, JsonElement>? With(
        Dictionary<string, JsonElement>? data,
        params (string Name, object? Value)[] modelled)
    {
        var merged = data is null ? [] : new Dictionary<string, JsonElement>(data);
        foreach (var (name, value) in modelled)
        {
            if (value is not null)
                merged[name] = JsonSerializer.SerializeToElement(value);
        }

        return merged.Count > 0 ? merged : null;
    }

    private static double? TakeDouble(Dictionary<string, JsonElement> data, string name)
    {
        if (!data.TryGetValue(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number))
            return null;
        data.Remove(name);
        return number;
    }

    private static long? TakeLong(Dictionary<string, JsonElement> data, string name)
    {
        if (!data.TryGetValue(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var number))
            return null;
        data.Remove(name);
        return number;
    }

    private static string? TakeString(Dictionary<string, JsonElement> data, string name)
    {
        if (!data.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        data.Remove(name);
        return value.GetString();
    }

    private static JsonObject? Pack(
        Dictionary<string, JsonElement>? top,
        params (string Name, Dictionary<string, JsonElement>? Data)[] nested)
    {
        var packed = new JsonObject();
        foreach (var (name, data) in nested)
        {
            if (data is { Count: > 0 })
                packed[name] = JsonSerializer.SerializeToNode(data);
        }

        foreach (var (key, value) in top ?? [])
            packed[key] = JsonNode.Parse(value.GetRawText());

        return packed.Count > 0 ? packed : null;
    }

    private static Dictionary<string, object?>? Wrap(string key, JsonObject? packed) =>
        packed is null ? null : new Dictionary<string, object?> { [key] = packed };

    private static (Dictionary<string, JsonElement>? Top, Dictionary<string, Dictionary<string, JsonElement>?> Nested) Unpack(
        Dictionary<string, object?>? stored, string key, params string[] nestedNames)
    {
        var nested = nestedNames.ToDictionary(name => name, _ => (Dictionary<string, JsonElement>?)null);
        if (stored is null || !stored.TryGetValue(key, out var value) || value is null)
            return (null, nested);

        var element = JsonSerializer.SerializeToElement(value);
        if (element.ValueKind != JsonValueKind.Object)
            return (null, nested);

        var top = new Dictionary<string, JsonElement>();
        foreach (var property in element.EnumerateObject())
        {
            if (nested.ContainsKey(property.Name) && property.Value.ValueKind == JsonValueKind.Object)
                nested[property.Name] = property.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            else
                top[property.Name] = property.Value.Clone();
        }

        return (top.Count > 0 ? top : null, nested);
    }
}
