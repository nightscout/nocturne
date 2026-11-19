import { describe, it, expect } from "vitest";
import { ProfileAttribute } from "#lib/api/index.js";
import {
  attributeLabel,
  attributeUnit,
  attributeValue,
  fractionOf,
  isSwitch,
  profileLabel,
  spanLength,
  versionLabel,
} from "./timeline-format";

const hour = 3_600_000;

describe("timeline format", () => {
  it("labels each schedule", () => {
    expect(
      [
        ProfileAttribute.Basal,
        ProfileAttribute.Sensitivity,
        ProfileAttribute.CarbRatio,
        ProfileAttribute.TargetRange,
        undefined,
      ].map(attributeLabel)
    ).toEqual(["Basal", "Sensitivity", "Carb ratio", "Target", ""]);
  });

  it("gives basal a unit and a carb ratio none, because its format carries one", () => {
    expect(attributeUnit(ProfileAttribute.Basal)).toBe("U/h");
    expect(attributeUnit(ProfileAttribute.CarbRatio)).toBe("");
    expect(attributeUnit(ProfileAttribute.Sensitivity)).toMatch(/\/U$/);
    expect(attributeUnit(ProfileAttribute.TargetRange)).not.toBe("");
  });

  it("writes a missing value as a dash and a target as one value or a range", () => {
    expect(attributeValue(ProfileAttribute.Basal, null)).toBe("–");
    expect(attributeValue(ProfileAttribute.Basal, 0.8)).toBe("0.80");
    expect(attributeValue(ProfileAttribute.TargetRange, 100, 100)).not.toContain("–");
    expect(attributeValue(ProfileAttribute.TargetRange, 90, 120)).toContain("–");
    expect(attributeValue(ProfileAttribute.Sensitivity, 40)).not.toBe("");
    expect(attributeValue(ProfileAttribute.CarbRatio, 10)).toContain("10");
    expect(attributeValue(undefined, 3)).toBe("3");
  });

  it("adds the percentage and time shift only when they change the schedules", () => {
    expect(profileLabel("Day", 100, 0)).toBe("Day");
    expect(profileLabel("Day", 120, 2)).toBe("Day 120% +2 h");
    expect(profileLabel(undefined, null, -1)).toBe("Default -1 h");
    expect(versionLabel({ profileName: "Night", percentage: 80 })).toBe("Night 80%");
  });

  it("reads a change with an activation as a switch", () => {
    expect(isSwitch({ activation: {} })).toBe(true);
    expect(isSwitch({})).toBe(false);
  });

  it("writes a stretch in hours up to two days, then in days", () => {
    expect(spanLength(0, 10 * 60_000)).toBe("< 1 h");
    expect(spanLength(0, 5 * hour)).toBe("5 h");
    expect(spanLength(0, 72 * hour)).toBe("3 d");
  });

  it("keeps a fraction inside the window", () => {
    expect(fractionOf(50, 0, 100)).toBe(0.5);
    expect(fractionOf(-5, 0, 100)).toBe(0);
    expect(fractionOf(500, 0, 100)).toBe(1);
  });
});
