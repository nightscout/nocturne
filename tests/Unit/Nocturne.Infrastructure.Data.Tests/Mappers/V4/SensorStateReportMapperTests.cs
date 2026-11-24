using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.Infrastructure.Data.Tests.Mappers.V4;

/// <summary>
/// A stored state reads as a state only by its exact member name: anything else is no state, never ok,
/// since alerts would otherwise trust a reading the sensor disowned.
/// </summary>
[Trait("Category", "Unit")]
public class SensorStateReportMapperTests
{
    public static TheoryData<SensorState> States() => [.. Enum.GetValues<SensorState>()];

    [Theory]
    [MemberData(nameof(States))]
    public void EveryStateRoundTripsThroughTheEntity(SensorState state)
    {
        var entity = SensorStateReportMapper.ToEntity(new SensorStateReport
        {
            Timestamp = DateTime.UtcNow,
            State = state,
            ReportedField = SensorStateReport.CareLinkField,
            ReportedValue = "WARM_UP",
        });

        entity.State.Should().Be(state.ToString());
        SensorStateReportMapper.ToDomainModel(entity).State.Should().Be(state);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bogus")]
    [InlineData("3")]
    [InlineData("ok")]
    [InlineData("Ok, Error")]
    public void AStoredStateThatIsNotAMemberNameReadsAsNoState(string? stored)
    {
        var entity = new SensorStateReportEntity
        {
            Id = Guid.CreateVersion7(),
            Timestamp = DateTime.UtcNow,
            State = stored,
            ReportedField = SensorStateReport.CareLinkField,
            ReportedValue = "WARM_UP",
        };

        SensorStateReportMapper.ToDomainModel(entity).State.Should().BeNull();
    }
}
