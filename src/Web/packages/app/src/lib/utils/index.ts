import { getLocalTimeZone, now, fromDate } from "@internationalized/date";

import {
  ArrowUp,
  ArrowUpRight,
  ArrowRight,
  ArrowDown,
  ArrowDownRight,
  HelpCircle,
  AlertTriangle,
} from "lucide-svelte";
import { canonicalDirection } from "@nocturne/ui/glucose";
import { formatLocale } from "$lib/utils/formatting";
import {
  Direction,
} from "$lib/api";

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type SvelteComponent = any;

type DirectionInfo = { label: string; icon: SvelteComponent; css: string };

/**
 * Shown for every direction no arrow can express — none reported, not computable, or a
 * spelling this build does not know. A trend we do not have must never read as a stable one.
 */
const unknownDirectionInfo: DirectionInfo = {
  label: "unknown",
  icon: HelpCircle,
  css: "text-gray-500",
};

/** Keyed by the canonical direction name {@link canonicalDirection} yields. */
const directionInfo: Partial<Record<string, DirectionInfo>> = {
  [Direction.TripleUp]: {
    label: "rising extremely fast",
    icon: ArrowUp,
    css: "text-red-500",
  },
  [Direction.DoubleUp]: {
    label: "rising very fast",
    icon: ArrowUp,
    css: "text-red-500",
  },
  [Direction.SingleUp]: {
    label: "rising",
    icon: ArrowUpRight,
    css: "text-orange-500",
  },
  [Direction.FortyFiveUp]: {
    label: "rising slowly",
    icon: ArrowUpRight,
    css: "text-yellow-500",
  },
  [Direction.Flat]: { label: "stable", icon: ArrowRight, css: "text-green-500" },
  [Direction.FortyFiveDown]: {
    label: "falling slowly",
    icon: ArrowDownRight,
    css: "text-yellow-500",
  },
  [Direction.SingleDown]: {
    label: "falling",
    icon: ArrowDownRight,
    css: "text-orange-500",
  },
  [Direction.DoubleDown]: {
    label: "falling very fast",
    icon: ArrowDown,
    css: "text-red-500",
  },
  [Direction.TripleDown]: {
    label: "falling extremely fast",
    icon: ArrowDown,
    css: "text-red-500",
  },
  [Direction.RateOutOfRange]: {
    label: "out of range",
    icon: AlertTriangle,
    css: "text-gray-500",
  },
  [Direction.CgmError]: {
    label: "sensor error",
    icon: AlertTriangle,
    css: "text-gray-500",
  },
  [Direction.NONE]: unknownDirectionInfo,
  [Direction.NotComputable]: unknownDirectionInfo,
};

/**
 * Get BG trend direction information. v1/v3 responses carry the space-separated Nightscout
 * spellings ("NOT COMPUTABLE"); v4 carries the enum member names. Both resolve to the same
 * entry, via the same fold the glyph and rotation tables use.
 */
export function getDirectionInfo(direction?: Direction | string): DirectionInfo {
  return directionInfo[canonicalDirection(direction)] ?? unknownDirectionInfo;
}

/** Enhanced relative time formatting with internationalization support */
const getRelativeTimeFormatter = (() => {
  let formatter: Intl.RelativeTimeFormat | null = null;
  let cachedFor: string | null = null;
  return (locale?: string) => {
    const wanted = locale || formatLocale();
    // Keyed on the requested tag, not on `resolvedOptions().locale`: ICU answers
    // "nb-NO" with "nb", so comparing against the resolved tag never matches and
    // rebuilds the formatter on every call.
    if (!formatter || wanted !== cachedFor) {
      cachedFor = wanted;
      formatter = new Intl.RelativeTimeFormat(wanted, {
        numeric: "auto",
        style: "long",
      });
    }
    return formatter;
  };
})();

/**
 * Generate human-readable time ago string with enhanced internationalization
 *
 * @param nowMs Reference time. Pass a ticking value (see `Now`) where the
 *   result is rendered, or the text freezes at the age it had on first render.
 */
export function timeAgo(
  timestamp: number | string,
  locale?: string,
  nowMs?: number
): string {
  // Validate input timestamp
  const timestampNum =
    typeof timestamp === "string" ? parseInt(timestamp) : timestamp;
  if (!isFinite(timestampNum) || isNaN(timestampNum) || timestampNum <= 0) {
    return "Unknown";
  }

  // Convert to DateValue using @internationalized/date for better timezone handling
  const inputDate = fromDate(new Date(timestampNum), getLocalTimeZone());
  const currentDate =
    nowMs === undefined
      ? now(getLocalTimeZone())
      : fromDate(new Date(nowMs), getLocalTimeZone());

  // Calculate difference in milliseconds
  const diffMs = currentDate.toDate().getTime() - inputDate.toDate().getTime();
  const absDiffMs = Math.abs(diffMs);

  // Get the relative time formatter for the specified locale
  const rtf = getRelativeTimeFormatter(locale);

  // Convert to appropriate time units and format
  if (absDiffMs < 60 * 1000) {
    // Less than 1 minute
    const seconds = Math.floor(diffMs / 1000);
    return rtf.format(-seconds, "second");
  } else if (absDiffMs < 60 * 60 * 1000) {
    // Less than 1 hour
    const minutes = Math.floor(diffMs / (60 * 1000));
    return rtf.format(-minutes, "minute");
  } else if (absDiffMs < 24 * 60 * 60 * 1000) {
    // Less than 1 day
    const hours = Math.floor(diffMs / (60 * 60 * 1000));
    return rtf.format(-hours, "hour");
  } else if (absDiffMs < 7 * 24 * 60 * 60 * 1000) {
    // Less than 1 week
    const days = Math.floor(diffMs / (24 * 60 * 60 * 1000));
    return rtf.format(-days, "day");
  } else if (absDiffMs < 30 * 24 * 60 * 60 * 1000) {
    // Less than 1 month (approximately)
    const weeks = Math.floor(diffMs / (7 * 24 * 60 * 60 * 1000));
    return rtf.format(-weeks, "week");
  } else if (absDiffMs < 365 * 24 * 60 * 60 * 1000) {
    // Less than 1 year
    const months = Math.floor(diffMs / (30 * 24 * 60 * 60 * 1000));
    return rtf.format(-months, "month");
  } else {
    // 1 year or more
    const years = Math.floor(diffMs / (365 * 24 * 60 * 60 * 1000));
    return rtf.format(-years, "year");
  }
}

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
