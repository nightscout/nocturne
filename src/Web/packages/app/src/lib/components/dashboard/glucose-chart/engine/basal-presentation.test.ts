import { describe, it, expect } from "vitest";
import { BasalDeliveryOrigin } from "$lib/api";
import { isBasalAdjusted } from "./basal-presentation";

describe("isBasalAdjusted", () => {
  it("reports a rate that departs from a known schedule", () => {
    expect(
      isBasalAdjusted(BasalDeliveryOrigin.Algorithm, 0.65, 0.9)
    ).toBe(true);
  });

  it("does not report a rate that matches its schedule", () => {
    expect(isBasalAdjusted(BasalDeliveryOrigin.Manual, 0.9, 0.9)).toBe(false);
  });

  // With no therapy profile the server sends a null scheduledRate. Comparing a
  // number against null is always unequal, which would paint every algorithm
  // basal as a deviation and render a blank "Scheduled" row beside it.
  it("does not treat an unknown schedule as a deviation", () => {
    expect(isBasalAdjusted(BasalDeliveryOrigin.Algorithm, 0.65, null)).toBe(
      false
    );
    expect(
      isBasalAdjusted(BasalDeliveryOrigin.Manual, 0.65, undefined)
    ).toBe(false);
  });

  it("only applies to algorithm and manual origins", () => {
    for (const origin of [
      BasalDeliveryOrigin.Scheduled,
      BasalDeliveryOrigin.Suspended,
      BasalDeliveryOrigin.Inferred,
    ]) {
      expect(isBasalAdjusted(origin, 0.65, 0.9), origin).toBe(false);
    }
  });
});
