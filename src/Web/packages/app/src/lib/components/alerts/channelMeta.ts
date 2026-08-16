import type { ComponentType } from "svelte";
import {
  Bell,
  BellRing,
  MonitorSmartphone,
  Webhook as WebhookIcon,
} from "lucide-svelte";
import { ChannelType } from "$api-clients";

/**
 * Display metadata for a single notification channel kind. The union of every
 * field used by the two callers (rule row and channels section) so those callers
 * can stop maintaining their own parallel tables.
 *
 * Which kinds may be added, and which of them need a destination typed in, come
 * from the channel-status endpoint rather than from here.
 */
export interface ChannelMetaEntry {
  type: ChannelType;
  label: string;
  description: string;
  /** Lucide icon component for first-party kinds. */
  icon?: ComponentType;
  /** Path under `/logos/` for branded channels. Overrides `icon`. */
  logo?: string;
  /** Linked-platform key for getLinkedPlatforms. */
  platform?: string;
  destinationLabel?: string;
  destinationPlaceholder?: string;
  destinationHelper?: string;
  /** Shape the destination must match. Mirrors the server-side check. */
  destinationPattern?: RegExp;
  /** Shown when the destination does not match `destinationPattern`. */
  destinationPatternMessage?: string;
  /**
   * When true, this channel is authored by the dedicated device-action editor
   * (kind selector + capability checkboxes) rather than the generic
   * destination/label inputs. Its destination is a device kind and its metadata
   * carries the selected capabilities.
   */
  isDeviceAction?: boolean;
}

/** All known channel kinds. Kept in display order for the picker. */
export const CHANNEL_META: ChannelMetaEntry[] = [
  {
    type: ChannelType.WebPush,
    label: "Browser Push",
    description: "Receive alerts directly in your browser",
    icon: Bell as unknown as ComponentType,
  },
  {
    type: ChannelType.InApp,
    label: "In-App",
    description: "Show alerts in the Nocturne notification centre",
    icon: BellRing as unknown as ComponentType,
    destinationHelper: "Routed to your account automatically.",
  },
  {
    type: ChannelType.Webhook,
    label: "Webhook",
    description: "POST to a custom URL",
    icon: WebhookIcon as unknown as ComponentType,
    destinationLabel: "Webhook URL",
    destinationPlaceholder: "https://example.com/webhook",
  },
  {
    type: ChannelType.DiscordDm,
    label: "Discord DM",
    description: "Direct message via the linked Discord identity",
    logo: "/logos/discord.png",
    platform: "discord",
    destinationHelper: "Sent to the Discord account you linked.",
  },
  {
    type: ChannelType.DiscordChannel,
    label: "Discord channel",
    description: "Post to a Discord channel the Nocturne bot has joined",
    logo: "/logos/discord.png",
    destinationLabel: "Channel ID",
    destinationPlaceholder: "1234567890123456789",
    destinationHelper:
      "In Discord, turn on Settings → Advanced → Developer Mode, then right-click the channel and choose Copy Channel ID.",
    destinationPattern: /^\d{17,20}$/,
    destinationPatternMessage: "A channel ID is 17-20 digits.",
  },
  {
    type: ChannelType.SlackDm,
    label: "Slack DM",
    description: "Direct message via the linked Slack identity",
    logo: "/logos/slack.png",
    platform: "slack",
    destinationHelper: "Sent to the Slack account you linked.",
  },
  {
    type: ChannelType.SlackChannel,
    label: "Slack channel",
    description: "Post to a Slack channel the Nocturne bot has joined",
    logo: "/logos/slack.png",
    destinationLabel: "Channel ID",
    destinationPlaceholder: "C0123456789",
    destinationHelper:
      "In Slack, open the channel, choose View channel details, and copy the ID at the bottom.",
    destinationPattern: /^[CGD][A-Z0-9]+$/,
    destinationPatternMessage: "A channel ID starts with C, G or D.",
  },
  {
    type: ChannelType.TelegramDm,
    label: "Telegram DM",
    description: "Direct message via the linked Telegram identity",
    logo: "/logos/telegram.png",
    platform: "telegram",
    destinationHelper: "Sent to the Telegram account you linked.",
  },
  {
    type: ChannelType.TelegramGroup,
    label: "Telegram group",
    description: "Post to a Telegram group the Nocturne bot has joined",
    logo: "/logos/telegram.png",
    destinationLabel: "Chat ID",
    destinationPlaceholder: "-1001234567890",
    destinationHelper:
      "Add the bot to the group, then use its chat ID (a negative number) or the group's @username.",
    destinationPattern: /^(-\d+|@[A-Za-z][A-Za-z0-9_]{4,31})$/,
    destinationPatternMessage:
      "A group chat ID is a negative number, or an @username.",
  },
  {
    type: ChannelType.WhatsAppDm,
    label: "WhatsApp DM",
    description: "Direct message via WhatsApp Business",
    logo: "/logos/whatsapp.png",
    platform: "whatsapp",
    destinationLabel: "Phone (E.164)",
    destinationPlaceholder: "+15551234567",
    destinationHelper:
      "Include the + and country code. Without the +, WhatsApp prepends the sending business number's country code.",
    destinationPattern: /^\+[1-9]\d{6,14}$/,
    destinationPatternMessage:
      "A phone number starts with + and a country code, digits only.",
  },
  {
    type: ChannelType.ResendEmail,
    label: "Email",
    description: "Send alerts to an email address via Resend",
    logo: "/logos/email.jpg",
    platform: "resend",
    destinationLabel: "Email Address",
    destinationPlaceholder: "user@example.com",
  },
  {
    type: ChannelType.DeviceAction,
    label: "Device",
    description: "Send an actuation intent to a registered device by kind",
    icon: MonitorSmartphone as unknown as ComponentType,
    isDeviceAction: true,
  },
];

const CHANNEL_META_BY_TYPE: Map<string, ChannelMetaEntry> = new Map(
  CHANNEL_META.map((m) => [m.type as string, m]),
);

/**
 * Validation message for the destination of a channel that requires one, or null when it is
 * acceptable. The API runs the same checks and is authoritative; this only shortens the
 * feedback loop.
 */
export function destinationError(
  meta: ChannelMetaEntry | undefined,
  destination: string | undefined,
): string | null {
  if (!meta) return null;
  const value = (destination ?? "").trim();
  if (value === "") return `${meta.destinationLabel} is required.`;
  if (meta.destinationPattern && !meta.destinationPattern.test(value)) {
    return meta.destinationPatternMessage ?? null;
  }
  return null;
}

/** Fast lookup by ChannelType. Returns undefined for unknown values. */
export function findChannelMeta(
  t: ChannelType | string | undefined,
): ChannelMetaEntry | undefined {
  if (t === undefined || t === null) return undefined;
  return CHANNEL_META_BY_TYPE.get(t as string);
}
