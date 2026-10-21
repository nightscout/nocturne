using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

public class TherapySettingsMapperTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ToDomainModel_MalformedLoopSettingsJson_YieldsNoLoopSettings()
    {
        var entity = new TherapySettingsEntity
        {
            Id = Guid.CreateVersion7(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            LoopSettingsJson = """{"dia":""",
        };

        var model = TherapySettingsMapper.ToDomainModel(entity);

        model.LoopSettings.Should().BeNull(
            "one unparseable jsonb row must not fail the read of every record around it");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void ApnsEnvironment_RoundTripsThroughTheEntity(bool? isApnsProduction)
    {
        var model = new TherapySettings { IsAPNSProduction = isApnsProduction };

        var entity = TherapySettingsMapper.ToEntity(model);
        TherapySettingsMapper.ToDomainModel(entity).IsAPNSProduction.Should().Be(isApnsProduction);

        var updated = new TherapySettingsEntity { IsApnsProduction = !isApnsProduction };
        TherapySettingsMapper.UpdateEntity(updated, model);
        updated.IsApnsProduction.Should().Be(isApnsProduction);
    }
}
