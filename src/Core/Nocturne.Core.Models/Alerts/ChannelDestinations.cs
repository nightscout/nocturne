using System.Text.RegularExpressions;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Shape rules for an alert channel's destination string, applied when a rule is saved.
/// </summary>
/// <remarks>
/// The delivery path never inspects a destination: the bot service passes it straight to the
/// platform adapter (a Discord channel destination reaches <c>bot.channel(destination)</c>, a DM
/// destination reaches <c>bot.openDM(destination)</c>). A destination in the wrong shape therefore
/// produces a channel that is accepted, stored, and silently never delivers.
/// </remarks>
public static partial class ChannelDestinations
{
    /// <summary>
    /// Channel types whose destination the user supplies and that cannot deliver without one.
    /// DM channels are absent: their destination is resolved from the sender's linked chat
    /// identity rather than typed in.
    /// </summary>
    private static readonly HashSet<ChannelType> RequiredDestinationTypes =
    [
        ChannelType.Webhook,
        ChannelType.DiscordChannel,
        ChannelType.WhatsAppDm,
        ChannelType.ResendEmail,
    ];

    /// <summary>Channel types the Discord adapter addresses by snowflake ID.</summary>
    private static readonly HashSet<ChannelType> SnowflakeDestinationTypes =
    [
        ChannelType.DiscordChannel,
        ChannelType.DiscordDm,
    ];

    /// <summary>Whether a channel of this type is undeliverable without a destination.</summary>
    public static bool RequiresDestination(ChannelType channelType) =>
        RequiredDestinationTypes.Contains(channelType);

    /// <summary>Whether a channel of this type must carry a Discord snowflake ID.</summary>
    public static bool RequiresSnowflake(ChannelType channelType) =>
        SnowflakeDestinationTypes.Contains(channelType);

    /// <summary>
    /// Whether the value is a Discord snowflake — the 17-20 digit ID a channel, guild, or user is
    /// addressed by.
    /// </summary>
    public static bool IsSnowflake(string? value) =>
        value is not null && SnowflakePattern().IsMatch(value);

    [GeneratedRegex(@"^\d{17,20}$")]
    private static partial Regex SnowflakePattern();
}
