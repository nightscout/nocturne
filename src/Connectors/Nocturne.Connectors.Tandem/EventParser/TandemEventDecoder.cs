using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Connectors.Tandem.Models;

namespace Nocturne.Connectors.Tandem.EventParser;

/// <summary>
/// Decodes Tandem pump events into <see cref="TandemPumpEvent"/> records, from either the
/// server-decoded JSON of the pump-logs endpoint (<see cref="Decode(TandemPumpLogEvent, ILogger?)"/>)
/// or the legacy base64 binary payload of the retired reportsfacade endpoint. Mirrors
/// <c>tconnectsync</c> v3's <c>eventparser</c> (which likewise accepts both formats): the 26-byte
/// big-endian header, the normalized-name matching of <c>eventProperties</c> keys, and the
/// per-field transforms — but is driven by <see cref="TandemEventCatalog"/>.
/// </summary>
public static partial class TandemEventDecoder
{
    /// <summary>
    /// Decodes one pump-logs JSON event. The server pre-decodes fields into
    /// <c>eventProperties</c> with <b>raw</b> values, so the schema transforms (ratio scaling,
    /// enum/dictionary names, bitmask bits) still apply; property keys are camelCase and are
    /// matched to schema field names by normalization (tconnectsync's <c>_norm</c>). Unknown event
    /// codes are still returned (with no decoded fields) so callers can count them.
    /// </summary>
    public static TandemPumpEvent Decode(TandemPumpLogEvent logEvent, ILogger? logger = null)
    {
        // pumpDateTime is the pump's local wall-clock with no tz. Parsing it as if UTC reproduces
        // the binary header's raw value (local wall-clock seconds since the Tandem epoch), so
        // TandemTimeResolver applies the configured offset identically on both paths.
        long rawTimestampSeconds = 0;
        if (DateTimeOffset.TryParse(
                logEvent.PumpDateTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var wallClock))
            rawTimestampSeconds = wallClock.ToUnixTimeSeconds() - TandemConstants.TandemEpochUnixSeconds;
        else
            logger?.LogDebug(
                "Unparseable pumpDateTime '{PumpDateTime}' on event code {Code}",
                logEvent.PumpDateTime, logEvent.EventCode);

        if (!TandemEventCatalog.Definitions.TryGetValue(logEvent.EventCode, out var definition))
            return new TandemPumpEvent
            {
                Id = logEvent.EventCode,
                Name = "RawEvent",
                Source = 0,
                SeqNum = logEvent.SequenceNumber,
                SequenceGroup = logEvent.SequenceGroup,
                RawTimestampSeconds = rawTimestampSeconds,
                IsKnown = false,
            };

        var props = new Dictionary<string, JsonElement>(logEvent.EventProperties.Count);
        foreach (var (key, value) in logEvent.EventProperties)
            props[Norm(key)] = value;

        var fields = new Dictionary<string, TandemFieldValue>(definition.Fields.Count);
        foreach (var field in definition.Fields)
            if (props.TryGetValue(Norm(field.Name), out var value)
                && TryDecodeJsonField(field, value, logger) is { } decoded)
                fields[field.Name] = decoded;

        return new TandemPumpEvent
        {
            Id = logEvent.EventCode,
            Name = definition.Name,
            Source = 0,
            SeqNum = logEvent.SequenceNumber,
            SequenceGroup = logEvent.SequenceGroup,
            RawTimestampSeconds = rawTimestampSeconds,
            IsKnown = true,
            Fields = fields,
        };
    }

    private static TandemFieldValue? TryDecodeJsonField(
        TandemFieldDefinition field, JsonElement value, ILogger? logger)
    {
        var isBitmask = field.Transforms.Any(t => t.Kind == TandemTransformKind.Bitmask);

        double numeric;
        long rawInteger;
        var isFloat = field.Type == TandemFieldType.Float32;

        if (isBitmask && value.ValueKind == JsonValueKind.Array)
        {
            // Bitmask fields arrive as arrays of set-bit indices (tconnectsync's
            // _bitmask_arr_to_int); fold them back into the integer the transforms expect.
            rawInteger = 0;
            foreach (var bit in value.EnumerateArray())
                if (TryGetNumber(bit, out var bitNumeric, out _))
                    rawInteger |= 1L << (int)bitNumeric;
            numeric = rawInteger;
            isFloat = false;
        }
        else if (TryGetNumber(value, out numeric, out rawInteger))
        {
            if (isFloat)
                rawInteger = 0;
        }
        else
        {
            logger?.LogDebug(
                "Unusable eventProperties value of kind {Kind} for field {Field}; skipping",
                value.ValueKind, field.Name);
            return null;
        }

        return ApplyTransforms(field, numeric, rawInteger, isFloat);
    }

