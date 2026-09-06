import { describe, expect, it } from "vitest";
import {
  GLUCOSE_HEATMAP_LEGEND_STOPS,
  getGlucoseHeatmapFill,
} from "./chart-colors";
import {
  colorFocusGradient,
  getFocusedIntensityFill,
  parseColorFocusPreferences,
  resolveColorFocusRange,
  resolveGlucoseColorThresholds,
  glucoseColorFocusStops,
  type ColorFocusRange,
} from "./metric-color-focus";

const cssVar = "--chart-bolus";
const focus = [10, 70] as const satisfies ColorFocusRange;

function colorShare(fill: string): number {
  const match = fill.match(/var\(--chart-bolus\) ([\d.]+)%/);
  expect(match).not.toBeNull();
  return Number(match![1]);
}

describe("resolveColorFocusRange", () => {
  it("accepts increasing nonnegative bounds including decimals", () => {
    expect(resolveColorFocusRange([0, 100])).toEqual([0, 100]);
    expect(resolveColorFocusRange([10.5, 70.5])).toEqual([10.5, 70.5]);
  });

  it.each(
    [
      null,
      undefined,
      "10,70",
      { min: 10, max: 70 },
      [],
      [10],
      [10, 70, 100],
      ["10", 70],
      [10, "70"],
      [-1, 70],
      [10, 10],
      [70, 10],
      [NaN, 70],
      [10, Infinity],
      [-Infinity, 70],
    ].map((candidate) => ({ candidate }))
  )("rejects invalid range $candidate", ({ candidate }) => {
    expect(resolveColorFocusRange(candidate)).toBeNull();
  });
});

describe("resolveGlucoseColorThresholds", () => {
  it("accepts four strictly increasing glucose boundaries", () => {
    expect(resolveGlucoseColorThresholds([54, 70, 180, 250])).toEqual([
      54, 70, 180, 250,
    ]);
  });

  it.each(
    [
      null,
      [54, 70, 180],
      [54, 70, 70, 250],
      [40, 70, 180, 250],
      [54, 70, 180, 350],
      [-1, 70, 180, 250],
      [54, 70, 180, Infinity],
      [54, "70", 180, 250],
      [54, 70, 180, 170],
    ].map((value) => ({ value }))
  )("rejects invalid boundaries $value", ({ value }) =>
    expect(resolveGlucoseColorThresholds(value)).toBeNull()
  );
});

describe("glucoseColorFocusStops", () => {
  it("preserves every original color when using the default boundaries", () => {
    expect(glucoseColorFocusStops([54, 70, 180, 250])).toEqual(
      GLUCOSE_HEATMAP_LEGEND_STOPS
    );
  });

  it("moves palette anchors continuously and uses those same stops for cells", () => {
    const stops = glucoseColorFocusStops([60, 100, 200, 280]);
    expect(stops.find((stop) => stop.mgdl === 100)?.color).toBe(
      "var(--glucose-heatmap-3)"
    );
    expect(stops.find((stop) => stop.mgdl === 200)?.color).toBe(
      "var(--glucose-heatmap-6)"
    );
    expect(stops.some((stop) => stop.mgdl === 280)).toBe(true);
    expect(
      stops.every(
        (stop, index) => index === 0 || stop.mgdl > stops[index - 1].mgdl
      )
    ).toBe(true);
    expect(getGlucoseHeatmapFill(100 + (50 / 110) * 100, stops)).toBe(
      getGlucoseHeatmapFill(120)
    );
    expect(getGlucoseHeatmapFill(-1, stops)).toBe("var(--glucose-heatmap-1)");
    expect(getGlucoseHeatmapFill(500, stops)).toBe("var(--glucose-heatmap-9)");
    expect(getGlucoseHeatmapFill(120, stops)).not.toBe(
      getGlucoseHeatmapFill(120)
    );
  });
});

describe("getFocusedIntensityFill", () => {
  it("clamps outliers to the selected endpoint colors", () => {
    const low = getFocusedIntensityFill(10, focus, cssVar);
    const high = getFocusedIntensityFill(70, focus, cssVar);

    expect(getFocusedIntensityFill(0, focus, cssVar)).toBe(low);
    expect(getFocusedIntensityFill(500, focus, cssVar)).toBe(high);
    expect(colorShare(low)).toBe(15);
    expect(colorShare(high)).toBe(100);
  });

  it("makes 20, 40 and 60 distinguishable despite an observed outlier of 500", () => {
    const focused = [20, 40, 60].map((value) =>
      colorShare(getFocusedIntensityFill(value, focus, cssVar))
    );
    const fullDomain = [20, 40, 60].map((value) =>
      colorShare(getFocusedIntensityFill(value, [0, 500], cssVar))
    );

    expect(focused).toEqual([29, 58, 86]);
    expect(focused[2] - focused[0]).toBeGreaterThan(
      fullDomain[2] - fullDomain[0]
    );
  });

  it("preserves the default zero-to-maximum scale and theme color", () => {
    expect(getFocusedIntensityFill(0, [0, 500], cssVar)).toBe(
      "color-mix(in srgb, var(--chart-bolus) 15%, transparent)"
    );
    expect(colorShare(getFocusedIntensityFill(250, [0, 500], cssVar))).toBe(58);
    expect(colorShare(getFocusedIntensityFill(500, [0, 500], cssVar))).toBe(
      100
    );
  });
});

describe("colorFocusGradient", () => {
  it("uses the cell endpoint colors with flat ends outside the selected focus", () => {
    const low = getFocusedIntensityFill(0, focus, cssVar);
    const high = getFocusedIntensityFill(500, focus, cssVar);
    const gradient = colorFocusGradient(focus, 500, cssVar);

    expect(gradient).toContain(`${low} 0%, ${low} 2%`);
    const highStop = gradient
      .slice(gradient.indexOf(high) + high.length)
      .match(/^ ([\d.]+)%/);
    expect(Number(highStop?.[1])).toBeCloseTo(14);
    expect(gradient).toContain(`${high} 100%)`);
    expect(gradient).toMatch(/^linear-gradient\(to right,/);
  });

  it("keeps the selected maximum in the legend when observed values decrease", () => {
    const high = getFocusedIntensityFill(70, focus, cssVar);

    expect(colorFocusGradient(focus, 20, cssVar)).toContain(
      `${high} 100%, ${high} 100%`
    );
  });
});

describe("parseColorFocusPreferences", () => {
  it("restores independent ranges for supported metrics", () => {
    const preferences = {
      avgGlucose: [54, 70, 180, 250],
      tir: [70, 100],
      bolus: [10, 70],
      basal: [0, 20],
      tdd: [10, 100],
      carbs: [0, 500],
    };

    expect(parseColorFocusPreferences(JSON.stringify(preferences))).toEqual(
      preferences
    );
  });

  it.each([null, "", "{", "null", "false", "42", '"text"', "[]"])(
    "ignores missing or malformed storage %j",
    (raw) => {
      expect(parseColorFocusPreferences(raw)).toEqual({});
    }
  );

  it("discards invalid and unknown entries without losing valid preferences", () => {
    expect(
      parseColorFocusPreferences(
        JSON.stringify({
          tir: [70, 101],
          bolus: [10, 70],
          basal: [-1, 20],
          tdd: [100, 10],
          carbs: ["0", 500],
          avgGlucose: [70, 180],
          unknown: [0, 100],
        })
      )
    ).toEqual({ bolus: [10, 70] });
  });
});
