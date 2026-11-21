using System.Text.Json;
using Nocturne.API.Models.Responses;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The wire shape of a stored alert rule's parts, read by the rule editor and the setup hub.
/// </summary>
internal static class AlertRuleResponses
{
    public static AlertRuleChannelResponse Channel(AlertRuleChannelSnapshot c) => new()
    {
        Id = c.Id,
        ChannelType = c.ChannelType,
        Destination = c.Destination,
        DestinationLabel = c.DestinationLabel,
        SortOrder = c.SortOrder,
        Metadata = c.Metadata is null ? null : Json(c.Metadata),
        HasSecret = c.Secret is not null,
    };

    /// <summary>A stored JSON tree as an object that serialises back to the same JSON; an empty object when it is not JSON.</summary>
    public static object Json(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return new { };
        }
    }
}
