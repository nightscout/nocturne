import {
  Droplet,
  PieChart,
  Syringe,
  Cpu,
  HeartPulse,
  Footprints,
  Utensils,
  FileText,
} from "lucide-svelte";
import type { ComponentType } from "svelte";

/**
 * A data category that can be shared with anonymous (public-link) viewers. Each maps to a single
 * read-permission atom — the same atoms the backend validates against
 * (`TenantPermissions.PublicShareScopes`) and resolves for the Public subject. Order matches the
 * Sharing & Privacy scope grid.
 */
export interface PublicDataCategory {
  /** The read-permission atom granted to the Public subject when this category is shared. */
  readonly scope: string;
  readonly name: string;
  readonly description: string;
  readonly icon: ComponentType;
}

export const publicDataCategories: PublicDataCategory[] = [
  { scope: "glucose.read", name: "Blood Glucose", description: "CGM readings and current value", icon: Droplet },
  { scope: "statistics.read", name: "Statistics", description: "Time-in-range, A1c, and averages", icon: PieChart },
  { scope: "treatments.read", name: "Treatments", description: "Insulin doses and carb entries", icon: Syringe },
  { scope: "devices.read", name: "Device Status", description: "Pump, CGM, and phone status", icon: Cpu },
  { scope: "heartrate.read", name: "Heart Rate", description: "Data from wearables", icon: HeartPulse },
  { scope: "stepcount.read", name: "Step Count", description: "Daily activity totals", icon: Footprints },
  { scope: "food.read", name: "Food & Meals", description: "Meals and nutrition log", icon: Utensils },
  { scope: "reports.read", name: "Reports", description: "Shareable reports", icon: FileText },
];

/** Humanised list joiner: "a", "a and b", "a, b, and c". */
export function formatList(items: string[]): string {
  if (items.length === 0) return "";
  if (items.length === 1) return items[0];
  if (items.length === 2) return `${items[0]} and ${items[1]}`;
  return `${items.slice(0, -1).join(", ")}, and ${items[items.length - 1]}`;
}
