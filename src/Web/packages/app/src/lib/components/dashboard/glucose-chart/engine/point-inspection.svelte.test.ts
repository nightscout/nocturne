import { describe, it, expect, afterEach } from "vitest";
import { untrack } from "svelte";
import { glucoseUnits, type GlucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import type { SeriesFinders } from "./chart-data-view.svelte";
import { createPointInspection, type PointInspection } from "./point-inspection.svelte";

const no_context_finders: SeriesFinders = {
  findSeriesValue: () => undefined,
  findBasalValue: () => undefined,
  findNearbyBolus: () => undefined,
  findNearbyCarbs: () => undefined,
  findNearbyDeviceEvent: () => undefined,
  findActivePumpMode: () => undefined,
  findActiveOverride: () => undefined,
  findActiveProfile: () => undefined,
  findActiveActivities: () => [],
  findActiveTempBasal: () => undefined,
  findActiveBasalDelivery: () => undefined,
  findNearbySystemEvent: () => undefined,
  isStaleBasalTime: () => false,
  findPreviousGlucose: () => undefined,
};

const series_data = { iobData: () => [], cobData: () => [], basalData: () => [] };

const cleanups: (() => void)[] = [];

afterEach(() => {
  cleanups.splice(0).forEach((cleanup) => cleanup());
});

function inspect_at_120(units?: () => GlucoseUnits | undefined): PointInspection {
  let inspection!: PointInspection;
  cleanups.push(
    $effect.root(() => {
      inspection = units
        ? createPointInspection(no_context_finders, () => [], series_data, units)
        : createPointInspection(no_context_finders, () => [], series_data);
    })
  );
  inspection.inspect(new Date("2026-01-01T12:00:00Z"), { sgv: 120, color: "green" });
  return inspection;
}

describe("createPointInspection glucose preview", () => {
  it("previews the reading in the units it is given, not the viewer's", () => {
    glucoseUnits.current = "mg/dl";
    const inspection = inspect_at_120(() => "mmol");

    const glucose_option = untrack(() => inspection.pickerOptions).find((o) => o.type === "glucose");
    expect(glucose_option?.preview).toBe("6.7 mmol/L");
  });

  it("previews the reading in the viewer's units when given none", () => {
    glucoseUnits.current = "mmol";
    const inspection = inspect_at_120();

    const glucose_option = untrack(() => inspection.pickerOptions).find((o) => o.type === "glucose");
    expect(glucose_option?.preview).toBe("6.7 mmol/L");

    glucoseUnits.current = "mg/dl";
    const mgdl_option = untrack(() => inspect_at_120().pickerOptions).find((o) => o.type === "glucose");
    expect(mgdl_option?.preview).toBe("120 mg/dL");
  });
});
