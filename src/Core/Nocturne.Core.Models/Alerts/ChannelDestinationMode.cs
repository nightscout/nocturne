namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Where an offered <see cref="ChannelType"/>'s destination comes from.
/// </summary>
/// <seealso cref="ChannelDestinations"/>
public enum ChannelDestinationMode
{
    /// <summary>
    /// No destination: delivery is addressed to the account itself (browser push
    /// subscriptions, the in-app notification centre).
    /// </summary>
    AccountRouted,

    /// <summary>The user types the destination and it is shape-checked on save.</summary>
    UserSupplied,

    /// <summary>
    /// Filled in on save from the caller's active link in the chat identity directory for the
    /// channel's platform.
    /// </summary>
    LinkedIdentity,

    /// <summary>
    /// A registered device kind, checked against the device catalog rather than a text shape.
    /// </summary>
    DeviceKind,
}
