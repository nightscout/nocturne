using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.JsonConverters;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>
/// Moves <see cref="TimeZoneInfo.Local"/> off UTC for its tests. The environment variable is
/// process-wide, so nothing else may run alongside.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NonUtcHostCollection
{
    public const string Name = "NonUtcHost";
}

/// <summary>
/// Zone-less and offset cases only prove anything when the host is not on UTC, and the CI runner is.
/// </summary>
[Collection(NonUtcHostCollection.Name)]
[Trait("Category", "Unit")]
public sealed class UploaderTimestampNonUtcHostTests : IDisposable
{
    private static readonly long EightUtcMills =
        new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private readonly string? _previousTz = Environment.GetEnvironmentVariable("TZ");

    public UploaderTimestampNonUtcHostTests()
    {
        Environment.SetEnvironmentVariable("TZ", "America/New_York");
        TimeZoneInfo.ClearCachedData();
        TimeZoneInfo.Local.BaseUtcOffset.Should().NotBe(TimeSpan.Zero);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TZ", _previousTz);
        TimeZoneInfo.ClearCachedData();
    }

    [Theory]
    [InlineData("2026-09-26T10:00:00")]
    [InlineData("2026-09-26T10:00:00.000")]
    [InlineData("2026-09-26 10:00:00")]
    public void TryParse_ZonelessString_IsReadAsUtc(string value)
    {
        UploaderTimestamp.TryParse(value, out var parsed).Should().BeTrue();

        parsed.Offset.Should().Be(TimeSpan.Zero);
        parsed.UtcDateTime.Should().Be(new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseUtcDateTime_ZonelessString_ReturnsUtcKind()
    {
        var parsed = UploaderTimestamp.ParseUtcDateTime("2026-09-26T10:00:00");

        parsed.Should().Be(new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc));
        parsed!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("2026-09-26T08:00:00")]
    [InlineData("2026-09-26T13:00:00+05:00")]
    public void TreatmentCalculatedMills_IsTheUtcInstant(string createdAt)
    {
        var treatment = new Treatment { Created_at = createdAt };

        treatment.CalculatedMills.Should().Be(EightUtcMills);
    }

    [Theory]
    [InlineData("2026-09-26T08:00:00")]
    [InlineData("2026-09-26T13:00:00+05:00")]
    public void EntryMillsAndDate_FromDateString_AreTheUtcInstant(string dateString)
    {
        var entry = new Entry { DateString = dateString };

        entry.Mills.Should().Be(EightUtcMills);
        entry.Date.Should().Be(new DateTime(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc));
        entry.Date!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("2026-09-26T08:00:00")]
    [InlineData("2026-09-26T13:00:00+05:00")]
    public void UnixTimestampOrDateTimeConverter_IsoString_IsTheUtcInstant(string iso)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new UnixTimestampOrDateTimeConverter());

        var parsed = JsonSerializer.Deserialize<DateTime?>($"\"{iso}\"", options);

        parsed.Should().Be(new DateTime(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc));
        parsed!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }
}
