import type { Treatment, Bolus, CarbIntake } from "$lib/api";

/**
 * Treatment category definitions for UI organization
 * Each category groups related event types together
 */
export const TREATMENT_CATEGORIES = {
  bolus: {
    id: "bolus",
    name: "Bolus & Insulin",
    description: "Insulin doses, corrections, and combo boluses",
    eventTypes: ["Snack Bolus", "Meal Bolus", "Correction Bolus", "Combo Bolus"],
    icon: "syringe" as const,
    colorClass: "text-entry-bolus",
    badge: "entry-bolus" as const,
  },
  basal: {
    id: "basal",
    name: "Basal & Profiles",
    description: "Temp basals and profile switches",
    eventTypes: ["Temp Basal Start", "Temp Basal End", "Temp Basal", "Profile Switch"],
    icon: "activity" as const,
    colorClass: "text-entry-basal",
    badge: "entry-basal" as const,
  },
  carbs: {
    id: "carbs",
    name: "Carbs & Nutrition",
    description: "Meals, snacks, and carb corrections",
    eventTypes: ["Carb Correction"],
    icon: "utensils" as const,
    colorClass: "text-entry-carbs",
    badge: "entry-carbs" as const,
  },
  device: {
    id: "device",
    name: "Device Events",
    description: "Sensor, pump, and site changes",
    eventTypes: [
      "Site Change",
      "Sensor Start",
      "Sensor Change",
      "Sensor Stop",
      "Pump Battery Change",
      "Insulin Change",
    ],
    icon: "smartphone" as const,
    colorClass: "text-entry-device-event",
    badge: "entry-device-event" as const,
  },
  notes: {
    id: "notes",
    name: "Notes & Alerts",
    description: "Notes, announcements, and BG checks",
    eventTypes: ["BG Check", "Note", "Announcement", "Question", "D.A.D. Alert"],
    icon: "file-text" as const,
    colorClass: "text-muted-foreground",
    badge: "secondary" as const,
  },
} as const;

export type TreatmentCategoryId = keyof typeof TREATMENT_CATEGORIES;
export type TreatmentCategory = (typeof TREATMENT_CATEGORIES)[TreatmentCategoryId];

/**
 * Get the category for an event type
 */
export function getCategoryForEventType(eventType: string): TreatmentCategoryId | null {
  const category = Object.values(TREATMENT_CATEGORIES).find((c) =>
    c.eventTypes.some((type) => type === eventType)
  );
  return category?.id ?? null;
}

/**
 * Get style classes for an event type
 */
export function getEventTypeStyle(eventType: string): { colorClass: string } {
  const categoryId = getCategoryForEventType(eventType);
  if (categoryId) {
    return { colorClass: TREATMENT_CATEGORIES[categoryId].colorClass };
  }
  return { colorClass: "text-muted-foreground" };
}

/**
 * Sort options for treatments
 */
export type TreatmentSortField =
  | "time"
  | "eventType"
  | "insulin"
  | "carbs"
  | "enteredBy";

export type SortDirection = "asc" | "desc";

/**
 * Filter options for treatments table
 */
export interface TreatmentFilters {
  search: string;
  categories: TreatmentCategoryId[];
  eventTypes: string[];
  hasInsulin: boolean | null;
  hasCarbs: boolean | null;
  showAnomaliesOnly: boolean;
  dateRange: {
    from: Date;
    to: Date;
  };
}

/**
 * Default filter state
 */
export function getDefaultFilters(dateRange: { from: Date; to: Date }): TreatmentFilters {
  return {
    search: "",
    categories: [],
    eventTypes: [],
    hasInsulin: null,
    hasCarbs: null,
    showAnomaliesOnly: false,
    dateRange,
  };
}

/**
 * Apply filters to treatments
 */
