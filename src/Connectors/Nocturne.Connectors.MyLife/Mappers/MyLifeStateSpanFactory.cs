using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers;

/// <summary>
///     Factory for creating TempBasal instances from MyLife events
/// </summary>
internal static class MyLifeStateSpanFactory
{
    /// <param name="rate">U/h.</param>
    internal static TempBasal CreateTempBasal(
        MyLifeEvent ev,
        DateTime now,
        double rate,
        TempBasalOrigin origin)
    {
        var timestamp = MyLifeMapperHelpers.FromInstantTicks(ev.EventDateTime);
        var eventKey = MyLifeMapperHelpers.BuildEventKey(ev);

        return new TempBasal
        {
            Id = Guid.CreateVersion7(),
            StartTimestamp = timestamp.UtcDateTime,
            EndTimestamp = null, // Will be set when the next record arrives
            Rate = rate,
            ScheduledRate = null,
            Origin = origin,
            Device = null,
            App = null,
            DataSource = DataSources.MyLifeConnector,
            LegacyId = eventKey,
            CreatedAt = now,
            ModifiedAt = now,
        };
    }
}
