import { describe, it, expect } from "vitest";
import { AlertConditionType } from "$api-clients";
import { conditionLabel } from "./conditionLabel.svelte";
import { LEAF_FACTS } from "./factCatalog";
import { toAlertConditionType } from "./types";
import { factLabel } from "./factText.svelte";

describe("conditionLabel", () => {
  it.each([
    [AlertConditionType.Threshold, "Glucose threshold"],
    [AlertConditionType.RateOfChange, "Rate of change"],
    [AlertConditionType.SignalLoss, "Signal loss"],
    [AlertConditionType.Composite, "Combined conditions"],
    [AlertConditionType.PumpBattery, "Pump battery"],
    [undefined, "Alert"],
  ])("labels %s as %s", (type, label) => {
    expect(conditionLabel(type)).toBe(label);
  });

  it("uses the picker label for every leaf kind", () => {
    for (const fact of LEAF_FACTS) {
      expect(conditionLabel(toAlertConditionType(fact.kind)), fact.kind).toBe(
        factLabel(fact.kind)
      );
    }
  });

  it("never shows a raw condition type", () => {
    for (const type of Object.values(AlertConditionType)) {
      expect(conditionLabel(type)).not.toBe(type);
    }
  });
});