export function filterTreatments(
  treatments: Treatment[],
  filters: TreatmentFilters
): Treatment[] {
  return treatments.filter((t) => {
    // Search filter
    if (filters.search) {
      const query = filters.search.toLowerCase();
      const searchable = [
        t.eventType,
        t.notes,
        t.enteredBy,
        t.reason,
        t.profile,
      ]
        .filter(Boolean)
        .join(" ")
        .toLowerCase();
      if (!searchable.includes(query)) return false;
    }

    // Category filter
    if (filters.categories.length > 0) {
      const category = getCategoryForEventType(t.eventType || "");
      if (!category || !filters.categories.includes(category)) return false;
    }

    // Event type filter
    if (filters.eventTypes.length > 0) {
      if (!filters.eventTypes.includes(t.eventType || "")) return false;
    }

    // Has insulin filter
    if (filters.hasInsulin !== null) {
      const hasInsulin = t.insulin !== undefined && t.insulin !== null && t.insulin > 0;
      if (filters.hasInsulin !== hasInsulin) return false;
    }

    // Has carbs filter
    if (filters.hasCarbs !== null) {
      const hasCarbs = t.carbs !== undefined && t.carbs !== null && t.carbs > 0;
      if (filters.hasCarbs !== hasCarbs) return false;
    }

    return true;
  });
}

/**
 * Count treatments by category and event type for UI display purposes only.
 * All insulin/carb totals should come from backend TreatmentSummary.
 */
export interface TreatmentCounts {
  total: number;
  byCategoryCount: Record<TreatmentCategoryId, number>;
  byEventTypeCount: Record<string, number>;
}

/**
 * Count treatments by category and event type.
 * NOTE: This only counts treatments for UI categorization tabs/filters.
 * Do NOT use this for insulin/carb calculations - use backend TreatmentSummary instead.
 */
export function countTreatmentsByCategory(treatments: Treatment[]): TreatmentCounts {
  const counts: TreatmentCounts = {
    total: treatments.length,
    byCategoryCount: {
      bolus: 0,
      basal: 0,
      carbs: 0,
      device: 0,
      notes: 0,
    },
    byEventTypeCount: {},
  };

  for (const t of treatments) {
    // Category count
    const category = getCategoryForEventType(t.eventType || "");
    if (category) {
      counts.byCategoryCount[category]++;
    }

    // Event type count
    const eventType = t.eventType || "<none>";
    counts.byEventTypeCount[eventType] = (counts.byEventTypeCount[eventType] || 0) + 1;
  }

  return counts;
}

// ─── V4 Decomposed Types ────────────────────────────────────────────────────

export type BolusRow = Bolus & { rowType: "bolus" };
export type CarbIntakeRow = CarbIntake & { rowType: "carbIntake" };
export type TreatmentRow = BolusRow | CarbIntakeRow;

export const V4_CATEGORIES = {
  bolus: {
    id: "bolus" as const,
    name: "Bolus",
    colorClass: "text-entry-bolus",
    badge: "entry-bolus" as const,
  },
  carbs: {
    id: "carbs" as const,
    name: "Carbs",
    colorClass: "text-entry-carbs",
    badge: "entry-carbs" as const,
  },
} as const;

export type V4CategoryId = keyof typeof V4_CATEGORIES;

export interface V4TreatmentCounts {
  total: number;
  bolus: number;
  carbs: number;
}

export function mergeTreatmentRows(boluses: Bolus[], carbIntakes: CarbIntake[]): TreatmentRow[] {
  const bolusRows: BolusRow[] = boluses.map((b) => ({ ...b, rowType: "bolus" as const }));
  const carbRows: CarbIntakeRow[] = carbIntakes.map((c) => ({ ...c, rowType: "carbIntake" as const }));
  return [...bolusRows, ...carbRows].sort((a, b) => (b.mills ?? 0) - (a.mills ?? 0));
}

export function countV4Rows(rows: TreatmentRow[]): V4TreatmentCounts {
  let bolus = 0;
  let carbs = 0;
  for (const r of rows) {
    if (r.rowType === "bolus") bolus++;
    else carbs++;
  }
  return { total: rows.length, bolus, carbs };
}

export function getRowTypeStyle(rowType: "bolus" | "carbIntake") {
  if (rowType === "bolus") return V4_CATEGORIES.bolus;
  return V4_CATEGORIES.carbs;
}
