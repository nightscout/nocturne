import Droplet from "@lucide/svelte/icons/droplet";
import Syringe from "@lucide/svelte/icons/syringe";
import Cpu from "@lucide/svelte/icons/cpu";
import HeartPulse from "@lucide/svelte/icons/heart-pulse";
import Footprints from "@lucide/svelte/icons/footprints";
import Utensils from "@lucide/svelte/icons/utensils";
import FileText from "@lucide/svelte/icons/file-text";
import SlidersHorizontal from "@lucide/svelte/icons/sliders-horizontal";
import Moon from "@lucide/svelte/icons/moon";
import Bell from "@lucide/svelte/icons/bell";
import type { Component } from "svelte";

/**
 * A data category a link can share with someone who has no account. Each maps to one read scope.
 * Which categories a link may carry is the API's answer (`shareableScopes` on the share link, the
 * guest-link scope options); this list only names and draws them, in the order the scope grids
 * show them.
 */
export interface DataCategory {
  /** The read scope the link holds when this category is shared. */
  readonly scope: string;
  readonly name: string;
  readonly description: string;
  readonly icon: Component;
}

export const dataCategories: DataCategory[] = [
  { scope: "glucose.read", name: "Blood glucose", description: "CGM readings and current value", icon: Droplet },
  { scope: "treatments.read", name: "Treatments", description: "Insulin doses and carb entries", icon: Syringe },
  { scope: "devices.read", name: "Device status", description: "Pump, CGM, and closed-loop status, including IOB and automated insulin", icon: Cpu },
  { scope: "therapy.read", name: "Therapy settings", description: "Basal rates, carb ratios, sensitivity and targets", icon: SlidersHorizontal },
  { scope: "alerts.read", name: "Alerts", description: "Alarms that are firing now", icon: Bell },
  { scope: "heartrate.read", name: "Heart rate", description: "Data from wearables", icon: HeartPulse },
  { scope: "stepcount.read", name: "Step count", description: "Daily activity totals", icon: Footprints },
  { scope: "sleep.read", name: "Sleep", description: "Sleep sessions and stages", icon: Moon },
  { scope: "food.read", name: "Food & meals", description: "Meals and nutrition log", icon: Utensils },
  { scope: "reports.read", name: "Reports", description: "Shareable reports", icon: FileText },
];

/** The categories among {@link dataCategories} whose scope is in `scopes`, in display order. */
export function categoriesIn(scopes: readonly string[] | undefined): DataCategory[] {
  return dataCategories.filter((c) => scopes?.includes(c.scope));
}

/** Humanised list joiner: "a", "a and b", "a, b, and c". */
export function formatList(items: string[]): string {
  if (items.length === 0) return "";
  if (items.length === 1) return items[0];
  if (items.length === 2) return `${items[0]} and ${items[1]}`;
  return `${items.slice(0, -1).join(", ")}, and ${items[items.length - 1]}`;
}
