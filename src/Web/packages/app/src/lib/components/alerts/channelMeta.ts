import Bell from "@lucide/svelte/icons/bell";
import BellRing from "@lucide/svelte/icons/bell-ring";
import MonitorSmartphone from "@lucide/svelte/icons/monitor-smartphone";
import WebhookIcon from "@lucide/svelte/icons/webhook";
import { ChannelType } from "$api-clients";

/**
 * Every channel kind but the legacy ones the picker never offers. Excluding rather than listing
 * means a kind the server adds fails the exhaustive switches in `channelText.svelte.ts`.
 */
export type ChannelKind = Exclude<
  ChannelType,
  | ChannelType.WebPush
  | ChannelType.Telegram
  | ChannelType.WhatsApp
  | ChannelType.HomeAssistant
>;

/**
 * Display data for a single notification channel kind. The union of every
 * field used by the two callers (rule row and channels section) so those callers
 * can stop maintaining their own parallel tables. Its text lives in
 * `channelText.svelte.ts`.
 *
 * Which kinds may be added, and which of them need a destination typed in, come
 * from the channel-status endpoint rather than from here.
 */
export interface ChannelMetaEntry {
  type: ChannelKind;
  /** Lucide icon component for first-party kinds. */
  icon?: typeof Bell;
  /** Path under `/logos/` for branded channels. Overrides `icon`. */
  logo?: string;
  /** Linked-platform key for getLinkedPlatforms. */
  platform?: string;
  destinationPlaceholder?: string;
  /** Shape the destination must match. Mirrors the server-side check. The message lives in `channelDestinationPatternMessage`. */
  destinationPattern?: RegExp;
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
    type: ChannelType.InApp,
    icon: BellRing,
  },
  {
    type: ChannelType.Webhook,
    icon: WebhookIcon,
    destinationPlaceholder: "https://example.com/webhook",
  },
  {
    type: ChannelType.DiscordDm,
    logo: "/logos/discord.png",
    platform: "discord",
  },
  {
    type: ChannelType.DiscordChannel,
    logo: "/logos/discord.png",
    destinationPlaceholder: "1234567890123456789",
    destinationPattern: /^\d{17,20}$/,
  },
  {
    type: ChannelType.SlackDm,
    logo: "/logos/slack.png",
    platform: "slack",
  },
  {
    type: ChannelType.SlackChannel,
    logo: "/logos/slack.png",
    destinationPlaceholder: "C0123456789",
    destinationPattern: /^[CGD][A-Z0-9]+$/,
  },
  {
    type: ChannelType.TelegramDm,
    logo: "/logos/telegram.png",
    platform: "telegram",
  },
  {
    type: ChannelType.TelegramGroup,
    logo: "/logos/telegram.png",
    destinationPlaceholder: "-1001234567890",
    destinationPattern: /^(-\d+|@[A-Za-z][A-Za-z0-9_]{4,31})$/,
  },
  {
    type: ChannelType.WhatsAppDm,
    logo: "/logos/whatsapp.png",
    platform: "whatsapp",
    destinationPlaceholder: "+15551234567",
    destinationPattern: /^\+[1-9]\d{6,14}$/,
  },
  {
    type: ChannelType.ResendEmail,
    logo: "/logos/email.jpg",
    platform: "resend",
    destinationPlaceholder: "user@example.com",
  },
  {
    type: ChannelType.DeviceAction,
    icon: MonitorSmartphone,
    isDeviceAction: true,
  },
];

const CHANNEL_META_BY_TYPE: Map<string, ChannelMetaEntry> = new Map(
  CHANNEL_META.map((m) => [m.type, m]),
);

/** Fast lookup by ChannelType. Returns undefined for unknown values. */
export function findChannelMeta(
  t: ChannelType | string | undefined,
): ChannelMetaEntry | undefined {
  if (t === undefined || t === null) return undefined;
  return CHANNEL_META_BY_TYPE.get(t);
}
