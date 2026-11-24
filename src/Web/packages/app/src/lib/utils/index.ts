// Re-export UI utilities from shared package
export {
  cn,
  copyToClipboard,
  type WithoutChild,
  type WithoutChildren,
  type WithoutChildrenOrChild,
  type WithElementRef,
  type Prettify,
} from "@nocturne/ui/utils";

export interface DateRange {
  /** ISO 8601 */
  start: string;
  /** ISO 8601 */
  end: string;
}

/**
 * Base64-encode a string of any content.
 *
 * `btoa` throws on code points above U+00FF, so it cannot carry text the user
 * typed — an accented or non-Latin name is enough to break it.
 */
export function encodeBase64Utf8(value: string): string {
  const bytes = new TextEncoder().encode(value);
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

/**
 * Decode a string produced by {@link encodeBase64Utf8}.
 *
 * Payloads written by plain `btoa` still decode: ASCII is identical in UTF-8,
 * and a Latin-1 payload that isn't valid UTF-8 falls back to the byte-per-char
 * reading `atob` gives.
 */
export function decodeBase64Utf8(encoded: string): string {
  const binary = atob(encoded);
  const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0));
  try {
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    return binary;
  }
}

/**
 * Generate a UUID v4 string
 * Uses crypto.randomUUID() if available, otherwise falls back to a polyfill
 */
export function randomUUID(): string {
  // Use native crypto.randomUUID() if available (Node.js 15.6+, modern browsers)
  if (typeof crypto !== "undefined" && crypto.randomUUID) {
    return crypto.randomUUID();
  }

  // Fallback polyfill for environments without crypto.randomUUID()
  // Generate a UUID v4 compliant string
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === "x" ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}
