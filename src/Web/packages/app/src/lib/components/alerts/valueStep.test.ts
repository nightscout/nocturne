import { afterEach, describe, expect, it } from "vitest";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { valueStep } from "./valueStep";

describe("valueStep", () => {
  afterEach(() => {
    glucoseUnits.current = "mg/dl";
  });

  it("steps a rate by 0.1 mg/dL and 0.01 mmol/L", () => {
    expect(valueStep("rate_of_change", "mg/dl")).toBe(0.1);
    expect(valueStep("rate_of_change", "mmol")).toBe(0.01);
  });

  it("steps a glucose threshold by 1 mg/dL and 0.1 mmol/L", () => {
    for (const kind of ["threshold", "predicted", "eventual_glucose"]) {
      expect(valueStep(kind, "mg/dl")).toBe(1);
      expect(valueStep(kind, "mmol")).toBe(0.1);
    }
  });

  it("reads the display unit when none is given", () => {
    glucoseUnits.current = "mmol";
    expect(valueStep("rate_of_change")).toBe(0.01);
    expect(valueStep("threshold")).toBe(0.1);
  });

  it("steps a kind without a unit by its own amount", () => {
    expect(valueStep("carbs_required", "mmol")).toBe(1);
    expect(valueStep("sensitivity_ratio", "mmol")).toBe(0.01);
    expect(valueStep("transmitter_battery", "mmol")).toBe(1);
    expect(valueStep("iob", "mmol")).toBe(0.1);
  });
});
