using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

/// <summary>
/// <see cref="TempBasal"/> joins <see cref="IV4Record"/> through its span start, implemented
/// explicitly so the wire shape keeps only the start/end pair.
/// </summary>
[Trait("Category", "Unit")]
public class TempBasalRecordTests
{
    private static readonly DateTime Start = new(2026, 9, 27, 12, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void RecordTimestamp_ReadsAndWritesTheSpanStart()
    {
        var tempBasal = new TempBasal { StartTimestamp = Start, EndTimestamp = Start.AddMinutes(30) };
        IV4Record record = tempBasal;

        record.Timestamp.Should().Be(Start);

        record.Timestamp = Start.AddMinutes(5);

        tempBasal.StartTimestamp.Should().Be(Start.AddMinutes(5));
        tempBasal.EndTimestamp.Should().Be(Start.AddMinutes(30));
    }

    [Fact]
    public void RecordMills_IsTheSpanStartInUnixMilliseconds()
    {
        IV4Record record = new TempBasal { StartTimestamp = Start };

        record.Mills.Should().Be(new DateTimeOffset(Start).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void WireShape_CarriesNoRecordTimestampOrMills()
    {
        var json = JsonSerializer.Serialize(new TempBasal { StartTimestamp = Start });

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        names.Should().Contain(["StartTimestamp", "StartMills"]);
        names.Should().NotContain(["Timestamp", "Mills"]);
    }
}
