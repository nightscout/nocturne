import { describe, it, expect, vi } from "vitest";

// chart-colors reaches $lib/api only for the ChartColor type; stub it so this
// suite doesn't need the generated API client.
vi.mock("$lib/api", () => ({}));

import {
  FALLBACK_GLUCOSE_THRESHOLDS,
  resolveGlucoseThresholds,
  toStatusThresholds,
} from "./glucose-thresholds";
import { getGlucoseColor } from "$lib/utils/chart-colors";
import { getGlucoseStatus } from "@nocturne/ui/glucose-icon";

describe("FALLBACK_GLUCOSE_THRESHOLDS", () => {
  it("matches the boundaries the time-in-range reports use", () => {
    expect(FALLBACK_GLUCOSE_THRESHOLDS).toEqual({
      veryLow: 54,
      low: 70,
      high: 180,
      veryHigh: 250,
    });
  });

  it("does not colour hypoglycaemia as in-range", () => {
    // 55-69 mg/dL is hypoglycaemia; a `low` of 55 painted this band green.
    for (const mgdl of [55, 60, 65, 69]) {
      expect(getGlucoseColor(mgdl, FALLBACK_GLUCOSE_THRESHOLDS)).toBe(
        "var(--glucose-low)"
      );
    }
    expect(getGlucoseColor(70, FALLBACK_GLUCOSE_THRESHOLDS)).toBe(
      "var(--glucose-in-range)"
    );
  });
});

describe("resolveGlucoseThresholds", () => {
  it("prefers the supplied values", () => {
    expect(
      resolveGlucoseThresholds({
        veryLow: 50,
        low: 80,
        high: 160,
        veryHigh: 240,
      })
    ).toEqual({ veryLow: 50, low: 80, high: 160, veryHigh: 240 });
  });

  it("fills omitted values from the fallback", () => {
    expect(resolveGlucoseThresholds({ high: 160 })).toEqual({
      ...FALLBACK_GLUCOSE_THRESHOLDS,
      high: 160,
    });
  });

  it("treats a supplied 0 as absent", () => {
    // The API sends 0 for a tenant with no profile yet.
    expect(
      resolveGlucoseThresholds({ veryLow: 0, low: 0, high: 0, veryHigh: 0 })
    ).toEqual(FALLBACK_GLUCOSE_THRESHOLDS);
  });

  it("falls back entirely for null or undefined", () => {
    expect(resolveGlucoseThresholds(null)).toEqual(FALLBACK_GLUCOSE_THRESHOLDS);
    expect(resolveGlucoseThresholds(undefined)).toEqual(
      FALLBACK_GLUCOSE_THRESHOLDS
    );
  });
});

describe("toStatusThresholds", () => {
  it("remaps onto the status-bucket field names", () => {
    expect(toStatusThresholds(FALLBACK_GLUCOSE_THRESHOLDS)).toEqual({
      low: 54,
      targetBottom: 70,
      targetTop: 180,
      high: 250,
    });
  });

  it("buckets the same way getGlucoseColor does", () => {
    const status = toStatusThresholds(FALLBACK_GLUCOSE_THRESHOLDS);
    const cases: [number, string][] = [
      [40, "very-low"],
      [60, "low"],
      [120, "in-range"],
      [200, "high"],
      [300, "very-high"],
    ];
    for (const [mgdl, expected] of cases) {
      expect(getGlucoseStatus(mgdl, status)).toBe(expected);
      expect(getGlucoseColor(mgdl, FALLBACK_GLUCOSE_THRESHOLDS)).toBe(
        `var(--glucose-${expected})`
      );
    }
  });
});
