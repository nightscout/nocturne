using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.SetupHub;

/// <summary>
/// An item on the setup hub. Declaration order is the order the hub lists them in.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SetupHubItemKey>))]
public enum SetupHubItemKey
{
    ConnectData,
    Alerts,
    Devices,
    Therapy,
    Sharing,
    About,
}

/// <summary>
/// Where a setup hub item stands. <see cref="Done"/> and <see cref="NotForMe"/> both resolve it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SetupHubItemState>))]
public enum SetupHubItemState
{
    Open,

    /// <summary>The thing the item sets up works. Never reverts.</summary>
    Done,

    /// <summary>The owner chose not to set it up. They can reopen it.</summary>
    NotForMe,
}

public record SetupHubItem(SetupHubItemKey Key, SetupHubItemState State);

/// <param name="Items">The items listed for this tenant, in hub order.</param>
/// <param name="Revision">
/// Identifies the hub's listed items and their states; any change to either changes it.
/// </param>
/// <param name="ShowStrip">
/// Whether the dashboard offers the hub: the tenant finished the new onboarding core, something is
/// still open, and the strip was not dismissed at this <paramref name="Revision"/>.
/// </param>
public record SetupHubStatus(
    IReadOnlyList<SetupHubItem> Items,
    int ResolvedCount,
    int OpenCount,
    int TotalCount,
    string Revision,
    bool ShowStrip);
