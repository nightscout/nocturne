namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Where an offered <see cref="ChannelType"/>'s destination comes from.
/// </summary>
/// <seealso cref="ChannelDestinations"/>
public enum ChannelDestinationMode
{
    /// <summary>
    /// A member's subject id: the caller's own when the editor leaves it blank, otherwise a
    /// member of the rule's tenant. Checked on save and again on delivery, because the in-app
    /// notification centre lists a notification only to the subject it names.
    /// </summary>
    Member,

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
