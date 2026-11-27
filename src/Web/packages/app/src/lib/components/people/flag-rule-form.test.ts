import { describe, expect, it } from "vitest";
import { FlagRuleComparison, NotificationUrgency } from "@nocturne/api-client";
import { FlagRuleRequestSchema } from "@nocturne/api-client/schemas";
import { FLAG_FACTS, FLAG_PRESETS, makeRule } from "./flag-rule-fixtures.js";
import {
  DEFAULT_RULE_LEVEL,
  draftOfFact,
  draftOfPreset,
  draftOfRule,
  findFact,
  fromDisplayValue,
  requestOf,
  toDisplayValue,
} from "./flag-rule-form.js";

const [timeBelow, , mean, hoursSinceReading] = FLAG_FACTS;

describe("flag rule drafts", () => {
  it("shows a glucose value in the viewer's unit and sends it in mg/dL", () => {
    expect(toDisplayValue(180, mean, "mg/dl")).toBe(180);
    expect(toDisplayValue(180, mean, "mmol")).toBeCloseTo(10, 1);
    expect(fromDisplayValue(10, mean, "mmol")).toBeCloseTo(180, 0);
    expect(fromDisplayValue(10, mean, "mg/dl")).toBe(10);
  });

  it("leaves a value that is not glucose in its own unit", () => {
    expect(toDisplayValue(4, timeBelow, "mmol")).toBe(4);
    expect(fromDisplayValue(4, timeBelow, "mmol")).toBe(4);
  });

  it("starts a preset at warn with its range, comparison and value", () => {
    expect(draftOfPreset(FLAG_PRESETS[0], timeBelow, "mmol")).toEqual({
      dataset: "glucose_statistics",
      field: "timeBelow",
      days: 14,
      comparison: FlagRuleComparison.Above,
      value: 4,
      level: DEFAULT_RULE_LEVEL,
    });
    expect(draftOfPreset(FLAG_PRESETS[1], hoursSinceReading, "mmol").days).toBeNull();
  });

  it("starts a fact from the full list on 14 days for a period fact and none for a live fact", () => {
    expect(draftOfFact(timeBelow).days).toBe(14);
    expect(draftOfFact(hoursSinceReading).days).toBeNull();
    expect(draftOfFact(timeBelow).level).toBe(NotificationUrgency.Warn);
  });

  it("opens a saved glucose rule in the viewer's unit", () => {
    const rule = makeRule({ field: "mean", value: 180, level: NotificationUrgency.Hazard });

    const draft = draftOfRule(rule, mean, "mmol");

    expect(draft.value).toBeCloseTo(10, 1);
    expect(draft.level).toBe(NotificationUrgency.Hazard);
  });

  it("finds a fact by its dataset and field", () => {
    expect(findFact(FLAG_FACTS, "glucose_statistics", "mean")).toBe(mean);
    expect(findFact(FLAG_FACTS, "glucose_statistics", "removed")).toBeUndefined();
  });
});

describe("flag rule request bodies", () => {
  it("parses a period rule through the generated schema with its days and its value in mg/dL", () => {
    const draft = { ...draftOfFact(mean), value: 10, comparison: FlagRuleComparison.Below };

    const body = FlagRuleRequestSchema.parse(requestOf(draft, mean, "mmol"));

    expect(body.days).toBe(14);
    expect(body.value).toBeCloseTo(180, 0);
    expect(body.comparison).toBe(FlagRuleComparison.Below);
    expect(body.level).toBe(NotificationUrgency.Warn);
  });

  it("parses a live rule with no days, which the API reads as a fact as it is now", () => {
    const draft = { ...draftOfFact(hoursSinceReading), days: 7, value: 2 };

    const body = FlagRuleRequestSchema.parse(requestOf(draft, hoursSinceReading, "mg/dl"));

    expect(body.days ?? null).toBeNull();
    expect("days" in body && body.days !== undefined).toBe(false);
  });

  it("parses a body with no level, which the API reads as warn", () => {
    const body = FlagRuleRequestSchema.parse({
      dataset: "glucose_statistics",
      field: "timeBelow",
      days: 14,
      comparison: FlagRuleComparison.Above,
      value: 4,
    });

    expect(body.level ?? null).toBeNull();
  });

  it("refuses a level the generated schema does not know", () => {
    expect(
      FlagRuleRequestSchema.safeParse({ ...requestOf(draftOfFact(timeBelow), timeBelow, "mg/dl"), level: "Severe" })
        .success
    ).toBe(false);
  });
});
