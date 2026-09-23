import type { OidcProviderInfo } from "$lib/api/generated/nocturne-api-client";

/**
 * A provider's button colours for `<Button variant="brand">`, or undefined when the API
 * could not pick a legible foreground for its configured colour.
 */
export function brandColors(
  provider: Pick<OidcProviderInfo, "buttonColor" | "buttonForegroundColor">
): { background: string; foreground: string } | undefined {
  const { buttonColor, buttonForegroundColor } = provider;
  return buttonColor && buttonForegroundColor
    ? { background: buttonColor, foreground: buttonForegroundColor }
    : undefined;
}
