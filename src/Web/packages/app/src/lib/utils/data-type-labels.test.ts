import { describe, it, expect } from "vitest";
import { SyncDataType } from "@nocturne/api-client";
import { getDataTypeCountLabel, getDataTypeLabel } from "./data-type-labels";

const members = Object.values(SyncDataType);

describe("data type labels", () => {
  it.each(members)("%s has an in-sentence form", (key) => {
    expect(getDataTypeCountLabel(key)).not.toBe(key);
  });

  it("keeps the headings other screens show", () => {
    expect(getDataTypeLabel("CarbIntake")).toBe("Carb intake");
    expect(getDataTypeLabel("TempBasals")).toBe("Basal");
    expect(getDataTypeLabel("HeartRate")).toBe("Heart rate");
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
      expect(getDataTypeCountLabel(key)).toBe(form);
    }
  });

  it("names the legacy tables in both forms", () => {
    expect(getDataTypeLabel("Entries")).toBe("Entries (Legacy)");
    expect(getDataTypeCountLabel("Entries")).toBe("legacy entries");
    expect(getDataTypeLabel("Treatments")).toBe("Treatments (Legacy)");
    expect(getDataTypeCountLabel("Treatments")).toBe("legacy treatments");
  });

  it("falls back to an unknown key in both forms", () => {
    expect(getDataTypeLabel("Widgets")).toBe("Widgets");
    expect(getDataTypeCountLabel("Widgets")).toBe("Widgets");
  });
});
