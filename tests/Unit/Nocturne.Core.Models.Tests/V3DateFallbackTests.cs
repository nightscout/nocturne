using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>
/// AAPS's NS v3 socket handler reads <c>doc.date</c> with <c>getLong</c> on entries,
/// treatments and devicestatus, with no fallback to <c>mills</c> and no null check, so a
/// realtime storage event whose doc omits <c>date</c> or sends it as null throws on the
/// background thread and takes the AAPS process down (#965). Every model broadcast as a
/// storage event must therefore serialize a numeric <c>date</c>, falling back to Mills —
/// the same value the V3 REST projection emits, so socket and catch-up loads agree.
/// </summary>
[Trait("Category", "Unit")]
public class V3DateFallbackTests
{
    private const long Mills = 1_722_945_600_000;

    public static TheoryData<string, object> BroadcastDocsWithMills =>
        new()
        {
            { "entries", new Entry { Mills = Mills } },
            { "treatments", new Treatment { Mills = Mills } },
            { "devicestatus", new DeviceStatus { Mills = Mills } },
        };

    [Theory]
    [MemberData(nameof(BroadcastDocsWithMills))]
    public void BroadcastDoc_WithMills_SerializesDateAsMills(string collection, object doc)
    {
        var root = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(doc));

        root.TryGetProperty("date", out var date)
            .Should()
            .BeTrue($"AAPS crashes on {collection} docs without a date");
        date.ValueKind.Should().Be(JsonValueKind.Number);
        date.GetInt64().Should().Be(Mills);
    }

    [Fact]
    public void ExplicitDate_WinsOverTheMillsFallback()
    {
        var treatment = new Treatment { Mills = Mills, Date = Mills + 5_000 };
        var deviceStatus = new DeviceStatus { Mills = Mills, Date = Mills + 5_000 };

        treatment.Date.Should().Be(Mills + 5_000);
        deviceStatus.Date.Should().Be(Mills + 5_000);
    }

    [Fact]
    public void DeviceStatus_WithoutMills_KeepsTheUploadedDateAsTheMillsSource()
    {
        // AAPS uploads devicestatus with "date" and no "mills"; the V4 decomposer reads
        // Date to seed Mills, so the fallback must not shadow the uploaded value.
        var deviceStatus = new DeviceStatus { Date = Mills };

        deviceStatus.Mills.Should().Be(0);
        deviceStatus.Date.Should().Be(Mills);
    }

    [Fact]
    public void Treatment_WithoutMills_DerivesDateFromCreatedAt()
    {
        var treatment = new Treatment { CreatedAt = "2026-08-06T12:00:00.000Z" };
        var expected = DateTimeOffset.Parse("2026-08-06T12:00:00.000Z").ToUnixTimeMilliseconds();

        treatment.Date.Should().Be(expected);
    }

    [Fact]
    public void Treatment_WithOnlyDate_ResolvesMillsWithoutRecursing()
    {
        // Treatment.Mills resolves through Date; Date now falls back to Mills. Reading either
        // must terminate — a naive fallback stack-overflows and kills the process.
        var treatment = new Treatment { Date = Mills };

        treatment.Mills.Should().Be(Mills);
        treatment.Date.Should().Be(Mills);
    }

    [Fact]
    public void Treatment_WithNoTimestampAtAll_TerminatesAndFabricatesNothing()
    {
        var treatment = new Treatment();

        treatment.Mills.Should().Be(0);
        treatment.Date.Should().BeNull();
    }

    [Fact]
    public void DeviceStatus_WithNoTimestampAtAll_OmitsDateRatherThanWritingNull()
    {
        var root = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(new DeviceStatus())
        );

        root.TryGetProperty("date", out _).Should().BeFalse();
    }
}
