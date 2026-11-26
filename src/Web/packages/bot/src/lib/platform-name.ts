const PLATFORM_NAMES: Record<string, string> = {
  discord: "Discord",
  slack: "Slack",
  telegram: "Telegram",
  whatsapp: "WhatsApp",
};

/** The name a user knows the chat platform by, from its adapter name. */
export function platformName(platform: string): string {
  return PLATFORM_NAMES[platform] ?? platform.charAt(0).toUpperCase() + platform.slice(1);
}

/**
 * Where a user picks the default of their linked Nocturne accounts, as the end of a sentence. The
 * only settings page for links is Discord's, so the other platforms point to none (#4325).
 */
export function defaultLinkSetting(platform: string): string | null {
  return platform === "discord" ? "set a default in Settings → Integrations → Discord" : null;
}
