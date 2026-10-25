import { ChannelType } from "$api-clients";
import type { ChannelKind, ChannelMetaEntry } from "./channelMeta";

export function channelLabel(kind: ChannelKind): string {
  switch (kind) {
    case ChannelType.InApp:
      return "In-app";
    case ChannelType.Webhook:
      return "Webhook";
    case ChannelType.DiscordDm:
      return "Discord DM";
    case ChannelType.DiscordChannel:
      return "Discord channel";
    case ChannelType.SlackDm:
      return "Slack DM";
    case ChannelType.SlackChannel:
      return "Slack channel";
    case ChannelType.TelegramDm:
      return "Telegram DM";
    case ChannelType.TelegramGroup:
      return "Telegram group";
    case ChannelType.WhatsAppDm:
      return "WhatsApp DM";
    case ChannelType.ResendEmail:
      return "Email";
    case ChannelType.DeviceAction:
      return "Device";
    default: {
      const unhandled: never = kind;
      return unhandled;
    }
  }
}

export function channelDescription(kind: ChannelKind): string {
  switch (kind) {
    case ChannelType.InApp:
      return "Show alerts in the Nocturne notification center";
    case ChannelType.Webhook:
      return "POST to a custom URL";
    case ChannelType.DiscordDm:
      return "Direct message via the linked Discord identity";
    case ChannelType.DiscordChannel:
      return "Post to a Discord channel the Nocturne bot has joined";
    case ChannelType.SlackDm:
      return "Direct message via the linked Slack identity";
    case ChannelType.SlackChannel:
      return "Post to a Slack channel the Nocturne bot has joined";
    case ChannelType.TelegramDm:
      return "Direct message via the linked Telegram identity";
    case ChannelType.TelegramGroup:
      return "Post to a Telegram group the Nocturne bot has joined";
    case ChannelType.WhatsAppDm:
      return "Direct message via WhatsApp Business";
    case ChannelType.ResendEmail:
      return "Send alerts to an email address via Resend";
    case ChannelType.DeviceAction:
      return "Send an actuation intent to a registered device by kind";
    default: {
      const unhandled: never = kind;
      return unhandled;
    }
  }
}

/** The label of the destination field, or undefined for a kind with no destination typed in. */
export function channelDestinationLabel(kind: ChannelKind): string | undefined {
  switch (kind) {
    case ChannelType.Webhook:
      return "Webhook URL";
    case ChannelType.DiscordChannel:
    case ChannelType.SlackChannel:
      return "Channel ID";
    case ChannelType.TelegramGroup:
      return "Chat ID";
    case ChannelType.WhatsAppDm:
      return "Phone (E.164)";
    case ChannelType.ResendEmail:
      return "Email address";
    case ChannelType.InApp:
    case ChannelType.DiscordDm:
    case ChannelType.SlackDm:
    case ChannelType.TelegramDm:
    case ChannelType.DeviceAction:
      return undefined;
    default: {
      const unhandled: never = kind;
      return unhandled;
    }
  }
}

export function channelDestinationHelper(
  kind: ChannelKind
): string | undefined {
  switch (kind) {
    case ChannelType.DiscordDm:
      return "Sent to the Discord account you linked.";
    case ChannelType.DiscordChannel:
      return "In Discord, turn on Settings → Advanced → Developer Mode, then open the channel's menu and choose Copy Channel ID.";
    case ChannelType.SlackDm:
      return "Sent to the Slack account you linked.";
    case ChannelType.SlackChannel:
      return "In Slack, open the channel, choose View channel details, and copy the ID at the bottom.";
    case ChannelType.TelegramDm:
      return "Sent to the Telegram account you linked.";
    case ChannelType.TelegramGroup:
      return "Add the bot to the group, then use its chat ID (a negative number) or the group's @username.";
    case ChannelType.WhatsAppDm:
      return "Include the + and country code. Without the +, WhatsApp prepends the sending business number's country code.";
    case ChannelType.InApp:
    case ChannelType.Webhook:
    case ChannelType.ResendEmail:
    case ChannelType.DeviceAction:
      return undefined;
    default: {
      const unhandled: never = kind;
      return unhandled;
    }
  }
}

/** Shown when the destination does not match the kind's `destinationPattern`. */
export function channelDestinationPatternMessage(
  kind: ChannelKind
): string | undefined {
  switch (kind) {
    case ChannelType.DiscordChannel:
      return "A channel ID is 17-20 digits.";
    case ChannelType.SlackChannel:
      return "A channel ID starts with C, G or D.";
    case ChannelType.TelegramGroup:
      return "A group chat ID is a negative number, or an @username.";
    case ChannelType.WhatsAppDm:
      return "A phone number starts with + and a country code, digits only.";
    case ChannelType.InApp:
    case ChannelType.Webhook:
    case ChannelType.DiscordDm:
    case ChannelType.SlackDm:
    case ChannelType.TelegramDm:
    case ChannelType.ResendEmail:
    case ChannelType.DeviceAction:
      return undefined;
    default: {
      const unhandled: never = kind;
      return unhandled;
    }
  }
}

/**
 * Validation message for the destination of a channel that requires one, or null when it is
 * acceptable. The API runs the same checks and is authoritative; this only shortens the
 * feedback loop.
 */
export function destinationError(
  meta: ChannelMetaEntry | undefined,
  destination: string | undefined
): string | null {
  if (!meta) return null;
  const value = (destination ?? "").trim();
  if (value === "") {
    return `${channelDestinationLabel(meta.type) ?? channelLabel(meta.type)} is required.`;
  }
  if (meta.destinationPattern && !meta.destinationPattern.test(value)) {
    return channelDestinationPatternMessage(meta.type) ?? null;
  }
  return null;
}
