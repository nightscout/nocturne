using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tandem.Configurations;

namespace Nocturne.Connectors.Tandem.EventParser;

/// <summary>
/// Decodes the base64 pump-event payload returned by the Tandem Source API into
/// <see cref="TandemPumpEvent"/> records. Mirrors <c>tconnectsync</c>'s
/// <c>eventparser/raw_event.py</c> (the 26-byte big-endian header) and the generated event
/// classes (per-field decode and transforms), but is driven by <see cref="TandemEventCatalog"/>.
/// </summary>
public static class TandemEventDecoder
{
    /// <summary>
    /// Decodes a base64 events blob into the contained events. Unknown event ids are still returned
    /// (with no decoded fields) so callers can count them; recognised ids carry decoded fields.
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
