import { describe, it, expect } from "vitest";
import { SyncDataType } from "@nocturne/api-client";
import { dataTypeCount, dataTypePhrase, dataTypeLabel } from "./data-type-labels.svelte";

const members = Object.values(SyncDataType);

describe("data type labels", () => {
  it.each(members)("%s has an in-sentence form", (key) => {
    expect(dataTypePhrase(key)).not.toBe(key);
  });

  it("keeps the headings other screens show", () => {
    expect(dataTypeLabel("CarbIntake")).toBe("Carb intake");
    expect(dataTypeLabel("TempBasals")).toBe("Basal");
    expect(dataTypeLabel("HeartRate")).toBe("Heart rate");
  });

  it("keeps the in-sentence wording the danger zone shows", () => {
    const wording: Record<string, string> = {
      Glucose: "glucose readings",
      ManualBG: "manual BG readings",
      Calibrations: "calibrations",
      Boluses: "boluses",
      CarbIntake: "carb intakes",
      BGChecks: "BG checks",
      BolusCalculations: "bolus calculations",
      Notes: "notes",
      DeviceEvents: "device events",
      StateSpans: "state spans",
      DeviceStatus: "device status records",
      TempBasals: "temp basals",
      BasalInjections: "basal injections",
      HeartRate: "heart rate readings",
      Steps: "step counts",
      BodyWeight: "body weights",
      Sleep: "sleep sessions",
      Profiles: "profile records",
    };
    for (const [key, form] of Object.entries(wording)) {
      expect(dataTypePhrase(key)).toBe(form);
    }
  });

  it("names the legacy tables in both forms", () => {
    expect(dataTypeLabel("Entries")).toBe("Entries (Legacy)");
    expect(dataTypePhrase("Entries")).toBe("legacy entries");
    expect(dataTypeLabel("Treatments")).toBe("Treatments (Legacy)");
    expect(dataTypePhrase("Treatments")).toBe("legacy treatments");
  });

  it("falls back to an unknown key in both forms", () => {
    expect(dataTypeLabel("Widgets")).toBe("Widgets");
    expect(dataTypePhrase("Widgets")).toBe("Widgets");
  });

  it.each(members)("%s counts one in the singular and the rest in the plural", (key) => {
    expect(dataTypeCount(key, 12)).toBe(`12 ${dataTypePhrase(key)}`);
    expect(dataTypeCount(key, 1)).not.toBe(`1 ${dataTypePhrase(key)}`);
    expect(dataTypeCount(key, 1)).toMatch(/^1 \S/);
  });

  it("counts an unknown key with the key itself", () => {
    expect(dataTypeCount("Widgets", 2)).toBe("2 Widgets");
  });
});