    private static bool TryGetNumber(JsonElement value, out double numeric, out long rawInteger)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                numeric = value.GetDouble();
                rawInteger = value.TryGetInt64(out var l) ? l : (long)numeric;
                return true;
            case JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                numeric = parsed;
                rawInteger = (long)parsed;
                return true;
            case JsonValueKind.True or JsonValueKind.False:
                rawInteger = value.ValueKind == JsonValueKind.True ? 1 : 0;
                numeric = rawInteger;
                return true;
            default:
                numeric = 0;
                rawInteger = 0;
                return false;
        }
    }

    /// <summary>Normalizes a schema field name or JSON property key for matching: lowercase, alphanumerics only.</summary>
    internal static string Norm(string name) => NormRegex().Replace(name.ToLowerInvariant(), "");

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NormRegex();

    /// <summary>
    /// Decodes a legacy base64 events blob (the retired reportsfacade payload) into the contained
    /// events. Unknown event ids are still returned (with no decoded fields) so callers can count
    /// them; recognised ids carry decoded fields.
    /// </summary>
    public static IReadOnlyList<TandemPumpEvent> Decode(string? base64Events, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(base64Events))
            return [];

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Events);
        }
        catch (FormatException ex)
        {
            logger?.LogWarning(ex, "Failed to base64-decode Tandem pump events");
            return [];
        }

        var len = TandemConstants.EventLength;
        var events = new List<TandemPumpEvent>(bytes.Length / len);

        for (var offset = 0; offset + len <= bytes.Length; offset += len)
        {
            var record = bytes.AsSpan(offset, len);
            events.Add(DecodeRecord(record, logger));
        }

        return events;
    }

    private static TandemPumpEvent DecodeRecord(ReadOnlySpan<byte> record, ILogger? logger)
    {
        var sourceAndId = BinaryPrimitives.ReadUInt16BigEndian(record);
        var source = (sourceAndId & 0xF000) >> 12;
        var id = sourceAndId & 0x0FFF;
        var timestampRaw = BinaryPrimitives.ReadUInt32BigEndian(record[2..]);
        var seqNum = BinaryPrimitives.ReadUInt32BigEndian(record[6..]);

        if (!TandemEventCatalog.Definitions.TryGetValue(id, out var definition))
            return new TandemPumpEvent
            {
                Id = id,
                Name = "RawEvent",
                Source = source,
                SeqNum = seqNum,
                RawTimestampSeconds = timestampRaw,
                IsKnown = false,
            };

        var fields = new Dictionary<string, TandemFieldValue>(definition.Fields.Count);
        foreach (var field in definition.Fields)
            if (TryDecodeField(record, field, logger) is { } value)
                fields[field.Name] = value;

        return new TandemPumpEvent
        {
            Id = id,
            Name = definition.Name,
            Source = source,
            SeqNum = seqNum,
            RawTimestampSeconds = timestampRaw,
            IsKnown = true,
            Fields = fields,
        };
    }

    private static TandemFieldValue? TryDecodeField(
        ReadOnlySpan<byte> record, TandemFieldDefinition field, ILogger? logger)
    {
        var pos = TandemConstants.EventHeaderSize + field.Offset;
        var size = FieldSize(field.Type);
        if (pos + size > record.Length)
        {
            logger?.LogDebug(
                "Field {Field} at offset {Offset} exceeds event payload; skipping", field.Name, field.Offset);
            return null;
        }

        var slice = record[pos..];
        var (numeric, rawInteger, isFloat) = ReadPrimitive(slice, field.Type);

        return ApplyTransforms(field, numeric, rawInteger, isFloat);
    }

    private static TandemFieldValue ApplyTransforms(
        TandemFieldDefinition field, double numeric, long rawInteger, bool isFloat)
    {
        string? name = null;
        IReadOnlyList<string> bits = [];

        foreach (var transform in field.Transforms)
            switch (transform.Kind)
            {
                case TandemTransformKind.Ratio:
                    numeric = (isFloat ? numeric : rawInteger) * transform.RatioFactor;
                    break;
                case TandemTransformKind.Enum:
                    if (transform.Map != null && transform.Map.TryGetValue((int)rawInteger, out var enumName))
                        name = enumName;
                    break;
                case TandemTransformKind.Dictionary:
                    var table = transform.DictionaryName != null
                        ? TandemEventDictionaries.ForName(transform.DictionaryName)
                        : null;
                    if (table != null && table.TryGetValue((int)rawInteger, out var dictName))
                        name = dictName;
                    break;
                case TandemTransformKind.Bitmask:
                    bits = ResolveBits(rawInteger, transform.Map);
                    break;
                case TandemTransformKind.BatteryChargePercent:
                    // Computed from sibling MSB/LSB raw fields by the device-status mapper; no-op here.
                    break;
            }

        return new TandemFieldValue
        {
            Numeric = numeric,
            RawInteger = rawInteger,
            Name = name,
            Bits = bits,
        };
    }

    private static IReadOnlyList<string> ResolveBits(long rawValue, IReadOnlyDictionary<int, string>? map)
    {
        if (map == null)
            return [];

        var bits = new List<string>();
        for (var bit = 0; bit < 32; bit++)
            if ((rawValue & (1L << bit)) != 0 && map.TryGetValue(bit, out var name))
                bits.Add(name);

        return bits;
    }

    private static (double Numeric, long RawInteger, bool IsFloat) ReadPrimitive(
        ReadOnlySpan<byte> slice, TandemFieldType type) => type switch
    {
        TandemFieldType.UInt8 => (slice[0], slice[0], false),
        TandemFieldType.Int8 => ((sbyte)slice[0], (sbyte)slice[0], false),
        TandemFieldType.UInt16 => Int(BinaryPrimitives.ReadUInt16BigEndian(slice)),
        TandemFieldType.Int16 => Int(BinaryPrimitives.ReadInt16BigEndian(slice)),
        TandemFieldType.UInt32 => Int(BinaryPrimitives.ReadUInt32BigEndian(slice)),
        TandemFieldType.Float32 => (BinaryPrimitives.ReadSingleBigEndian(slice), 0L, true),
        _ => (0d, 0L, false),
    };

    private static (double, long, bool) Int(long value) => (value, value, false);

    private static int FieldSize(TandemFieldType type) => type switch
    {
        TandemFieldType.UInt8 or TandemFieldType.Int8 => 1,
        TandemFieldType.UInt16 or TandemFieldType.Int16 => 2,
        TandemFieldType.UInt32 or TandemFieldType.Float32 => 4,
        _ => 0,
    };
}
