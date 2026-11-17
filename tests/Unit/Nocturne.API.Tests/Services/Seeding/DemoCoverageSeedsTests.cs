using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Services.Demo.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

public class DemoCoverageSeedsTests
{
    private static readonly DateOnly Sunday = new(2026, 11, 1);

    private static DemoClock Clock() =>
        new(new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero)), TimeZoneInfo.Utc);

    [Fact]
    public void A_compression_night_has_ended_once_its_recovery_is_past()
    {
        var recovered = new DateTime(2026, 11, 2, 4, 20, 0, DateTimeKind.Utc);

        DemoCoverageSeeds.CompressionArtifactEndedBy(Sunday, Clock(), recovered).Should().BeTrue();
        DemoCoverageSeeds.CompressionArtifactEndedBy(Sunday, Clock(), recovered.AddMinutes(-1)).Should().BeFalse();
    }

    [Fact]
    public void A_night_without_a_planted_artifact_never_has_one_ended()
    {
        var monday = Sunday.AddDays(1);

        DemoCoverageSeeds.CompressionArtifactEndedBy(monday, Clock(), new DateTime(2027, 11, 3, 0, 0, 0, DateTimeKind.Utc)).Should().BeFalse();
    }
}
