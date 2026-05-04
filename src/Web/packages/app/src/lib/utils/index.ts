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
import {
  Direction,
} from "$lib/api";

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type SvelteComponent = any;

/** Time utility functions */
export const times = {
  mins: (mins: number) => ({ msecs: mins * 60 * 1000 }),
  hours: (hours: number) => ({ msecs: hours * 60 * 60 * 1000 }),
  days: (days: number) => ({ msecs: days * 24 * 60 * 60 * 1000 }),
};

/** Unit conversion utilities */
export const units = {
  mgdlToMMOL: (mgdl: number): number => {
    return Math.round((mgdl / 18.01559) * 10) / 10;
  },
  mmolToMGDL: (mmol: number): number => {
    return Math.round(mmol * 18.01559);
  },
};

/** Format time based on user settings */
export function formatTime(
  date: Date | number,
  timeFormat: number = 12,
  compact: boolean = false
): string {
  const options: Intl.DateTimeFormatOptions = {
    hour: "numeric",
    minute: "2-digit",
  };
  date = typeof date === "number" ? new Date(date) : date;

  if (timeFormat === 24) {
    options.hour12 = false;
    return date.toLocaleTimeString("en-US", options);
  }

  if (compact) {
    options.minute = "numeric";
  }

  return date.toLocaleTimeString("en-US", options).toLowerCase();
}

/** Calculate BG trend direction based on raw delta value */
export function calculateDirection(delta: number): string {
  if (delta > 8) return "DoubleUp";
  if (delta > 5) return "SingleUp";
  if (delta > 2) return "FortyFiveUp";
  if (delta < -8) return "DoubleDown";
  if (delta < -5) return "SingleDown";
  if (delta < -2) return "FortyFiveDown";
  return "Flat";
}


/** Get BG trend direction information */
export function getDirectionInfo(direction?: Direction | string) {
  const directions: Partial<Record<
    Direction,
    { label: string; icon: SvelteComponent; css: string }
  >> = {
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
    [Direction.NotComputable]: {
      label: "unknown",
      icon: HelpCircle,
      css: "text-gray-500",
    },
    [Direction.RateOutOfRange]: {
      label: "out of range",
      icon: AlertTriangle,
      css: "text-gray-500",
    },
  };

  const dirValue = typeof direction === 'string' ? direction as Direction : direction;
  return directions[dirValue || Direction.Flat] || directions[Direction.Flat]!;
}

/** Determine BG status level based on thresholds */
export function getBGStatus(value: number, thresholds: any) {
  if (!thresholds) {
    thresholds = {
      bgHigh: 180,
      bgTargetTop: 140,
      bgTargetBottom: 80,
      bgLow: 55,
    };
  }

  if (value >= thresholds.bgHigh) return "urgent-high";
  if (value <= thresholds.bgLow) return "urgent-low";
  if (value > thresholds.bgTargetTop) return "high";
  if (value < thresholds.bgTargetBottom) return "low";
  return "in-range";
}

/** Get color class for BG status */
export function getBGColorClass(status: string) {
  const colors: Record<string, string> = {
    "urgent-high": "bg-red-500 text-white",
    "urgent-low": "bg-red-500 text-white",
    high: "bg-orange-500 text-white",
    low: "bg-yellow-500 text-black",
    "in-range": "bg-green-500 text-white",
  };

  return colors[status] || "bg-gray-500 text-white";
}

/** Check if data is stale based on timestamp */
export function isDataStale(
  timestamp: number,
  thresholdMinutes: number = 15
): boolean {
  const now = Date.now();
  const diffMinutes = (now - timestamp) / (60 * 1000);
  return diffMinutes > thresholdMinutes;
}

/** Enhanced relative time formatting with internationalization support */
const getRelativeTimeFormatter = (() => {
  let formatter: Intl.RelativeTimeFormat | null = null;
  return (locale?: string) => {
    if (
      !formatter ||
      (locale && locale !== formatter.resolvedOptions().locale)
    ) {
      formatter = new Intl.RelativeTimeFormat(locale || "en", {
        numeric: "auto",
        style: "long",
      });
    }
    return formatter;
  };
})();

/** Generate human-readable time ago string with enhanced internationalization */
export function timeAgo(timestamp: number | string, locale?: string): string {
  // Validate input timestamp
  const timestampNum =
    typeof timestamp === "string" ? parseInt(timestamp) : timestamp;
  if (!isFinite(timestampNum) || isNaN(timestampNum) || timestampNum <= 0) {
    return "Unknown";
  }

  // Convert to DateValue using @internationalized/date for better timezone handling
  const inputDate = fromDate(new Date(timestampNum), getLocalTimeZone());
  const currentDate = now(getLocalTimeZone());

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
