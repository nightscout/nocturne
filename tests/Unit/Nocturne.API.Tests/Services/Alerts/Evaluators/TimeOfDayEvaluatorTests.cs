using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class TimeOfDayEvaluatorTests
{
    [Fact]
    public void ConditionType_ShouldBeTimeOfDay()
    {
        var sut = MakeSut(new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc));
        sut.ConditionType.Should().Be(AlertConditionType.TimeOfDay);
    }

    [Fact]
    public async Task WithinWindow_Utc_ReturnsTrue()
    {
        var sut = MakeSut(new DateTime(2026, 3, 22, 10, 30, 0, DateTimeKind.Utc));
        var json = """{"from": "09:00", "to": "12:00", "timezone": null}""";

        (await sut.EvaluateAsync(json, MakeContext(), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task BeforeWindow_ReturnsFalse()
    {
        var sut = MakeSut(new DateTime(2026, 3, 22, 8, 30, 0, DateTimeKind.Utc));
        var json = """{"from": "09:00", "to": "12:00", "timezone": null}""";

        (await sut.EvaluateAsync(json, MakeContext(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AfterWindow_ReturnsFalse()
    {
        var sut = MakeSut(new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc));
        var json = """{"from": "09:00", "to": "12:00", "timezone": null}""";

        // Half-open [09:00, 12:00) — exactly 12:00 is OUT.
        (await sut.EvaluateAsync(json, MakeContext(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task OvernightWindow_BeforeMidnightInside_ReturnsTrue()
    {
        var sut = MakeSut(new DateTime(2026, 3, 22, 23, 0, 0, DateTimeKind.Utc));
        var json = """{"from": "22:00", "to": "06:00", "timezone": null}""";

        (await sut.EvaluateAsync(json, MakeContext(), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task OvernightWindow_AfterMidnightInside_ReturnsTrue()
    {
        var sut = MakeSut(new DateTime(2026, 3, 22, 2, 0, 0, DateTimeKind.Utc));
        var json = """{"from": "22:00", "to": "06:00", "timezone": null}""";

        (await sut.EvaluateAsync(json, MakeContext(), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task OvernightWindow_DaytimeOutside_ReturnsFalse()
    {
        var sut = MakeSut(new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc));
        var json = """{"from": "22:00", "to": "06:00", "timezone": null}""";

        (await sut.EvaluateAsync(json, MakeContext(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownTimezone_ReturnsFalse()
    {
        var sut = MakeSut(new DateTime(2026, 3, 22, 10, 30, 0, DateTimeKind.Utc));
        var json = """{"from": "09:00", "to": "12:00", "timezone": "Made/Up"}""";

        (await sut.EvaluateAsync(json, MakeContext(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task UnparseableTime_ReturnsFalse()
    {
        var sut = MakeSut(new DateTime(2026, 3, 22, 10, 30, 0, DateTimeKind.Utc));
        var json = """{"from": "9am", "to": "noon", "timezone": null}""";

        (await sut.EvaluateAsync(json, MakeContext(), CancellationToken.None)).Should().BeFalse();
    }

    private static TimeOfDayEvaluator MakeSut(DateTime utcNow) =>
        new(new FakeTimeProvider(new DateTimeOffset(utcNow)));

    private static SensorContext MakeContext() => new()
    {
        LatestValue = 100m,
        LatestTimestamp = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
        TrendRate = 0m,
        LastReadingAt = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc)
    };
}
