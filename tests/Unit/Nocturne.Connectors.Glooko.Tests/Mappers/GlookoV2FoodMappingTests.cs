using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

public class GlookoV2FoodMappingTests
{
    private static GlookoV4TreatmentMapper Mapper(ILogger? logger = null) => new(
        "glooko-connector",
        new GlookoTimeMapper(new GlookoConnectorConfiguration { TimezoneOffset = 2 }, NullLogger.Instance),
        logger ?? NullLogger.Instance);

    [Theory]
    [InlineData(20, 30, 20)]
    [InlineData(0, 30, 30)]
    [InlineData(-1, 30, 30)]
    public void Both_outputs_use_the_pump_time_and_selected_carbs(double carbs, double fallback, double expected)
    {
        var batch = new GlookoBatchData
        {
            Foods = [new GlookoFood
            {
                Guid = "food-1", Name = "Toast", Timestamp = "2026-04-28T09:00:00Z",
                PumpTimestamp = "2026-04-28T10:00:00Z", Carbs = carbs, CarbohydrateGrams = fallback,
            }],
        };
        var mapper = Mapper();

        var intake = mapper.MapBatchData(batch).carbs.Should().ContainSingle().Subject;
        var import = mapper.MapFoodsToConnectorEntries(batch).Should().ContainSingle().Subject;

        intake.Timestamp.Should().Be(new DateTime(2026, 4, 28, 8, 0, 0, DateTimeKind.Utc));
        import.Entry.ConsumedAt.UtcDateTime.Should().Be(intake.Timestamp);
        intake.Carbs.Should().Be(expected);
        import.Entry.Carbs.Should().Be((decimal)expected);
        import.Entry.Food!.Carbs.Should().Be((decimal)expected);
        import.CarbIntakeLegacyId.Should().Be(intake.LegacyId);
    }

    [Theory]
    [InlineData(true, 20, 30)]
    [InlineData(false, 0, 0)]
    [InlineData(false, -1, -2)]
    public void Both_outputs_skip_deleted_or_nonpositive_foods(bool deleted, double carbs, double fallback)
    {
        var batch = new GlookoBatchData
        {
            Foods = [new GlookoFood
            {
                Timestamp = "2026-04-28T10:00:00Z", SoftDeleted = deleted,
                Carbs = carbs, CarbohydrateGrams = fallback,
            }],
        };
        var mapper = Mapper();

        mapper.MapBatchData(batch).carbs.Should().BeEmpty();
        mapper.MapFoodsToConnectorEntries(batch).Should().BeEmpty();
    }

    [Fact]
    public void Both_outputs_skip_malformed_foods_and_continue_with_valid_records()
    {
        var batch = new GlookoBatchData
        {
            Foods = [null!,
                new GlookoFood { Timestamp = "invalid", Carbs = 20 },
                new GlookoFood { Guid = "valid", Timestamp = "2026-04-28T10:00:00Z", Carbs = 30 }],
        };
        var logger = new Mock<ILogger>();
        var mapper = Mapper(logger.Object);

        mapper.MapBatchData(batch).carbs.Should().ContainSingle().Which.LegacyId.Should().Be("glooko_food_valid");
        mapper.MapFoodsToConnectorEntries(batch).Should().ContainSingle().Which.CarbIntakeLegacyId.Should().Be("glooko_food_valid");
        logger.Verify(l => l.Log(
            LogLevel.Warning, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((value, _) => value != null && value.ToString() != null && value.ToString()!.Contains("Skipped malformed V2 food")),
            It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Exactly(2));
    }
}
