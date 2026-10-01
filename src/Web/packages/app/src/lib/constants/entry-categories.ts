import {
  TreatmentLogCategory,
  type Bolus,
  type CarbIntake,
  type BGCheck,
  type Note,
  type DeviceEvent,
  type BasalInjection,
} from "$lib/api";

export const ENTRY_CATEGORIES = {
  bolus: {
    id: "bolus" as const,
    name: "Insulin",
    description: "Bolus insulin deliveries",
    icon: "syringe" as const,
    colorClass: "text-entry-bolus",
    badge: "entry-bolus" as const,
  },
  carbs: {
    id: "carbs" as const,
    name: "Carbs",
    description: "Carbohydrate intake records",
    icon: "utensils" as const,
    colorClass: "text-entry-carbs",
    badge: "entry-carbs" as const,
  },
  bgCheck: {
    id: "bgCheck" as const,
    name: "BG Checks",
    description: "Blood glucose measurements",
    icon: "droplet" as const,
    colorClass: "text-entry-bg-check",
    badge: "entry-bg-check" as const,
  },
  note: {
    id: "note" as const,
    name: "Notes",
    description: "User annotations and announcements",
    icon: "file-text" as const,
    colorClass: "text-muted-foreground",
    badge: "secondary" as const,
  },
  deviceEvent: {
    id: "deviceEvent" as const,
    name: "Device Events",
    description: "Sensor, pump, and site changes",
    icon: "smartphone" as const,
    colorClass: "text-entry-device-event",
    badge: "entry-device-event" as const,
  },
  basalInjection: {
    id: "basalInjection" as const,
    name: "Long-acting injection",
    description: "Basal insulin injections (pen / syringe)",
    icon: "syringe" as const,
    colorClass: "text-entry-basal-injection",
    badge: "entry-basal-injection" as const,
  },
} as const;

export type EntryCategoryId = keyof typeof ENTRY_CATEGORIES;

/** Discriminated union for all v4 record types displayed in the entries table */
export type EntryRecord =
  | { kind: "bolus"; data: Bolus }
  | { kind: "carbs"; data: CarbIntake }
  | { kind: "bgCheck"; data: BGCheck }
  | { kind: "note"; data: Note }
  | { kind: "deviceEvent"; data: DeviceEvent }
  | { kind: "basalInjection"; data: BasalInjection };

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
  basalInjections?: BasalInjection[];
}): EntryRecord[] {
  const records: EntryRecord[] = [
    ...(params.boluses ?? []).map((d) => ({ kind: "bolus" as const, data: d })),
    ...(params.carbIntakes ?? []).map((d) => ({ kind: "carbs" as const, data: d })),
    ...(params.bgChecks ?? []).map((d) => ({ kind: "bgCheck" as const, data: d })),
    ...(params.notes ?? []).map((d) => ({ kind: "note" as const, data: d })),
    ...(params.deviceEvents ?? []).map((d) => ({ kind: "deviceEvent" as const, data: d })),
    ...(params.basalInjections ?? []).map((d) => ({ kind: "basalInjection" as const, data: d })),
  ];
  return records.sort((a, b) => (b.data.mills ?? 0) - (a.data.mills ?? 0));
}

/** Count records by category */
export function countEntryRecords(records: EntryRecord[]): Record<EntryCategoryId | "all", number> {
  const counts = {
    all: records.length,
    bolus: 0,
    carbs: 0,
    bgCheck: 0,
    note: 0,
    deviceEvent: 0,
    basalInjection: 0,
  };
  for (const r of records) counts[r.kind]++;
  return counts;
}

export type EntryCategoryFilter = EntryCategoryId | "all";

/** The Treatment Log's filter: one category (or all) and a free-text search. */
export interface EntryFilter {
  category: EntryCategoryFilter;
  search: string;
}

export function isEntryCategoryFilter(value: string | null): value is EntryCategoryFilter {
  return value === "all" || (value !== null && Object.hasOwn(ENTRY_CATEGORIES, value));
}

/** The backend's name for each filter category, for the Treatment Log stats endpoint. */
export const TREATMENT_LOG_CATEGORY: Record<EntryCategoryFilter, TreatmentLogCategory> = {
  all: TreatmentLogCategory.All,
  bolus: TreatmentLogCategory.Bolus,
  carbs: TreatmentLogCategory.Carbs,
  bgCheck: TreatmentLogCategory.BgCheck,
  note: TreatmentLogCategory.Note,
  deviceEvent: TreatmentLogCategory.DeviceEvent,
  basalInjection: TreatmentLogCategory.BasalInjection,
};

/**
 * The rows a Treatment Log filter keeps. Mirrors the backend's `TreatmentLogFilter`,
 * which selects the records behind the stats card, so the rows and the card's
 * figures cover the same records; a change to one must be made to the other.
 */
export function filterEntryRecords(records: EntryRecord[], filter: EntryFilter): EntryRecord[] {
  let filtered = records;

  if (filter.category !== "all") {
    filtered = filtered.filter((r) => r.kind === filter.category);
  }

  const query = filter.search.trim().toLowerCase();
  if (query) {
    filtered = filtered.filter((r) => {
      const searchable: string[] = [ENTRY_CATEGORIES[r.kind].name];

      switch (r.kind) {
        case "bolus":
          if (r.data.bolusType) searchable.push(r.data.bolusType);
          break;
        case "carbs":
          break;
        case "bgCheck":
          if (r.data.glucoseType) searchable.push(r.data.glucoseType);
          break;
        case "note":
          if (r.data.text) searchable.push(r.data.text);
          if (r.data.eventType) searchable.push(r.data.eventType);
          break;
        case "deviceEvent":
          if (r.data.eventType) searchable.push(r.data.eventType);
          if (r.data.notes) searchable.push(r.data.notes);
          break;
        case "basalInjection":
          if (r.data.insulinContext?.insulinName)
            searchable.push(r.data.insulinContext.insulinName);
          if (r.data.notes) searchable.push(r.data.notes);
          break;
      }

      if (r.data.dataSource) searchable.push(r.data.dataSource);
      if (r.data.app) searchable.push(r.data.app);
      if (r.data.device) searchable.push(r.data.device);

      return searchable.join(" ").toLowerCase().includes(query);
    });
  }

  return filtered;
}
