using Nocturne.API.Services.Glucose;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Glucose;

/// <summary>
/// Tests for Direction service functionality with 1:1 legacy compatibility
/// Based on legacy direction.test.js and bgnow.test.js
/// </summary>
[Parity("direction.test.js")]
public class DirectionServiceTests
{

    [Fact]
    public void GetDirectionInfo_ShouldReturnCorrectInfoForFlat()
    {
        // Arrange
        var entry = new Entry
        {
            Direction = "Flat",
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        // Act
        var result = DirectionService.GetDirectionInfo(entry);

        // Assert
        Assert.Equal(Direction.Flat, result.Value);
        Assert.Equal("→", result.Label);
        Assert.Equal("&#8594;", result.Entity);
    }

    [Fact]
    public void GetDirectionInfo_ShouldReturnCorrectInfoForDoubleUp()
    {
        // Arrange
        var entry = new Entry
        {
            Direction = "DoubleUp",
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        // Act
        var result = DirectionService.GetDirectionInfo(entry);

        // Assert
        Assert.Equal(Direction.DoubleUp, result.Value);
        Assert.Equal("⇈", result.Label);
        Assert.Equal("&#8648;", result.Entity);
    }

    [Fact]
    public void GetDirectionInfo_ShouldHandleAllDirectionValues()
    {
        // Test all direction mappings from legacy direction.test.js
        var testCases = new Dictionary<
            string,
            (Direction expectedEnum, string expectedLabel, string expectedEntity)
        >
        {
            { "NONE", (Direction.NONE, "⇼", "&#8700;") },
            { "DoubleUp", (Direction.DoubleUp, "⇈", "&#8648;") },
            { "SingleUp", (Direction.SingleUp, "↑", "&#8593;") },
            { "FortyFiveUp", (Direction.FortyFiveUp, "↗", "&#8599;") },
            { "Flat", (Direction.Flat, "→", "&#8594;") },
            { "FortyFiveDown", (Direction.FortyFiveDown, "↘", "&#8600;") },
            { "SingleDown", (Direction.SingleDown, "↓", "&#8595;") },
            { "DoubleDown", (Direction.DoubleDown, "⇊", "&#8650;") },
            { "NOT COMPUTABLE", (Direction.NotComputable, "-", "&#45;") },
            { "RATE OUT OF RANGE", (Direction.RateOutOfRange, "⇕", "&#8661;") },
        };

        foreach (var testCase in testCases)
        {
            // Arrange
            var entry = new Entry
            {
                Direction = testCase.Key,
                Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            // Act
            var result = DirectionService.GetDirectionInfo(entry);

            // Assert
            Assert.Equal(testCase.Value.expectedEnum, result.Value);
            Assert.Equal(testCase.Value.expectedLabel, result.Label);
            Assert.Equal(testCase.Value.expectedEntity, result.Entity);
        }
    }

    [Theory]
    [InlineData("NotComputable", "-", "&#45;")]
    [InlineData("RateOutOfRange", "⇕", "&#8661;")]
    [InlineData("None", "⇼", "&#8700;")]
    public void GetDirectionInfo_ShouldAcceptEnumMemberSpelling(
        string direction,
        string expectedLabel,
        string expectedEntity
    )
    {
        var entry = new Entry
        {
            Direction = direction,
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var result = DirectionService.GetDirectionInfo(entry);

        Assert.Equal(expectedLabel, result.Label);
        Assert.Equal(expectedEntity, result.Entity);
    }

    [Fact]
    public void GetDirectionInfo_ShouldReturnNullDisplayForNullEntry()
    {
        // Arrange & Act
        var result = DirectionService.GetDirectionInfo(null);

        // Assert
        Assert.Null(result.Display);
    }

    [Fact]
    public void CalculateDelta_ShouldCalculateCorrectDelta()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fiveMinutesAgo = now - (5 * 60 * 1000);

        var entries = new List<Entry>
        {
            new() { Mills = fiveMinutesAgo, Mgdl = 100 },
            new() { Mills = now, Mgdl = 105 },
        };

        // Act
        var result = DirectionService.CalculateDelta(entries, "mg/dl");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(5, result.Mgdl);
        Assert.Equal(5, result.Scaled);
        Assert.Equal("+5", result.Display);
    }

    [Fact]
    public void CalculateDelta_ShouldInterpolateWhenMoreThanNineMinutesApart()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var elevenMinutesAgo = now - (11 * 60 * 1000);

        var entries = new List<Entry>
        {
            new() { Mills = elevenMinutesAgo, Mgdl = 100 },
            new() { Mills = now, Mgdl = 110 },
        };

        // Act
        var result = DirectionService.CalculateDelta(entries, "mg/dl");

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Interpolated);
        Assert.NotNull(result.ElapsedMins);
        Assert.Equal(11.0, result.ElapsedMins.Value, precision: 1); // Allow some precision tolerance

        // Legacy interpolation calculation: mean5MinsAgo = recent - ((recent - previous) / elapsedMins) * 5
        var expectedMean5MinsAgo = 110 - ((110 - 100) / 11.0) * 5;
        var expectedDelta = Math.Round(110 - expectedMean5MinsAgo);

        Assert.Equal(expectedDelta, result.Mgdl);
        Assert.Equal($"+{expectedDelta}", result.Display);
    }

    [Fact]
    public void CalculateDelta_ShouldConvertToMmolL()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fiveMinutesAgo = now - (5 * 60 * 1000);

        var entries = new List<Entry>
        {
            new() { Mills = fiveMinutesAgo, Mgdl = 180 },
            new() { Mills = now, Mgdl = 198 },
        };

        // Act
        var result = DirectionService.CalculateDelta(entries, "mmol");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(18, result.Mgdl); // Raw mg/dl delta
        Assert.NotNull(result.Scaled);
        Assert.Equal(1.0, result.Scaled.Value, precision: 1); // Converted to mmol/L (18/18)
        Assert.Equal("+1.0", result.Display);
    }

