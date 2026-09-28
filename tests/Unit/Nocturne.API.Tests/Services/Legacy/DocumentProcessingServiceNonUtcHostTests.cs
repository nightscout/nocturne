using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Legacy;
using Xunit;
using Activity = Nocturne.Core.Models.Activity;

namespace Nocturne.API.Tests.Services.Legacy;

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
/// Zone-less cases only prove anything when the host is not on UTC, and the CI runner is.
/// </summary>
[Collection(NonUtcHostCollection.Name)]
[Trait("Category", "Unit")]
public sealed class DocumentProcessingServiceNonUtcHostTests : IDisposable
{
    private readonly string? _previousTz = Environment.GetEnvironmentVariable("TZ");
    private readonly DocumentProcessingService _service =
        new(NullLogger<DocumentProcessingService>.Instance);

    public DocumentProcessingServiceNonUtcHostTests()
    {
        Environment.SetEnvironmentVariable("TZ", "America/New_York");
        TimeZoneInfo.ClearCachedData();
        Assert.NotEqual(TimeSpan.Zero, TimeZoneInfo.Local.BaseUtcOffset);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TZ", _previousTz);
        TimeZoneInfo.ClearCachedData();
    }

    [Fact]
    public void ProcessTimestamp_ZonelessCreatedAt_IsStoredAsUtcWithZeroOffset()
    {
        var activity = new Activity { CreatedAt = "2026-09-26T08:00:00" };

        _service.ProcessTimestamp(activity);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            activity.Mills
        );
        Assert.Equal("2026-09-26T08:00:00.000Z", activity.CreatedAt);
        Assert.Equal(0, activity.UtcOffset);
    }

    [Fact]
    public void ProcessTimestamp_ZonelessCreatedAt_HonorsClientSuppliedUtcOffset()
    {
        var activity = new Activity { CreatedAt = "2026-09-26T08:00:00", UtcOffset = -240 };

        _service.ProcessTimestamp(activity);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            activity.Mills
        );
        Assert.Equal(-240, activity.UtcOffset);
    }
}
