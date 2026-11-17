using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Legacy;
using Xunit;
using Activity = Nocturne.Core.Models.Activity;

namespace Nocturne.API.Tests.Services.Legacy;

[Trait("Category", "Unit")]
public class DocumentProcessingServiceClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 34, 56, 789, TimeSpan.Zero);

    private readonly DocumentProcessingService _service = new(
        new FakeTimeProvider(Now), NullLogger<DocumentProcessingService>.Instance);

    [Fact]
    public void ProcessTimestamp_WithNoTimestamp_StampsTheClock()
    {
        var activity = new Activity { CreatedAt = null };

        _service.ProcessTimestamp(activity);

        activity.Mills.Should().Be(Now.ToUnixTimeMilliseconds());
        activity.CreatedAt.Should().Be("2026-10-20T12:34:56.789Z");
        activity.UtcOffset.Should().Be(0);
    }

    [Fact]
    public void ProcessTimestamp_WithAnUnparseableCreatedAt_FallsBackToTheClock()
    {
        var activity = new Activity { CreatedAt = "not a timestamp" };

        _service.ProcessTimestamp(activity);

        activity.Mills.Should().Be(Now.ToUnixTimeMilliseconds());
        activity.CreatedAt.Should().Be("2026-10-20T12:34:56.789Z");
    }
}
