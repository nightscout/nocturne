using System.Text.RegularExpressions;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Which alert channel types the rule editor offers, where each one's destination comes from,
/// and the shape that destination must hold. Applied when a rule is saved.
/// </summary>
/// <remarks>
/// The delivery path never inspects a destination: the bot service hands it to the platform
/// adapter for the channel's type. A destination in the wrong shape therefore produces a
/// channel that is accepted, stored, and silently never delivers.
/// </remarks>
public static partial class ChannelDestinations
{
    /// <summary>
    /// Every channel type the rule editor offers, and where its destination comes from. A type
    /// missing from this map is not offered: <see cref="ChannelType.HomeAssistant"/> channels are
    /// authored by the Home Assistant integration rather than the editor, and the keys of
    /// <see cref="Superseded"/> have no delivery path at all.
    /// </summary>
    private static readonly Dictionary<ChannelType, ChannelDestinationMode> Modes = new()
    {
        [ChannelType.WebPush] = ChannelDestinationMode.AccountRouted,
        [ChannelType.InApp] = ChannelDestinationMode.AccountRouted,
        [ChannelType.Webhook] = ChannelDestinationMode.UserSupplied,
        [ChannelType.DiscordDm] = ChannelDestinationMode.LinkedIdentity,
        [ChannelType.DiscordChannel] = ChannelDestinationMode.UserSupplied,
        [ChannelType.SlackDm] = ChannelDestinationMode.LinkedIdentity,
        [ChannelType.SlackChannel] = ChannelDestinationMode.UserSupplied,
        [ChannelType.TelegramDm] = ChannelDestinationMode.LinkedIdentity,
        [ChannelType.TelegramGroup] = ChannelDestinationMode.UserSupplied,
        [ChannelType.WhatsAppDm] = ChannelDestinationMode.UserSupplied,
        [ChannelType.ResendEmail] = ChannelDestinationMode.UserSupplied,
        [ChannelType.DeviceAction] = ChannelDestinationMode.DeviceKind,
    };

    /// <summary>
    /// The chat platform each bot-delivered channel type is addressed through. One key serves
    /// three lookups that must agree: the bot adapter name, the <c>platform</c> column of the
    /// chat identity directory, and the platform names the bot heartbeat reports.
    /// </summary>
    private static readonly Dictionary<ChannelType, string> Platforms = new()
    {
        [ChannelType.DiscordDm] = "discord",
        [ChannelType.DiscordChannel] = "discord",
        [ChannelType.SlackDm] = "slack",
        [ChannelType.SlackChannel] = "slack",
        [ChannelType.TelegramDm] = "telegram",
        [ChannelType.TelegramGroup] = "telegram",
        [ChannelType.WhatsAppDm] = "whatsapp",
        [ChannelType.ResendEmail] = "resend",
    };

    /// <summary>
    /// Channel types that name a platform without naming a target, and the types that replace
    /// them. No provider claims them, so a dispatch falls through to the unsupported-type branch
    /// and is dropped.
    /// </summary>
    private static readonly Dictionary<ChannelType, ChannelType[]> Superseded = new()
    {
        [ChannelType.Telegram] = [ChannelType.TelegramDm, ChannelType.TelegramGroup],
        [ChannelType.WhatsApp] = [ChannelType.WhatsAppDm],
    };

    private static readonly Dictionary<ChannelType, DestinationShape> Shapes = new()
    {
        [ChannelType.DiscordDm] = new(DiscordSnowflake(), "a Discord ID (17-20 digits)"),
        [ChannelType.DiscordChannel] = new(DiscordSnowflake(), "a Discord ID (17-20 digits)"),
        [ChannelType.SlackDm] = new(SlackMemberId(), "a Slack member ID (U… or W…)"),
        [ChannelType.SlackChannel] = new(SlackChannelId(), "a Slack channel ID (C…, G… or D…)"),
        [ChannelType.TelegramDm] = new(TelegramUserId(), "a Telegram user ID (digits)"),
        [ChannelType.TelegramGroup] = new(
            TelegramGroupId(), "a Telegram group chat ID (-100…) or an @username"),
    };

    /// <summary>The channel types the rule editor offers, each classified by destination mode.</summary>
    public static IReadOnlyCollection<ChannelType> Offered => Modes.Keys;

    /// <summary>Where this channel type's destination comes from, or null when it is not offered.</summary>
    public static ChannelDestinationMode? ModeOf(ChannelType channelType) =>
        Modes.TryGetValue(channelType, out var mode) ? mode : null;

    /// <summary>Whether the user supplies this channel type's destination and it cannot deliver without one.</summary>
    public static bool RequiresDestination(ChannelType channelType) =>
        ModeOf(channelType) == ChannelDestinationMode.UserSupplied;

    /// <summary>Whether this channel type's destination is filled in from the caller's linked chat identity.</summary>
    public static bool ResolvesFromLinkedIdentity(ChannelType channelType) =>
        ModeOf(channelType) == ChannelDestinationMode.LinkedIdentity;

    /// <summary>The chat platform this channel type is addressed through, or null when it is not bot-delivered.</summary>
    public static string? PlatformOf(ChannelType channelType) =>
        Platforms.TryGetValue(channelType, out var platform) ? platform : null;

    /// <summary>
    /// The channel types that replace an undeliverable one, or null when this type has a
    /// delivery path.
    /// </summary>
    public static IReadOnlyList<ChannelType>? SupersededBy(ChannelType channelType) =>
        Superseded.TryGetValue(channelType, out var replacements) ? replacements : null;

    /// <summary>The shape this channel type's destination must hold, phrased for an error message.</summary>
    public static string? DescribeDestination(ChannelType channelType) =>
        Shapes.TryGetValue(channelType, out var shape) ? shape.Description : null;

    /// <summary>
    /// Whether the destination holds the shape the channel type's platform adapter addresses.
    /// Types with no shape rule accept anything, including nothing — a blank destination is
    /// rejected by <see cref="RequiresDestination"/> instead.
    /// </summary>
    public static bool IsWellFormed(ChannelType channelType, string? destination) =>
        !Shapes.TryGetValue(channelType, out var shape)
        || (destination is not null && shape.Pattern.IsMatch(destination));

    private sealed record DestinationShape(Regex Pattern, string Description);

    [GeneratedRegex(@"^\d{17,20}$")]
    private static partial Regex DiscordSnowflake();

    [GeneratedRegex("^[UW][A-Z0-9]+$")]
    private static partial Regex SlackMemberId();

    [GeneratedRegex("^[CGD][A-Z0-9]+$")]
    private static partial Regex SlackChannelId();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex TelegramUserId();

    [GeneratedRegex(@"^(-\d+|@[A-Za-z][A-Za-z0-9_]{4,31})$")]
    private static partial Regex TelegramGroupId();
}
