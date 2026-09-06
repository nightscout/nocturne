using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Models;

/// <summary>
/// Unit tests for the Entry model
/// Tests the data model behavior, especially the calculated properties
/// </summary>
public class EntryTests
{
    [Fact]
    public void Mills_WhenSetDirectly_ShouldReturnSetValue()
    {
        // Arrange
        var entry = new Entry();
        var expectedMills = 1641024000000; // 2022-01-01 12:00:00 UTC

        // Act
        entry.Mills = expectedMills;

        // Assert
        entry.Mills.Should().Be(expectedMills);
    }

    [Fact]
    public void Mills_WhenNotSetButDateStringExists_ShouldCalculateFromDateString()
    {
        // Arrange
        var entry = new Entry { DateString = "2022-01-01T12:00:00.000Z" };

        // Act
        var result = entry.Mills;
        // Assert
        result.Should().Be(1641038400000); // 2022-01-01 12:00:00 UTC in milliseconds
    }

    [Fact]
    public void Mills_WhenNotSetAndInvalidDateString_ShouldReturnZero()
    {
        // Arrange
        var entry = new Entry { DateString = "invalid-date" };

        // Act
        var result = entry.Mills;

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public void Date_WhenNotSetButMillsExists_ShouldCalculateFromMills()
    {
        // Arrange
        var entry = new Entry();
        entry.Mills = 1641038400000; // 2022-01-01 12:00:00 UTC

        // Act
        var result = entry.Date;

        // Assert
        result.Should().Be(new DateTime(2022, 1, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Date_WhenNotSetButDateStringExists_ShouldCalculateFromDateString()
    {
        // Arrange
        var entry = new Entry { DateString = "2022-01-01T12:00:00.000Z" };

        // Act
        var result = entry.Date;

        // Assert
        result.Should().Be(new DateTime(2022, 1, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Date_WhenSetDirectly_ShouldReturnSetValue()
    {
        // Arrange
        var entry = new Entry();
        var expectedDate = new DateTime(2022, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // Act
        entry.Date = expectedDate;

        // Assert
        entry.Date.Should().Be(expectedDate);
    }

    [Fact]
    public void Entry_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var entry = new Entry();

        // Assert
        entry.Type.Should().Be("sgv");
        entry.Mills.Should().Be(0);
        entry.Date.Should().BeNull();
        entry.Id.Should().BeNull();
        entry.Sgv.Should().BeNull();
        entry.Direction.Should().BeNull();
        entry.Device.Should().BeNull();
    }

    [Fact]
    public void Entry_WithCompleteData_ShouldMapCorrectly()
    {
        // Arrange & Act
        var entry = new Entry
        {
            Id = "507f1f77bcf86cd799439011",
            Mills = 1641024000000,
            DateString = "2022-01-01T12:00:00.000Z",
            Sgv = 120.5,
            Direction = "Flat",
            Type = "sgv",
            Device = "xDrip-DexcomG5",
            Delta = 2.5,
            Rssi = 100,
            Noise = 1,
            Filtered = 118.0,
            Unfiltered = 122.0,
            UtcOffset = 0,
        };

        // Assert
        entry.Id.Should().Be("507f1f77bcf86cd799439011");
        entry.Mills.Should().Be(1641024000000);
        entry.DateString.Should().Be("2022-01-01T12:00:00.000Z");
        entry.Sgv.Should().Be(120.5);
        entry.Direction.Should().Be("Flat");
        entry.Type.Should().Be("sgv");
        entry.Device.Should().Be("xDrip-DexcomG5");
        entry.Delta.Should().Be(2.5);
        entry.Rssi.Should().Be(100);
        entry.Noise.Should().Be(1);
        entry.Filtered.Should().Be(118.0);
        entry.Unfiltered.Should().Be(122.0);
        entry.UtcOffset.Should().Be(0);
    }

    [Fact]
    public void DateMills_WhenOnlyMillsIsSet_FallsBackToMills()
    {
        var entry = new Entry { Mills = 1787459739692 };

        entry.DateMills.Should().Be(1787459739692);
    }

    [Fact]
    public void DateMills_WhenOnlyDateStringIsSet_FallsBackToTheParsedTimestamp()
    {
        var entry = new Entry { DateString = "2022-01-01T12:00:00.000Z" };

        entry.DateMills.Should().Be(1641038400000);
    }

    [Fact]
    public void DateMills_WhenSetExplicitly_KeepsItsOwnValue()
    {
        // "date" wins over "mills" the same way Entry.Mills reads it: a client that sent both is
        // taken at its word rather than having one silently rewritten from the other.
        var entry = new Entry { DateMills = 1641038400000, Mills = 1787459739692 };

        entry.DateMills.Should().Be(1641038400000);
    }

    [Fact]
    public void SerializedEntry_CarriesTheSameTimestampInDateAndMills()
    {
        // AAPS reads "date" alone with no fallback, so an entry serialized with date: 0 lands in it
        // as a reading at the Unix epoch.
        var json = JsonSerializer.Serialize(new Entry { Mills = 1787459739692, Sgv = 120 });

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("date").GetInt64().Should().Be(1787459739692);
        document.RootElement.GetProperty("mills").GetInt64().Should().Be(1787459739692);
    }

    [Fact]
    public void SerializedEntry_WithNoTimestampAtAll_StillReportsZero()
    {
        var json = JsonSerializer.Serialize(new Entry { Sgv = 120 });

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("date").GetInt64().Should().Be(0);
    }
}
