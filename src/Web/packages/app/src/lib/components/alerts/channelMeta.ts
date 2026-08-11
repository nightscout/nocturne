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
 * field used by the three callers (rule row, picker, and channels section) so
 * those callers can stop maintaining their own parallel tables.
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
  /** Render type for the destination input. */
  destinationInput?: "url" | "text";
  destinationLabel?: string;
  destinationPlaceholder?: string;
  destinationHelper?: string;
  destinationRequired?: boolean;
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
    destinationLabel: "",
    destinationPlaceholder: "",
    destinationRequired: false,
  },
  {
    type: ChannelType.InApp,
    label: "In-App",
    description: "Show alerts in the Nocturne notification centre",
    icon: BellRing as unknown as ComponentType,
    destinationHelper: "Routed to your account automatically.",
    destinationLabel: "",
    destinationPlaceholder: "",
    destinationRequired: false,
  },
  {
    type: ChannelType.Webhook,
    label: "Webhook",
    description: "POST to a custom URL",
    icon: WebhookIcon as unknown as ComponentType,
    destinationInput: "url",
    destinationLabel: "Webhook URL",
    destinationPlaceholder: "https://example.com/webhook",
    destinationRequired: true,
  },
  {
    type: ChannelType.DiscordDm,
    label: "Discord DM",
    description: "Direct message via the linked Discord identity",
    logo: "/logos/discord.png",
    platform: "discord",
    destinationHelper: "Sent to the Discord account you linked.",
    destinationLabel: "",
    destinationPlaceholder: "",
    destinationRequired: false,
  },
  {
    type: ChannelType.DiscordChannel,
    label: "Discord channel",
    description: "Post to a Discord channel the Nocturne bot has joined",
    logo: "/logos/discord.png",
    destinationInput: "text",
    destinationLabel: "Channel ID",
    destinationPlaceholder: "1234567890123456789",
    destinationHelper:
      "In Discord, turn on Settings → Advanced → Developer Mode, then right-click the channel and choose Copy Channel ID.",
    destinationRequired: true,
    destinationPattern: /^\d{17,20}$/,
    destinationPatternMessage: "A channel ID is 17-20 digits.",
  },
  {
    type: ChannelType.SlackDm,
    label: "Slack DM",
    description: "Direct message in a Slack workspace",
    logo: "/logos/slack.png",
    platform: "slack",
    destinationLabel: "",
    destinationPlaceholder: "",
    destinationRequired: false,
  },
  {
    type: ChannelType.SlackChannel,
    label: "Slack channel",
    description: "Post to a Slack channel",
    logo: "/logos/slack.png",
    destinationLabel: "",
    destinationPlaceholder: "",
    destinationRequired: false,
  },
  {
    type: ChannelType.Telegram,
    label: "Telegram",
    description: "Send alerts to your Telegram chat",
    logo: "/logos/telegram.png",
    platform: "telegram",
    destinationLabel: "",
    destinationPlaceholder: "",
    destinationRequired: false,
  },
  {
    type: ChannelType.TelegramDm,
    label: "Telegram DM",
    description: "Direct message via the linked Telegram identity",
    logo: "/logos/telegram.png",
    platform: "telegram",
    destinationLabel: "",
    destinationPlaceholder: "",
    destinationRequired: false,
  },
  {
    type: ChannelType.TelegramGroup,
    label: "Telegram group",
    description: "Post to a Telegram group chat",
    logo: "/logos/telegram.png",
    destinationLabel: "",
    destinationPlaceholder: "",
    destinationRequired: false,
  },
  {
    type: ChannelType.WhatsApp,
    label: "WhatsApp",
    description: "Send alerts to your WhatsApp",
    logo: "/logos/whatsapp.png",
    platform: "whatsapp",
    destinationLabel: "",
    destinationPlaceholder: "",
    destinationRequired: false,
  },
  {
    type: ChannelType.WhatsAppDm,
    label: "WhatsApp DM",
    description: "Direct message via WhatsApp Business",
    logo: "/logos/whatsapp.png",
    platform: "whatsapp",
    destinationLabel: "Phone (E.164)",
    destinationPlaceholder: "+15551234567",
    destinationRequired: true,
  },
  {
    type: ChannelType.ResendEmail,
    label: "Email",
    description: "Send alerts to an email address via Resend",
    logo: "/logos/email.jpg",
    platform: "resend",
    destinationInput: "text",
    destinationLabel: "Email Address",
    destinationPlaceholder: "user@example.com",
    destinationRequired: true,
  },
  {
    type: ChannelType.DeviceAction,
    label: "Device",
    description: "Send an actuation intent to a registered device by kind",
    icon: MonitorSmartphone as unknown as ComponentType,
    isDeviceAction: true,
    destinationRequired: false,
  },
];

const CHANNEL_META_BY_TYPE: Map<string, ChannelMetaEntry> = new Map(
  CHANNEL_META.map((m) => [m.type as string, m]),
);

/**
 * Validation message for a channel's destination, or null when it is acceptable. The API
 * runs the same checks and is authoritative; this only shortens the feedback loop.
 */
export function destinationError(
  meta: ChannelMetaEntry | undefined,
  destination: string | undefined,
): string | null {
  if (!meta?.destinationRequired) return null;
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