    [Fact]
    public void CalculateDelta_ShouldReturnNullForInsufficientData()
    {
        // Arrange
        var entries = new List<Entry>
        {
            new() { Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mgdl = 100 },
        };

        // Act
        var result = DirectionService.CalculateDelta(entries, "mg/dl");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void CalculateDirection_ShouldCalculateCorrectDirections()
    {
        // Test direction calculation based on slope thresholds
        var testCases = new[]
        {
            (current: 200.0, previous: 170.0, minutes: 5.0, expected: Direction.TripleUp), // 6 mg/dL per minute
            (current: 180.0, previous: 165.0, minutes: 5.0, expected: Direction.DoubleUp), // 3 mg/dL per minute
            (current: 110.0, previous: 105.0, minutes: 5.0, expected: Direction.SingleUp), // 1 mg/dL per minute
            (current: 105.0, previous: 103.0, minutes: 5.0, expected: Direction.FortyFiveUp), // 0.4 mg/dL per minute
            (current: 100.0, previous: 100.0, minutes: 5.0, expected: Direction.Flat), // 0 mg/dL per minute
            (current: 97.0, previous: 100.0, minutes: 5.0, expected: Direction.FortyFiveDown), // -0.6 mg/dL per minute
            (current: 90.0, previous: 95.0, minutes: 5.0, expected: Direction.SingleDown), // -1 mg/dL per minute
            (current: 80.0, previous: 90.0, minutes: 5.0, expected: Direction.DoubleDown), // -2 mg/dL per minute
            (current: 60.0, previous: 80.0, minutes: 5.0, expected: Direction.TripleDown), // -4 mg/dL per minute
        };

        foreach (var testCase in testCases)
        {
            // Act
            var result = DirectionService.CalculateDirection(
                testCase.current,
                testCase.previous,
                testCase.minutes
            );

            // Assert
            Assert.Equal(testCase.expected, result);
        }
    }

    [Fact]
    public void DirectionToChar_ShouldReturnCorrectCharacters()
    {
        // Test exact legacy character mapping
        var testCases = new Dictionary<Direction, string>
        {
            { Direction.NONE, "⇼" },
            { Direction.TripleUp, "⤊" },
            { Direction.DoubleUp, "⇈" },
            { Direction.SingleUp, "↑" },
            { Direction.FortyFiveUp, "↗" },
            { Direction.Flat, "→" },
            { Direction.FortyFiveDown, "↘" },
            { Direction.SingleDown, "↓" },
            { Direction.DoubleDown, "⇊" },
            { Direction.TripleDown, "⤋" },
            { Direction.NotComputable, "-" },
            { Direction.RateOutOfRange, "⇕" },
        };

        foreach (var testCase in testCases)
        {
            // Act
            var result = DirectionService.DirectionToChar(testCase.Key);

            // Assert
            Assert.Equal(testCase.Value, result);
        }
    }

    [Fact]
    public void CharToEntity_ShouldConvertToHtmlEntity()
    {
        // Arrange & Act & Assert
        Assert.Equal("&#8594;", DirectionService.CharToEntity("→"));
        Assert.Equal("&#8648;", DirectionService.CharToEntity("⇈"));
        Assert.Equal("&#45;", DirectionService.CharToEntity("-"));
        Assert.Equal(string.Empty, DirectionService.CharToEntity(""));
        Assert.Equal(string.Empty, DirectionService.CharToEntity(null!));
    }

    [Fact]
    public void CalculateDelta_ShouldHandleNegativeDeltas()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fiveMinutesAgo = now - (5 * 60 * 1000);

        var entries = new List<Entry>
        {
            new() { Mills = fiveMinutesAgo, Mgdl = 120 },
            new() { Mills = now, Mgdl = 110 },
        };

        // Act
        var result = DirectionService.CalculateDelta(entries, "mg/dl");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(-10, result.Mgdl);
        Assert.Equal(-10, result.Scaled);
        Assert.Equal("-10", result.Display);
        Assert.False(result.Interpolated);
    }

    [Fact]
    public void CalculateDelta_ShouldUseSgvWhenMgdlIsZero()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fiveMinutesAgo = now - (5 * 60 * 1000);

        var entries = new List<Entry>
        {
            new()
            {
                Mills = fiveMinutesAgo,
                Mgdl = 0,
                Sgv = 100,
            },
            new()
            {
                Mills = now,
                Mgdl = 0,
                Sgv = 105,
            },
        };

        // Act
        var result = DirectionService.CalculateDelta(entries, "mg/dl");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(5, result.Mgdl);
        Assert.Equal("+5", result.Display);
    }
}
