import { describe, it, expect } from "vitest";
import {
  filterEntryRecords,
  isEntryCategoryFilter,
  mergeEntryRecords,
  type EntryRecord,
} from "./entry-categories";

const records: EntryRecord[] = mergeEntryRecords({
  boluses: [{ id: "bolus-1", insulin: 3, mills: 1_000, bolusType: "Square" }],
  carbIntakes: [{ id: "carbs-1", carbs: 30, mills: 2_000, app: "meal-logger" }],
  bgChecks: [{ id: "bg-1", mgdl: 120, mills: 3_000, glucoseType: "Finger" }],
  notes: [
    { id: "note-1", text: "Evening walk", mills: 4_000 },
    { id: "note-2", eventType: "Announcement", mills: 4_500 },
  ],
  deviceEvents: [
    { id: "dev-1", eventType: "Site Change", mills: 5_000 },
    { id: "dev-2", notes: "replaced reservoir", mills: 5_500, device: "pump-x" },
  ],
  basalInjections: [
    {
      id: "basal-1",
      mills: 6_000,
      insulinContext: { insulinName: "Longacting-A" },
      notes: "left thigh",
    },
  ],
} as Parameters<typeof mergeEntryRecords>[0]);

const idsFor = (category: string, search: string) =>
  filterEntryRecords(records, { category: category as never, search }).map((r) => r.data.id);

describe("isEntryCategoryFilter", () => {
  it("accepts 'all' and every category id", () => {
    for (const value of ["all", "bolus", "carbs", "bgCheck", "note", "deviceEvent", "basalInjection"]) {
      expect(isEntryCategoryFilter(value)).toBe(true);
    }
  });

  it("rejects null, unknown ids and inherited object keys", () => {
    expect(isEntryCategoryFilter(null)).toBe(false);
    expect(isEntryCategoryFilter("")).toBe(false);
    expect(isEntryCategoryFilter("insulin")).toBe(false);
    expect(isEntryCategoryFilter("toString")).toBe(false);
  });
});

describe("filterEntryRecords search", () => {
  it("matches a BG check by its glucose type", () => {
    expect(idsFor("all", "finger")).toEqual(["bg-1"]);
  });

  it("matches notes by text and by event type", () => {
    expect(idsFor("all", "evening walk")).toEqual(["note-1"]);
    expect(idsFor("all", "announcement")).toEqual(["note-2"]);
  });

  it("matches device events by event type and by notes", () => {
    expect(idsFor("all", "site change")).toEqual(["dev-1"]);
    expect(idsFor("all", "reservoir")).toEqual(["dev-2"]);
  });

  it("matches basal injections by insulin name and notes", () => {
    expect(idsFor("all", "longacting-a")).toEqual(["basal-1"]);
    expect(idsFor("all", "thigh")).toEqual(["basal-1"]);
  });

  it("matches on the category name, the bolus type and the source app", () => {
    expect(idsFor("all", "device events")).toEqual(["dev-2", "dev-1"]);
    expect(idsFor("all", "square")).toEqual(["bolus-1"]);
    expect(idsFor("all", "meal-logger")).toEqual(["carbs-1"]);
  });

  it("applies the category before the search", () => {
    expect(idsFor("note", "reservoir")).toEqual([]);
    expect(idsFor("deviceEvent", "pump-x")).toEqual(["dev-2"]);
  });

  it("ignores surrounding whitespace and keeps everything for a blank search", () => {
    expect(idsFor("all", "  FINGER  ")).toEqual(["bg-1"]);
    expect(idsFor("all", "   ")).toHaveLength(records.length);
  });
});
