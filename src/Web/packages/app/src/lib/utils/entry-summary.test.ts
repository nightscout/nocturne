import { describe, it, expect, vi } from "vitest";

import type {
  BGCheck,
  BasalInjection,
  Bolus,
  BolusType,
  CarbIntake,
  DeviceEvent,
  DeviceEventType,
  GlucoseType,
  Note,
} from "$lib/api";
import type { EntryRecord } from "$lib/constants/entry-categories";

// `entry-summary.ts` reaches `formatting.ts`, which imports the appearance store
// and the Svelte runtime behind it. Stub the store so Vite never resolves it;
// none of the amount formatters read a preference.
vi.mock("$app/environment", () => ({ browser: false }));
vi.mock("mode-watcher", () => ({}));
vi.mock("$lib/stores/appearance-store.svelte", () => ({
  glucoseUnits: { current: "mg/dl" },
  timeFormat: { current: "12" },
  regionFormat: { current: "" },
  preferredLanguage: { current: "en" },
}));

// Dynamic import so the mocks are established first
const { entryDetails, entryLabel, entrySummary } =
  await import("./entry-summary");

const bolus = (data: Bolus): EntryRecord => ({ kind: "bolus", data });
const carbs = (data: CarbIntake): EntryRecord => ({ kind: "carbs", data });
const bgCheck = (data: BGCheck): EntryRecord => ({ kind: "bgCheck", data });
const note = (data: Note): EntryRecord => ({ kind: "note", data });
const deviceEvent = (data: DeviceEvent): EntryRecord => ({
  kind: "deviceEvent",
  data,
});
const basalInjection = (data: BasalInjection): EntryRecord => ({
  kind: "basalInjection",
  data,
});

// String enums from the generated client, spelled out rather than imported by
// value, so the `$lib/api` import stays type-only and this suite runs before the
// NSwag client has been generated.
const enums = {
  normalBolus: "Normal",
  fingerStick: "Finger",
  siteChange: "SiteChange",
} as unknown as {
  normalBolus: BolusType;
  fingerStick: GlucoseType;
  siteChange: DeviceEventType;
};

// Computed rather than written as literals: both artifacts are longer than a
// double round-trips, which `no-loss-of-precision` rejects in source.
const INEXACT_BOLUS = 0.1 + 0.2;
const INEXACT_CARBS = 45 + 0.1 + 0.2 - 0.3;

describe("entry-summary fixtures", () => {
  it("holds the exact doubles a stored total can carry", () => {
    expect(INEXACT_BOLUS.toString()).toBe("0.30000000000000004");
    expect(INEXACT_CARBS.toString()).toBe("45.00000000000001");
  });
});

describe("entryLabel", () => {
  it("prints a binary-inexact bolus at the insulin convention's two places", () => {
    expect(entryLabel(bolus({ insulin: INEXACT_BOLUS }))).toBe("0.30u insulin");
  });

  it("keeps the finest real pump increment distinguishable", () => {
    expect(entryLabel(bolus({ insulin: 0.05 }))).toBe("0.05u insulin");
    expect(entryLabel(bolus({ insulin: 0.1 }))).toBe("0.10u insulin");
    expect(entryLabel(bolus({ insulin: 1.15 }))).toBe("1.15u insulin");
  });

  it("rounds, rather than truncates, below the second place", () => {
    expect(entryLabel(bolus({ insulin: 12.345 }))).toBe("12.35u insulin");
    expect(entryLabel(bolus({ insulin: 12.344 }))).toBe("12.34u insulin");
  });

  it("names the category when a bolus carries no amount", () => {
    expect(entryLabel(bolus({}))).toBe("Bolus");
    expect(entryLabel(bolus({ insulin: 0 }))).toBe("Bolus");
  });

  it("prints carbs as whole grams", () => {
    expect(entryLabel(carbs({ carbs: INEXACT_CARBS }))).toBe("45g carbs");
    expect(entryLabel(carbs({ carbs: 30 }))).toBe("30g carbs");
    expect(entryLabel(carbs({}))).toBe("Carbs");
  });

  it("prints a long-acting injection at the same insulin precision", () => {
    expect(entryLabel(basalInjection({ units: INEXACT_BOLUS }))).toBe(
      "0.30u basal"
    );
    expect(entryLabel(basalInjection({ units: 24 }))).toBe("24.00u basal");
    expect(entryLabel(basalInjection({}))).toBe("Long-acting injection");
  });

  it("passes glucose, note and device text through untouched", () => {
    expect(entryLabel(bgCheck({ mgdl: 112 }))).toBe("112 mg/dL");
    expect(entryLabel(note({ text: "felt low" }))).toBe("felt low");
    expect(entryLabel(deviceEvent({ eventType: enums.siteChange }))).toBe(
      "SiteChange"
    );
  });
});

describe("entryDetails", () => {
  it("gives the qualifier behind the amount", () => {
    expect(entryDetails(bolus({ bolusType: enums.normalBolus }))).toBe(
      "Normal"
    );
    expect(entryDetails(bgCheck({ glucoseType: enums.fingerStick }))).toBe(
      "Finger"
    );
    expect(entryDetails(note({ isAnnouncement: true }))).toBe("Announcement");
    expect(entryDetails(deviceEvent({ notes: "reservoir low" }))).toBe(
      "reservoir low"
    );
    expect(
      entryDetails(
        basalInjection({ insulinContext: { insulinName: "Tresiba" } })
      )
    ).toBe("Tresiba");
  });

  it("is empty where a record has no qualifier", () => {
    expect(entryDetails(bolus({}))).toBe("");
    expect(entryDetails(carbs({ carbs: 30 }))).toBe("");
    expect(entryDetails(note({}))).toBe("");
    expect(entryDetails(basalInjection({}))).toBe("");
  });
});

describe("entrySummary", () => {
  it("prints a binary-inexact bolus at two places", () => {
    expect(entrySummary(bolus({ insulin: INEXACT_BOLUS }))).toBe("0.30U");
    expect(
      entrySummary(
        bolus({ insulin: INEXACT_BOLUS, bolusType: enums.normalBolus })
      )
    ).toBe("0.30U · Normal");
  });

  it("prints carbs as whole grams", () => {
    expect(entrySummary(carbs({ carbs: INEXACT_CARBS }))).toBe("45g carbs");
  });

  it("prints a long-acting injection's units", () => {
    expect(entrySummary(basalInjection({ units: 24 }))).toBe("24.00U");
  });

  it("truncates a long note to a preview", () => {
    expect(entrySummary(note({ text: "a".repeat(80) }))).toBe("a".repeat(50));
  });

  it("falls back to the category name when nothing is quantified", () => {
    expect(entrySummary(bolus({}))).toBe("Insulin");
    expect(entrySummary(carbs({}))).toBe("Carbs");
    expect(entrySummary(deviceEvent({}))).toBe("Device Events");
    expect(entrySummary(basalInjection({}))).toBe("Long-acting injection");
  });
});
