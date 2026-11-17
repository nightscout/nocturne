import type { Bolus, CarbIntake } from "#lib/api/index.js";

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
