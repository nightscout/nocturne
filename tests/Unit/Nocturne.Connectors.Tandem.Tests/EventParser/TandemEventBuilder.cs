using System.Buffers.Binary;
using Nocturne.Connectors.Tandem.Configurations;

namespace Nocturne.Connectors.Tandem.Tests.EventParser;

/// <summary>
/// Builds synthetic 26-byte Tandem pump-event records (big-endian header + 16-byte payload) for
/// decoder and mapper tests, and assembles them into the base64 payload the API returns.
/// </summary>
public sealed class TandemEventBuilder
{
    private readonly byte[] _record = new byte[TandemConstants.EventLength];

    public TandemEventBuilder(int eventId, long timestampRaw, uint seqNum, int source = 0)
    {
        var sourceAndId = (ushort)(((source & 0xF) << 12) | (eventId & 0x0FFF));
        BinaryPrimitives.WriteUInt16BigEndian(_record.AsSpan(0), sourceAndId);
        BinaryPrimitives.WriteUInt32BigEndian(_record.AsSpan(2), (uint)timestampRaw);
        BinaryPrimitives.WriteUInt32BigEndian(_record.AsSpan(6), seqNum);
    }

    private int Index(int payloadOffset) => TandemConstants.EventHeaderSize + payloadOffset;

    public TandemEventBuilder UInt8(int offset, byte value)
    {
        _record[Index(offset)] = value;
        return this;
    }

    public TandemEventBuilder Int8(int offset, sbyte value)
    {
        _record[Index(offset)] = (byte)value;
        return this;
    }

    public TandemEventBuilder UInt16(int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_record.AsSpan(Index(offset)), value);
        return this;
    }

    public TandemEventBuilder UInt32(int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_record.AsSpan(Index(offset)), value);
        return this;
    }

    public TandemEventBuilder Float32(int offset, float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(_record.AsSpan(Index(offset)), value);
        return this;
    }

    public byte[] ToBytes() => (byte[])_record.Clone();

    /// <summary>Base64-encodes one or more records into a single events payload.</summary>
    public static string ToBase64(params TandemEventBuilder[] events)
    {
        var combined = new byte[events.Length * TandemConstants.EventLength];
        for (var i = 0; i < events.Length; i++)
            Array.Copy(events[i].ToBytes(), 0, combined, i * TandemConstants.EventLength, TandemConstants.EventLength);
        return Convert.ToBase64String(combined);
    }
}
