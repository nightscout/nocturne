import { describe, it, expect, vi } from "vitest";

// chart-colors reaches $lib/api only for the ChartColor type; stub it so this
// suite doesn't need the generated API client.
vi.mock("$lib/api", () => ({}));

import {
  FALLBACK_GLUCOSE_THRESHOLDS,
  FALLBACK_GLUCOSE_Y_MAX,
  resolveChartThresholds,
} from "./glucose-thresholds";
import { getGlucoseColor } from "$lib/utils/chart-colors";
import { transformChartData } from "$lib/utils/chart-data-transform";

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

const FALLBACK_CHART_THRESHOLDS = {
  ...FALLBACK_GLUCOSE_THRESHOLDS,
  glucoseYMax: FALLBACK_GLUCOSE_Y_MAX,
  targetLow: null,
  targetHigh: null,
};

describe("resolveChartThresholds", () => {
  it("prefers the supplied values", () => {
    expect(
      resolveChartThresholds({
        veryLow: 50,
        low: 80,
        high: 160,
        veryHigh: 240,
        glucoseYMax: 370,
        targetLow: 90,
        targetHigh: 150,
      })
    ).toEqual({
      veryLow: 50,
      low: 80,
      high: 160,
      veryHigh: 240,
      glucoseYMax: 370,
      targetLow: 90,
      targetHigh: 150,
    });
  });

  it("fills omitted values from the fallback", () => {
    expect(resolveChartThresholds({ high: 160 })).toEqual({
      ...FALLBACK_CHART_THRESHOLDS,
      high: 160,
    });
  });

  it("treats a supplied 0 cut-point as absent", () => {
    // The API sends 0 for a tenant with no profile yet.
    expect(
      resolveChartThresholds({ veryLow: 0, low: 0, high: 0, veryHigh: 0 })
    ).toEqual(FALLBACK_CHART_THRESHOLDS);
  });

  it("keeps a supplied 0 axis ceiling and targets", () => {
    expect(
      resolveChartThresholds({ glucoseYMax: 0, targetLow: 0, targetHigh: 0 })
    ).toMatchObject({ glucoseYMax: 0, targetLow: 0, targetHigh: 0 });
  });

  it("falls back entirely for null or undefined", () => {
    expect(resolveChartThresholds(null)).toEqual(FALLBACK_CHART_THRESHOLDS);
    expect(resolveChartThresholds(undefined)).toEqual(
      FALLBACK_CHART_THRESHOLDS
    );
  });

  it("is what the dashboard chart carries", () => {
    const supplied = { high: 160, glucoseYMax: 0, targetLow: 90 };
    expect(transformChartData({ thresholds: supplied }).thresholds).toEqual(
      resolveChartThresholds(supplied)
    );
  });
});
