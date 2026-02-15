using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers;

/// <summary>
///     Factory for creating StateSpan instances from MyLife events
/// </summary>
internal static class MyLifeStateSpanFactory
{
    /// <summary>
    ///     Creates a BasalDelivery StateSpan from a MyLife event
    /// </summary>
    /// <param name="ev">The MyLife event</param>
    /// <param name="rate">The basal rate in U/h</param>
    /// <param name="origin">The origin of the basal delivery</param>
    /// <returns>A configured StateSpan</returns>
    internal static StateSpan CreateBasalDelivery(
        MyLifeEvent ev,
        double rate,
        BasalDeliveryOrigin origin)
    {
        var timestamp = MyLifeMapperHelpers.FromInstantTicks(ev.EventDateTime);
        var eventKey = MyLifeMapperHelpers.BuildEventKey(ev);

        return new StateSpan
        {
            Id = $"{MyLifeIdPrefixes.StateSpan}basal-{eventKey}",
            OriginalId = eventKey,
            Category = StateSpanCategory.BasalDelivery,
            State = BasalDeliveryState.Active.ToString(),
            StartMills = timestamp.ToUnixTimeMilliseconds(),
            EndMills = null, // Will be set when the next span arrives
            Source = DataSources.MyLifeConnector,
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = rate,
                ["origin"] = origin.ToString()
            },
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    ///     Creates a BasalDelivery StateSpan with a suffix for unique identification
    /// </summary>
    internal static StateSpan CreateBasalDeliveryWithSuffix(
        MyLifeEvent ev,
        double rate,
        BasalDeliveryOrigin origin,
        string suffix)
    {
        var stateSpan = CreateBasalDelivery(ev, rate, origin);
        if (!string.IsNullOrWhiteSpace(suffix)) stateSpan.Id = $"{stateSpan.Id}-{suffix}";
        return stateSpan;
    }
}