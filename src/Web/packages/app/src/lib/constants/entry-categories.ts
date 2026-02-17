import type { Bolus, CarbIntake, BGCheck, Note, DeviceEvent } from "$lib/api";

export const ENTRY_CATEGORIES = {
  bolus: {
    id: "bolus" as const,
    name: "Insulin",
    description: "Bolus insulin deliveries",
    icon: "syringe" as const,
    colorClass: "text-blue-600 dark:text-blue-400",
    bgClass: "bg-blue-100 dark:bg-blue-900/30",
    borderClass: "border-blue-200 dark:border-blue-700",
  },
  carbs: {
    id: "carbs" as const,
    name: "Carbs",
    description: "Carbohydrate intake records",
    icon: "utensils" as const,
    colorClass: "text-green-600 dark:text-green-400",
    bgClass: "bg-green-100 dark:bg-green-900/30",
    borderClass: "border-green-200 dark:border-green-700",
  },
  bgCheck: {
    id: "bgCheck" as const,
    name: "BG Checks",
    description: "Blood glucose measurements",
    icon: "droplet" as const,
    colorClass: "text-red-600 dark:text-red-400",
    bgClass: "bg-red-100 dark:bg-red-900/30",
    borderClass: "border-red-200 dark:border-red-700",
  },
  note: {
    id: "note" as const,
    name: "Notes",
    description: "User annotations and announcements",
    icon: "file-text" as const,
    colorClass: "text-gray-600 dark:text-gray-400",
    bgClass: "bg-gray-100 dark:bg-gray-800/50",
    borderClass: "border-gray-200 dark:border-gray-600",
  },
  deviceEvent: {
    id: "deviceEvent" as const,
    name: "Device Events",
    description: "Sensor, pump, and site changes",
    icon: "smartphone" as const,
    colorClass: "text-orange-600 dark:text-orange-400",
    bgClass: "bg-orange-100 dark:bg-orange-900/30",
    borderClass: "border-orange-200 dark:border-orange-700",
  },
} as const;

export type EntryCategoryId = keyof typeof ENTRY_CATEGORIES;

/** Discriminated union for all v4 record types displayed in the entries table */
export type EntryRecord =
  | { kind: "bolus"; data: Bolus }
  | { kind: "carbs"; data: CarbIntake }
  | { kind: "bgCheck"; data: BGCheck }
  | { kind: "note"; data: Note }
  | { kind: "deviceEvent"; data: DeviceEvent };

/** Get the category style for an entry record */
export function getEntryStyle(kind: EntryCategoryId) {
  return ENTRY_CATEGORIES[kind];
}

/** Merge and sort multiple record types into a single timeline */
export function mergeEntryRecords(params: {
  boluses?: Bolus[];
  carbIntakes?: CarbIntake[];
  bgChecks?: BGCheck[];
  notes?: Note[];
  deviceEvents?: DeviceEvent[];
}): EntryRecord[] {
  const records: EntryRecord[] = [
    ...(params.boluses ?? []).map((d) => ({ kind: "bolus" as const, data: d })),
    ...(params.carbIntakes ?? []).map((d) => ({ kind: "carbs" as const, data: d })),
    ...(params.bgChecks ?? []).map((d) => ({ kind: "bgCheck" as const, data: d })),
    ...(params.notes ?? []).map((d) => ({ kind: "note" as const, data: d })),
    ...(params.deviceEvents ?? []).map((d) => ({ kind: "deviceEvent" as const, data: d })),
  ];
  return records.sort((a, b) => (b.data.mills ?? 0) - (a.data.mills ?? 0));
}

/** Count records by category */
export function countEntryRecords(records: EntryRecord[]): Record<EntryCategoryId | "all", number> {
  const counts = { all: records.length, bolus: 0, carbs: 0, bgCheck: 0, note: 0, deviceEvent: 0 };
  for (const r of records) counts[r.kind]++;
  return counts;
}
