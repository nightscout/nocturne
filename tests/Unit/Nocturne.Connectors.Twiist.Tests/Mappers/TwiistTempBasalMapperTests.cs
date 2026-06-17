using System.Buffers.Binary;
using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Mappers;
using Nocturne.Connectors.Twiist.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Twiist.Tests.Mappers;

public class TwiistTempBasalMapperTests
{
    private readonly TwiistTempBasalMapper _mapper = new(NullLogger.Instance);

    [Fact]
    public void MapTempBasals_ConvertsDeltasToAbsoluteRates_AlwaysAlgorithmOrigin()
    {
        // Two 5-minute intervals: +0.97 above scheduled, and a zero-rate low-temp (-0.40 == -scheduled).
        // Both are loop-enacted rates — a zero rate here is a low-temp, NOT a pump suspension.
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
        result[1].Origin.Should().Be(TempBasalOrigin.Algorithm);
    }

    [Fact]
    public void MapSuspensions_PairsSuspendWithNextResume_AsSuspendedSpans()
    {
        var suspend1 = new DateTime(2026, 6, 16, 16, 21, 0, DateTimeKind.Utc);
        var resume1 = new DateTime(2026, 6, 16, 18, 17, 0, DateTimeKind.Utc);
        var suspend2 = new DateTime(2026, 6, 16, 23, 44, 0, DateTimeKind.Utc); // no matching resume -> open

        var doses = new List<TwiistInsulinDose>
        {
            new() { DoseType = "Bolus", StartDate = suspend1.AddMinutes(1), Value = 1.0m },
            new() { DoseType = "Suspend", StartDate = suspend1 },
            new() { DoseType = "Resume", StartDate = resume1 },
            new() { DoseType = "Suspend", StartDate = suspend2 }
        };

        var result = _mapper.MapSuspensions(doses).ToList();

        result.Should().HaveCount(2);
        result.Should().OnlyContain(t => t.Origin == TempBasalOrigin.Suspended && t.Rate == 0);

        result[0].StartTimestamp.Should().Be(suspend1);
        result[0].EndTimestamp.Should().Be(resume1);
        result[1].StartTimestamp.Should().Be(suspend2);
        result[1].EndTimestamp.Should().BeNull(); // still suspended
    }

    [Fact]
    public void MapSuspensions_NoSuspendEvents_ReturnsEmpty()
    {
        var doses = new List<TwiistInsulinDose>
        {
            new() { DoseType = "Bolus", StartDate = DateTime.UtcNow, Value = 1.0m }
        };

        _mapper.MapSuspensions(doses).Should().BeEmpty();
        _mapper.MapSuspensions(null).Should().BeEmpty();
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
