import { afterEach, describe, expect, it } from "vitest";
import { FlagRuleComparison } from "@nocturne/api-client";
import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import { makeRule, makeRuleReason } from "./flag-rule-fixtures.js";
import { ruleReasonDetails, ruleReasonPhrase, ruleSentence } from "./flag-rule-text.svelte.js";

const meanReason = makeRuleReason({
  field: "mean",
  label: "Mean glucose",
  unit: "mg/dL",
  range: undefined,
  value: 180,
  ruleValue: 160,
});

afterEach(() => {
  glucoseUnits.current = "mg/dl";
});

describe("rule reason phrases", () => {
  it("states a percent figure, its period and the rule it met", () => {
    expect(ruleReasonPhrase(makeRuleReason())).toBe(
      "Time below consensus range: 6.5% in the last 14 days (your rule: above 4%)"
    );
  });

  it("states a glucose figure and the rule's value in mg/dL", () => {
    glucoseUnits.current = "mg/dl";

    expect(ruleReasonPhrase(meanReason)).toBe("Mean glucose: 180 mg/dL in the last 14 days (your rule: above 160 mg/dL)");
  });

  it("states a glucose figure and the rule's value in mmol/L", () => {
    glucoseUnits.current = "mmol";

    expect(ruleReasonPhrase(meanReason)).toBe("Mean glucose: 10 mmol/L in the last 14 days (your rule: above 8.9 mmol/L)");
  });

  it("states a live figure with no period and a below rule", () => {
    const phrase = ruleReasonPhrase(
      makeRuleReason({
        field: "sensorHoursLeft",
        label: "Sensor hours left",
        unit: "h",
        range: undefined,
        value: 12,
        comparison: FlagRuleComparison.Below,
        ruleValue: 24,
        days: undefined,
        from: undefined,
        to: undefined,
      })
    );

    expect(phrase).toBe("Sensor hours left: 12 h (your rule: below 24 h)");
  });

  it("states a period of one day in the singular", () => {
    expect(ruleReasonPhrase(makeRuleReason({ days: 1 }))).toContain("in the last 1 day ");
  });
});

describe("rule reason details", () => {
  it("states the own range in the viewer's unit and that it varies by time of day", () => {
    const reason = makeRuleReason({ range: "own", bound: { low: 70, high: 170, variesOverDay: true } });

    glucoseUnits.current = "mg/dl";
    const [mgdl] = ruleReasonDetails(reason);
    glucoseUnits.current = "mmol";
    const [mmol] = ruleReasonDetails(reason);

    expect(mgdl).toMatch(/^Own range 70\D+170 mg\/dL, varies by time of day$/);
    expect(mmol).toMatch(/^Own range 3\.9\D+9\.4 mmol\/L, varies by time of day$/);
  });

  it("states an own range that does not vary with no more", () => {
    expect(ruleReasonDetails(makeRuleReason({ bound: { low: 70, high: 180, variesOverDay: false } }))).toEqual([
      expect.stringMatching(/^Own range 70\D+180 mg\/dL$/),
    ]);
  });

  it("states the sensor coverage of a judgement on too little data", () => {
    expect(ruleReasonDetails(makeRuleReason({ coverage: 14.3 }))).toEqual(["Sensor worn 14% of the time"]);
  });

  it("never rounds a coverage below the mark up to the mark", () => {
    expect(ruleReasonDetails(makeRuleReason({ coverage: 69.6 }))).toEqual(["Sensor worn 69% of the time"]);
  });

  it("states nothing more for a consensus figure on enough data", () => {
    expect(ruleReasonDetails(makeRuleReason())).toEqual([]);
  });
});

describe("rule sentences", () => {
  it("says a period rule and a live rule as facts", () => {
    expect(ruleSentence(makeRule(), "Time below consensus range", "%")).toBe(
      "Time below consensus range in the last 14 days is above 4%"
    );
    expect(
      ruleSentence(
        makeRule({ field: "hoursSinceReading", days: undefined, value: 2 }),
        "Hours since the last reading",
        "h"
      )
    ).toBe("Hours since the last reading is above 2 h");
  });

  it("says a glucose rule in the viewer's unit", () => {
    glucoseUnits.current = "mmol";

    expect(
      ruleSentence(makeRule({ field: "mean", comparison: FlagRuleComparison.Below, value: 72 }), "Mean glucose", "mg/dL")
    ).toBe("Mean glucose in the last 14 days is below 4 mmol/L");
  });
});
