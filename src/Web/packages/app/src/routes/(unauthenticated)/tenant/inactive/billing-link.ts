import type { SupportChannelConfig } from "$api/generated/nocturne-api-client";

// API mode is a POST-only intake endpoint, not a page a visitor can open.
export function resolveBillingLink(
  config: SupportChannelConfig | null | undefined
): { url: string; label: string | null } | null {
  if (config?.mode !== "redirect" || !config.url) return null;

  return { url: config.url, label: config.label ?? null };
}
