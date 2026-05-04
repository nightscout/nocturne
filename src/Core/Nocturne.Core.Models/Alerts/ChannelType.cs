using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Notification delivery channel for alert dispatch.
/// Each value represents a distinct transport mechanism for delivering alert notifications to users.
/// </summary>
/// <seealso cref="ChannelStatus"/>
/// <seealso cref="ChannelUnavailableReason"/>
[JsonConverter(typeof(JsonStringEnumConverter<ChannelType>))]
public enum ChannelType
{
    /// <summary>Browser push notification via the Web Push API.</summary>
    [EnumMember(Value = "web_push"), JsonStringEnumMemberName("web_push")]
    WebPush,

    /// <summary>In-app notification rendered inside the Nocturne UI.</summary>
    [EnumMember(Value = "in_app"), JsonStringEnumMemberName("in_app")]
    InApp,

    /// <summary>HTTP webhook POST to a user-configured URL.</summary>
    [EnumMember(Value = "webhook"), JsonStringEnumMemberName("webhook")]
    Webhook,

    /// <summary>Discord direct message to a user.</summary>
    [EnumMember(Value = "discord_dm"), JsonStringEnumMemberName("discord_dm")]
    DiscordDm,

    /// <summary>Discord message to a guild channel.</summary>
    [EnumMember(Value = "discord_channel"), JsonStringEnumMemberName("discord_channel")]
    DiscordChannel,

    /// <summary>Slack direct message to a user.</summary>
    [EnumMember(Value = "slack_dm"), JsonStringEnumMemberName("slack_dm")]
    SlackDm,

    /// <summary>Slack message to a workspace channel.</summary>
    [EnumMember(Value = "slack_channel"), JsonStringEnumMemberName("slack_channel")]
    SlackChannel,

    /// <summary>Telegram message (legacy, unspecified target type).</summary>
    [EnumMember(Value = "telegram"), JsonStringEnumMemberName("telegram")]
    Telegram,

    /// <summary>Telegram direct message to a user.</summary>
    [EnumMember(Value = "telegram_dm"), JsonStringEnumMemberName("telegram_dm")]
    TelegramDm,

    /// <summary>Telegram message to a group chat.</summary>
    [EnumMember(Value = "telegram_group"), JsonStringEnumMemberName("telegram_group")]
    TelegramGroup,

    /// <summary>WhatsApp message (legacy, unspecified target type).</summary>
    [EnumMember(Value = "whatsapp"), JsonStringEnumMemberName("whatsapp")]
    WhatsApp,

    /// <summary>WhatsApp direct message to a user.</summary>
    [EnumMember(Value = "whatsapp_dm"), JsonStringEnumMemberName("whatsapp_dm")]
    WhatsAppDm,
}
