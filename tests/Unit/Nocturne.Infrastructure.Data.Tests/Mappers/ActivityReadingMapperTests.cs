using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

public class ActivityReadingMapperTests
{
    private static readonly DateTime ReadingTime = new(2026, 3, 1, 11, 30, 15, 250, DateTimeKind.Utc);

    private const string ReadingCreatedAt = "2026-03-01T11:30:15.250Z";

    [Fact]
    [Trait("Category", "Unit")]
    public void HeartRate_CreatedAt_IsTheReadingTime()
    {
        var entity = new HeartRateEntity { Id = Guid.CreateVersion7(), Timestamp = ReadingTime, Bpm = 70 };

        var model = HeartRateMapper.ToDomainModel(entity);

        model.CreatedAt.Should().Be(ReadingCreatedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StepCount_CreatedAt_IsTheReadingTime()
    {
        var entity = new StepCountEntity { Id = Guid.CreateVersion7(), Timestamp = ReadingTime, Metric = 12 };

        var model = StepCountMapper.ToDomainModel(entity);

        model.CreatedAt.Should().Be(ReadingCreatedAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CreatedAt_IgnoresTheKindOfTheStoredTimestamp()
    {
        var entity = new HeartRateEntity
        {
            Id = Guid.CreateVersion7(),
            Timestamp = DateTime.SpecifyKind(ReadingTime, DateTimeKind.Unspecified),
        };

        HeartRateMapper.ToDomainModel(entity).CreatedAt.Should().Be(ReadingCreatedAt);
    }
}
