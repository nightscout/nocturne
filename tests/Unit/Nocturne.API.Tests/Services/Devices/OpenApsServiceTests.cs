using Nocturne.API.Services.Devices;
using Nocturne.Core.Models;
using Xunit;
using Nocturne.API.Tests.Services.Glucose;

namespace Nocturne.API.Tests.Services.Devices;

/// <summary>
/// Tests for the OpenAPS offline marker, from legacy openaps.js behaviour.
/// </summary>
[Parity("openaps.test.js")]
public class OpenApsServiceTests
{
    private readonly OpenApsService _openApsService = new();

    [Parity]
    [Fact]
    public void FindOfflineMarker_WithActiveOfflineMarker_ShouldReturnMarker()
    {
        // Arrange
        var currentTime = new DateTime(2015, 12, 5, 19, 5, 0, DateTimeKind.Utc);
        var treatments = new List<Treatment>
        {
            new Treatment
            {
                EventType = "OpenAPS Offline",
                Mills = ((DateTimeOffset)currentTime.AddMinutes(-30)).ToUnixTimeMilliseconds(),
                Duration = 60, // 60 minutes duration
            },
        };

        // Act
        var result = _openApsService.FindOfflineMarker(treatments, currentTime);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("OpenAPS Offline", result.EventType);
    }

    [Parity]
    [Fact]
    public void FindOfflineMarker_WithExpiredOfflineMarker_ShouldReturnNull()
    {
        // Arrange
        var currentTime = new DateTime(2015, 12, 5, 19, 5, 0, DateTimeKind.Utc);
        var treatments = new List<Treatment>
        {
            new Treatment
            {
                EventType = "OpenAPS Offline",
                Mills = currentTime.AddMinutes(-120).Ticks / 10000, // 2 hours ago
                Duration = 60, // 60 minutes duration - expired
            },
        };

        // Act
        var result = _openApsService.FindOfflineMarker(treatments, currentTime);

        // Assert
        Assert.Null(result);
    }
}
