/**
 * Normalizes an instance URL by removing trailing slashes.
 * Returns empty string if input is empty (for SSR safety).
 */
function normalize(instanceUrl: string): string {
  return instanceUrl.replace(/\/+$/, "");
}

/**
 * Builds the Prelude deep link that hands the Android app its Nocturne
 * instance URL to begin OAuth device-grant pairing.
 *
 * @param instanceUrl — the Nocturne instance origin URL (with or without trailing slash)
 */
export function buildPreludeDeepLink(instanceUrl: string): string {
  const normalized = normalize(instanceUrl);
  return `prelude://connect?url=${encodeURIComponent(normalized)}`;
}

/**
 * Builds the public connect page URL a phone QR scanner can open. Prelude's
 * in-app scanner reads this URL directly and recovers the instance origin;
 * a system camera opens it in the browser, where the trampoline page
 * redirects to {@link buildPreludeDeepLink}.
 */
export function buildConnectPageUrl(instanceUrl: string): string {
  return `${normalize(instanceUrl)}/connect/prelude`;
}
