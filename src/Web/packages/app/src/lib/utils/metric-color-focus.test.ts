import { describe, expect, it } from "vitest";
import {
  GLUCOSE_HEATMAP_LEGEND_STOPS,
  GLUCOSE_HEATMAP_OUTSIDE_COLOR,
  getGlucoseHeatmapFill,
} from "./chart-colors";
import {
  colorFocusGradient,
  glucoseColorFocusBand,
  glucoseColorFocusGradient,
  getFocusedGlucoseFill,
  insertSliderSteps,
  getFocusedIntensityFill,
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
    expect(resolveGlucoseColorThresholds([54, 72, 180, 250])).toEqual([
      54, 72, 180, 250,
    ]);
  });

  it.each(
    [
      null,
      [54, 72, 180],
      [54, 72, 70, 250],
      [40, 70, 180, 250],
      [54, 72, 180, 350],
      [-1, 70, 180, 250],
      [54, 72, 180, Infinity],
      [54, "70", 180, 250],
      [54, 72, 180, 170],
    ].map((value) => ({ value }))
  )("rejects invalid boundaries $value", ({ value }) =>
    expect(resolveGlucoseColorThresholds(value)).toBeNull()
  );
});

describe("glucoseColorFocusStops", () => {
  it("preserves every original color when using the default boundaries", () => {
    expect(glucoseColorFocusStops([54, 72, 180, 250])).toEqual(
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
    expect(getGlucoseHeatmapFill(100 + (48 / 108) * 100, stops)).toBe(
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
  it("gives both ends outside the range the same faint color", () => {
    const low = getFocusedIntensityFill(10, focus, cssVar);
    const high = getFocusedIntensityFill(70, focus, cssVar);

    expect(getFocusedIntensityFill(0, focus, cssVar)).toBe(low);
    expect(getFocusedIntensityFill(500, focus, cssVar)).toBe(low);
    expect(getFocusedIntensityFill(9.99, focus, cssVar)).toBe(low);
    expect(getFocusedIntensityFill(70.01, focus, cssVar)).toBe(low);
    expect(colorShare(low)).toBe(15);
    expect(colorShare(high)).toBe(100);
  });

  it("keeps the maximum itself inside the range", () => {
    expect(colorShare(getFocusedIntensityFill(70, focus, cssVar))).toBe(100);
    expect(colorShare(getFocusedIntensityFill(70.000001, focus, cssVar))).toBe(
      15
    );
  });

  it("leaves a day with no value at the faintest color", () => {
    expect(getFocusedIntensityFill(NaN, focus, cssVar)).toBe(
      getFocusedIntensityFill(10, focus, cssVar)
    );
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
  it("ramps across the focus and cuts back to the same color at both ends", () => {
    const outside = getFocusedIntensityFill(10, focus, cssVar);
    const peak = getFocusedIntensityFill(70, focus, cssVar);
    const gradient = colorFocusGradient(focus, 500, cssVar);

    expect(gradient).toMatch(/^linear-gradient\(to right,/);
    expect(gradient).toContain(`${outside} 0%, ${outside} 2%`);
    const stops = gradient
      .slice("linear-gradient(to right, ".length, -1)
      .split(", color-mix")
      .map((stop, index) => (index === 0 ? stop : `color-mix${stop}`));
    const [peakStop, cutStop] = stops.slice(2, 4);
    expect(peakStop.startsWith(peak)).toBe(true);
    expect(cutStop.startsWith(outside)).toBe(true);
    expect(peakStop.slice(peak.length)).toBe(cutStop.slice(outside.length));
    expect(gradient).toContain(`${outside} 100%)`);
  });

  it("keeps the selected maximum in the legend when observed values decrease", () => {
    const outside = getFocusedIntensityFill(10, focus, cssVar);
    const peak = getFocusedIntensityFill(70, focus, cssVar);

    expect(colorFocusGradient(focus, 20, cssVar)).toContain(
      `${peak} 100%, ${outside} 100%`
    );
  });
});

describe("glucose focus band", () => {
  const stops = GLUCOSE_HEATMAP_LEGEND_STOPS;
  const outside = GLUCOSE_HEATMAP_OUTSIDE_COLOR;

  it("spans the outermost two boundaries", () => {
    expect(glucoseColorFocusBand([60, 100, 200, 280])).toEqual([60, 280]);
  });

  it("takes its outside color from the theme, not from either end of the ramp", () => {
    expect(outside).toBe(GLUCOSE_HEATMAP_OUTSIDE_COLOR);
    expect(stops.map((stop) => stop.color)).not.toContain(outside);
  });

  it("falls back to the defaults for an unusable saved value", () => {
    expect(glucoseColorFocusBand([250, 180, 72, 54])).toEqual([54, 250]);
  });

  it("gives days on either side of the band the same color", () => {
    const thresholds = [100, 120, 160, 180] as const;
    const focused = glucoseColorFocusStops(thresholds);

    expect(getFocusedGlucoseFill(60, thresholds, focused)).toBe(outside);
    expect(getFocusedGlucoseFill(300, thresholds, focused)).toBe(outside);
    expect(getFocusedGlucoseFill(300, thresholds, focused)).toBe(
      getFocusedGlucoseFill(60, thresholds, focused)
    );
  });

  it("colors the middle of the band from the ramp", () => {
    const thresholds = [100, 120, 160, 180] as const;
    const focused = glucoseColorFocusStops(thresholds);
    const middle = getFocusedGlucoseFill(140, thresholds, focused);

    expect(middle).not.toBe(outside);
    expect(middle).toBe(getGlucoseHeatmapFill(140, focused));
  });

  it("includes both boundaries in the band", () => {
    const thresholds = [100, 120, 160, 180] as const;
    const focused = glucoseColorFocusStops(thresholds);

    expect(getFocusedGlucoseFill(100, thresholds, focused)).toBe(
      getGlucoseHeatmapFill(100, focused)
    );
    expect(getFocusedGlucoseFill(180, thresholds, focused)).toBe(
      getGlucoseHeatmapFill(180, focused)
    );
    expect(getFocusedGlucoseFill(99, thresholds, focused)).toBe(outside);
    expect(getFocusedGlucoseFill(181, thresholds, focused)).toBe(outside);
  });

  it("draws the legend as a flat block, the ramp, then the same flat block", () => {
    const thresholds = [100, 120, 160, 180] as const;
    const focused = glucoseColorFocusStops(thresholds);
    const gradient = glucoseColorFocusGradient(focused, thresholds);
    const at = (mgdl: number) => ((mgdl - 40) / (350 - 40)) * 100;

    expect(gradient).toMatch(/^linear-gradient\(to right in srgb,/);
    expect(gradient).toContain(`${outside} 0%, ${outside} ${at(100)}%`);
    expect(gradient).toContain(`${outside} ${at(180)}%, ${outside} 100%)`);
    expect(gradient).not.toContain("var(--glucose-heatmap-9)");
  });

  it("carries the ramp all the way to both boundaries", () => {
    const thresholds = [100, 120, 160, 180] as const;
    const focused = glucoseColorFocusStops(thresholds);
    const gradient = glucoseColorFocusGradient(focused, thresholds);

    for (const boundary of [thresholds[0], thresholds[3]]) {
      const position = ((boundary - 40) / (350 - 40)) * 100;
      const atBoundary = gradient
        .split(", ")
        .filter((stop) => stop.endsWith(`${position}%`));
      // One stop closes the flat block, one opens or closes the ramp: a hard cut.
      expect(atBoundary).toHaveLength(2);
      expect(
        atBoundary.filter((stop) => stop.startsWith(outside))
      ).toHaveLength(1);
      expect(gradient).toContain(
        `${getFocusedGlucoseFill(boundary, thresholds, focused)} ${position}%`
      );
    }
  });

  it("never re-blends an anchor that already sits on a boundary", () => {
    const thresholds = [54, 72, 180, 250] as const;
    const gradient = glucoseColorFocusGradient(
      glucoseColorFocusStops(thresholds),
      thresholds
    );

    expect(gradient).not.toContain("color-mix(in srgb, color-mix");
    expect(gradient).not.toContain(" 0.00%,");
  });
});

describe("insertSliderSteps", () => {
  it("inserts exact bounds without mutating or sorting the base again", () => {
    const base = Object.freeze([0, 0.1, 0.2, 1]);
    expect(insertSliderSteps(base, [0.15, 0.1, 2, 0.15])).toEqual([
      0, 0.1, 0.15, 0.2, 1, 2,
    ]);
    expect(base).toEqual([0, 0.1, 0.2, 1]);
  });
});
