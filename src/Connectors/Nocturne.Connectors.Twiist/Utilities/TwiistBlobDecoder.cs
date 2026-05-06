using System.Buffers.Binary;
using System.IO.Compression;
using Nocturne.Connectors.Twiist.Configurations;

namespace Nocturne.Connectors.Twiist.Utilities;

/// <summary>
/// Decodes Twiist binary blobs: base64 -> raw deflate -> fixed-size LE records.
/// </summary>
public static class TwiistBlobDecoder
{
    /// <summary>
    /// Decodes a base64+deflate blob into raw bytes.
    /// </summary>
    public static byte[] DecodeRaw(string base64Data)
    {
        var compressed = Convert.FromBase64String(base64Data);

        // Raw deflate (no zlib header) — use DeflateStream
        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Parses glucose records from a decoded blob.
    /// Each record is 8 bytes: u32 timestamp (seconds since 2008-01-01) + u32 mgdl*100.
    /// </summary>
    public static IReadOnlyList<GlucoseRecord> ParseGlucoseRecords(string? base64Data)
    {
        if (string.IsNullOrEmpty(base64Data))
            return [];

        var data = DecodeRaw(base64Data);
        if (data.Length % 8 != 0)
            return [];

        var records = new List<GlucoseRecord>(data.Length / 8);
        for (var i = 0; i < data.Length; i += 8)
        {
            var epochSeconds = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i));
            var mgdlX100 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 4));

            var timestamp = TwiistConstants.BlobEpoch.AddSeconds(epochSeconds);
            var mgdl = mgdlX100 / 100.0;

            records.Add(new GlucoseRecord(timestamp, mgdl));
        }

        return records;
    }

    /// <summary>
    /// Parses insulin delivery records from a decoded blob.
    /// Each record is 10 bytes: u32 start_ts + u32 end_ts + i16 delta_uhr*100.
    /// Delta represents deviation from scheduled basal rate.
    /// </summary>
    public static IReadOnlyList<InsulinDeliveryRecord> ParseInsulinDeliveryRecords(string? base64Data)
    {
        if (string.IsNullOrEmpty(base64Data))
            return [];

        var data = DecodeRaw(base64Data);
        if (data.Length % 10 != 0)
            return [];

        var records = new List<InsulinDeliveryRecord>(data.Length / 10);
        for (var i = 0; i < data.Length; i += 10)
        {
            var startEpoch = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i));
            var endEpoch = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 4));
            var deltaX100 = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i + 8));

            var start = TwiistConstants.BlobEpoch.AddSeconds(startEpoch);
            var end = TwiistConstants.BlobEpoch.AddSeconds(endEpoch);
            var deltaUhr = deltaX100 / 100.0;

            records.Add(new InsulinDeliveryRecord(start, end, deltaUhr));
        }

        return records;
    }
}

/// <summary>
/// A decoded glucose record from the Twiist binary blob.
/// </summary>
public readonly record struct GlucoseRecord(DateTime Timestamp, double Mgdl);

/// <summary>
/// A decoded insulin delivery record from the Twiist binary blob.
/// Delta represents the deviation from the scheduled basal rate in U/hr.
/// </summary>
public readonly record struct InsulinDeliveryRecord(DateTime Start, DateTime End, double DeltaUhr);
