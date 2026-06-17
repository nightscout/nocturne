using System.Buffers.Binary;
using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Mappers;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Twiist.Tests.Mappers;

public class TwiistTempBasalMapperTests
{
    private readonly TwiistTempBasalMapper _mapper = new(NullLogger.Instance);

    [Fact]
    public void MapTempBasals_ConvertsDeltasToAbsoluteRates_AndTagsOrigin()
    {
        // Two 5-minute intervals: +0.97 above scheduled, and a full suspension (-0.40 == -scheduled).
        var start1 = new DateTime(2026, 6, 16, 22, 42, 0, DateTimeKind.Utc);
        var start2 = start1.AddMinutes(5);
        var blob = BuildBlob(
            (start1, start1.AddMinutes(5), 0.97),
            (start2, start2.AddMinutes(5), -0.40));

        var result = _mapper.MapTempBasals(blob, scheduledRate: 0.40).ToList();

        result.Should().HaveCount(2);

        result[0].Rate.Should().BeApproximately(1.37, 0.001);
        result[0].ScheduledRate.Should().Be(0.40);
        result[0].Origin.Should().Be(TempBasalOrigin.Algorithm);
        result[0].StartTimestamp.Should().Be(start1);
        result[0].EndTimestamp.Should().Be(start1.AddMinutes(5));

        result[1].Rate.Should().BeApproximately(0.0, 0.001);
        result[1].Origin.Should().Be(TempBasalOrigin.Suspended);
    }

    [Fact]
    public void MapTempBasals_EmptyBlob_ReturnsEmpty()
    {
        _mapper.MapTempBasals(null, 0.4).Should().BeEmpty();
        _mapper.MapTempBasals("", 0.4).Should().BeEmpty();
    }

    [Theory]
    [InlineData("0.4", 0.4)]
    [InlineData("1.25", 1.25)]
    [InlineData(null, 0.0)]
    [InlineData("", 0.0)]
    [InlineData("not-a-number", 0.0)]
    public void ParseScheduledRate_HandlesStringifiedNumbersAndJunk(string? input, double expected)
    {
        _mapper.ParseScheduledRate(input).Should().BeApproximately(expected, 0.0001);
    }

    /// <summary>
    /// Builds a Twiist insulin-delivery blob the way the API encodes it: raw-deflate over fixed
    /// 10-byte little-endian records (u32 start, u32 end seconds-since-2008, i16 delta*100), base64.
    /// </summary>
    private static string BuildBlob(params (DateTime Start, DateTime End, double Delta)[] records)
    {
        var raw = new byte[records.Length * 10];
        for (var i = 0; i < records.Length; i++)
        {
            var span = raw.AsSpan(i * 10);
            BinaryPrimitives.WriteUInt32LittleEndian(span,
                (uint)(records[i].Start - TwiistConstants.BlobEpoch).TotalSeconds);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..],
                (uint)(records[i].End - TwiistConstants.BlobEpoch).TotalSeconds);
            BinaryPrimitives.WriteInt16LittleEndian(span[8..],
                (short)Math.Round(records[i].Delta * 100));
        }

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionMode.Compress, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        return Convert.ToBase64String(output.ToArray());
    }
}
