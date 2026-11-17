// A .svelte.ts module so wuchale extracts its strings; it holds no runes.
import { NotificationUrgency } from "#lib/api/index.js";
import { cn } from "#lib/utils/index.js";

/** How loud a tracker or legacy age pill is drawn. */
export type AlertLevel = "none" | "info" | "warn" | "hazard" | "urgent";

const LEVELS: Record<NotificationUrgency, AlertLevel> = {
  [NotificationUrgency.Info]: "info",
  [NotificationUrgency.Warn]: "warn",
  [NotificationUrgency.Hazard]: "hazard",
  [NotificationUrgency.Urgent]: "urgent",
};

export function alertLevel(urgency: NotificationUrgency | null): AlertLevel {
  return urgency ? LEVELS[urgency] : "none";
}

export const VALUE_TONE: Record<AlertLevel, string> = {
  none: "",
  info: "text-severity-info",
  warn: "text-severity-warn",
  hazard: "text-severity-hazard",
  urgent: "text-severity-urgent",
};

/** The share of a tracker's lifespan used, drawn as a hairline under the pill. */
export const PROGRESS_FILL: Record<AlertLevel, string> = {
  none: "bg-muted-foreground/40",
  info: "bg-severity-info",
  warn: "bg-severity-warn",
  hazard: "bg-severity-hazard",
  urgent: "bg-severity-urgent",
};

/** What a screen reader hears before the value of a pill that shows a warning icon. */
export function alertLevelPrefix(level: AlertLevel): string | null {
  switch (level) {
    case "warn":
      return "Warning:";
    case "hazard":
      return "Hazard:";
    case "urgent":
      return "Urgent:";
    default:
      return null;
  }
}

/** The pill's own shape; PopoverTrigger renders unstyled. */
export function trackerPillClasses(className?: string): string {
  return cn(
    "relative inline-flex flex-col items-start rounded-md px-3 py-1 text-left whitespace-nowrap transition-colors cursor-pointer select-none overflow-hidden hover:bg-accent/50",
    className
  );
}
