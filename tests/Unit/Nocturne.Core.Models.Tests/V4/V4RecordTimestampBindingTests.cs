using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

/// <summary>
/// A V4 record body binds a timestamp with an offset as <see cref="DateTimeKind.Local"/>, and only
/// a host off UTC makes that differ from the instant the client sent.
/// </summary>
[Collection(NonUtcHostCollection.Name)]
[Trait("Category", "Unit")]
public sealed class V4RecordTimestampBindingTests : IDisposable
{
    private static readonly DateTime Instant = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

    private readonly string? _previousTz = Environment.GetEnvironmentVariable("TZ");

    public V4RecordTimestampBindingTests()
    {
        Environment.SetEnvironmentVariable("TZ", "Australia/Brisbane");
        TimeZoneInfo.ClearCachedData();
        TimeZoneInfo.Local.BaseUtcOffset.Should().NotBe(TimeSpan.Zero);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TZ", _previousTz);
        TimeZoneInfo.ClearCachedData();
    }

    [Theory]
    [InlineData("2026-09-30T10:00:00+10:00")]
    [InlineData("2026-09-30T00:00:00+00:00")]
    [InlineData("2026-09-30T00:00:00Z")]
    [InlineData("2026-09-30T00:00:00")]
    [InlineData("2026-09-29T19:00:00-05:00")]
    public void Deserialise_AnyOffsetForm_GivesUtcTimestampAndMatchingMills(string wire)
    {
        var record = JsonSerializer.Deserialize<SensorGlucose>(
            $$"""{"timestamp":"{{wire}}"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        record.Timestamp.Should().Be(Instant);
        record.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        record.Mills.Should().Be(new DateTimeOffset(Instant).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Timestamp_AssignedLocal_IsStoredAsTheSameInstantInUtc()
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(Instant, TimeZoneInfo.Local);
        local = DateTime.SpecifyKind(local, DateTimeKind.Local);

        var record = new SensorGlucose { Timestamp = local };

        record.Timestamp.Should().Be(Instant);
        record.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
    }
}
