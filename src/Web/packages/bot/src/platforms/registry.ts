import { discord } from "./discord.js";
import { slack } from "./slack.js";
import { telegram } from "./telegram.js";
import { whatsapp } from "./whatsapp.js";
import { resend } from "./resend.js";
import type { ChannelKind, PlatformCredentials, PlatformModule } from "./platform.js";

/** Every platform the bot can run on. A new platform is one module and one entry here. */
export const PLATFORMS = [discord, slack, telegram, whatsapp, resend] as const satisfies readonly PlatformModule[];

export type PlatformName = (typeof PLATFORMS)[number]["name"];

/** Per platform: its credentials, `true` to read them from the environment, or `false` when off. */
export type PlatformSettings = Partial<Record<PlatformName, PlatformCredentials | boolean>>;

/** A platform's stored settings, as the API returns them decrypted. */
export interface StoredPlatformSettings {
  category?: string;
  enabled?: boolean;
  fields?: Readonly<Record<string, string>>;
}

const BY_NAME = new Map<string, PlatformModule>(PLATFORMS.map((p) => [p.name, p]));

const CHANNEL_TARGETS = new Map<string, { platform: PlatformModule; kind: ChannelKind }>(
  PLATFORMS.flatMap((platform: PlatformModule) =>
    Object.entries(platform.channelTypes).map(([type, kind]) => [type, { platform, kind }] as const),
  ),
);

export function platformModule(name: string): PlatformModule | undefined {
  return BY_NAME.get(name);
}

/** The name a user knows the chat platform by, from its adapter name. */
export function platformName(name: string): string {
  return platformModule(name)?.displayName ?? name.charAt(0).toUpperCase() + name.slice(1);
}

/** Where a user picks the default of their linked accounts on `name`, or null where there is no such page. */
export function defaultLinkSetting(name: string): string | null {
  return platformModule(name)?.defaultLinkSetting ?? null;
}

/** The platform an alert channel type is delivered on, and how it is addressed there. */
export function channelTarget(channelType: string): { platform: PlatformModule; kind: ChannelKind } | undefined {
  return CHANNEL_TARGETS.get(channelType);
}

/**
 * Which platforms run, and with which credentials. Stored settings own their platform: a disabled
 * entry turns it off whatever the environment holds. A platform with no stored settings, or every
 * platform when the settings could not be read (`null`), runs when the environment configures it.
 */
export function resolvePlatforms(
  stored: readonly StoredPlatformSettings[] | null,
  env: NodeJS.ProcessEnv = process.env,
): PlatformSettings {
  const settings: PlatformSettings = {};
  for (const platform of PLATFORMS) {
    const entry = stored?.find((s) => s.category === platform.name);
    settings[platform.name] = entry
      ? !!entry.enabled && platform.fromSettings(entry.fields ?? {})
      : platform.configuredInEnvironment(env);
  }
  return settings;
}
