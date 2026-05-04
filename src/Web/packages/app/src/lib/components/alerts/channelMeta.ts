import type { ComponentType } from "svelte";
import {
  Bell,
  BellRing,
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
    destinationLabel: "",
    destinationPlaceholder: "",
    destinationRequired: false,
  },
  {
    type: ChannelType.DiscordChannel,
    label: "Discord channel",
    description: "Post to a Discord channel via webhook",
    logo: "/logos/discord.png",
    destinationInput: "url",
    destinationLabel: "Webhook URL",
    destinationPlaceholder: "https://discord.com/api/webhooks/…",
    destinationRequired: true,
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
];

const CHANNEL_META_BY_TYPE: Map<string, ChannelMetaEntry> = new Map(
  CHANNEL_META.map((m) => [m.type as string, m]),
);

/** Fast lookup by ChannelType. Returns undefined for unknown values. */
export function findChannelMeta(
  t: ChannelType | string | undefined,
): ChannelMetaEntry | undefined {
  if (t === undefined || t === null) return undefined;
  return CHANNEL_META_BY_TYPE.get(t as string);
}
