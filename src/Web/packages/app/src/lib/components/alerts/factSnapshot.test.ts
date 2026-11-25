import { afterEach, describe, expect, it } from "vitest";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { leafFactBinding } from "./factSnapshot";
import { summarizeCondition } from "./summarizeCondition.svelte";
import type { ConditionNode } from "./types";

const rateLeaf = (rate: number): ConditionNode => ({
  type: "rate_of_change",
  rate_of_change: { direction: "falling", rate },
});

describe("leafFactBinding rate_of_change", () => {
  afterEach(() => {
    glucoseUnits.current = "mg/dl";
  });

  it.each(["mg/dl", "mmol"] as const)(
    "shows 1.5 mg/dL per minute as the rule summary does in %s",
    (units) => {
      glucoseUnits.current = units;
      const shown = leafFactBinding(rateLeaf(1.5))!.format(1.5);
      expect(summarizeCondition(rateLeaf(1.5))).toBe(
        `Glucose falling at least ${shown}`
      );
    }
  );

  it("shows the rate in the display unit", () => {
    const format = leafFactBinding(rateLeaf(1.5))!.format;
    glucoseUnits.current = "mg/dl";
    expect(format(1.5)).toBe("1.5 mg/dL per minute");
    glucoseUnits.current = "mmol";
    expect(format(1.5)).toBe("0.08 mmol/L per minute");
  });
});
