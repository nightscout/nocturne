import { describe, it, expect } from "vitest";
import {
  getGlucoseColorContinuous,
  getGlucoseColorByMode,
  getGlucoseHeatmapFill,
  getGlucoseRangeBlend,
  glucoseRangeStops,
  distinctRangeStops,
  GLUCOSE_HEATMAP_LEGEND_STOPS,
} from "./colors";

describe("getGlucoseColorContinuous", () => {
  it("returns an oklch() string", () => {
    const c = getGlucoseColorContinuous(120);
    expect(c).toMatch(/^oklch\(/);
  });

  it("clamps below the lowest anchor", () => {
    expect(getGlucoseColorContinuous(20)).toBe(getGlucoseColorContinuous(40));
    expect(getGlucoseColorContinuous(20)).not.toBe(getGlucoseColorContinuous(120));
  });

  it("clamps above the highest anchor", () => {
    expect(getGlucoseColorContinuous(500)).toBe(getGlucoseColorContinuous(320));
    expect(getGlucoseColorContinuous(500)).not.toBe(getGlucoseColorContinuous(120));
  });

  it("interpolates between anchors", () => {
    const at70 = getGlucoseColorContinuous(70);
    const at90 = getGlucoseColorContinuous(90);
    const at80 = getGlucoseColorContinuous(80);
    expect(at80).not.toBe(at70);
    expect(at80).not.toBe(at90);
  });
});

describe("getGlucoseHeatmapFill", () => {
  const first = GLUCOSE_HEATMAP_LEGEND_STOPS[0];
  const last =
    GLUCOSE_HEATMAP_LEGEND_STOPS[GLUCOSE_HEATMAP_LEGEND_STOPS.length - 1];

  it("references only theme variables, never a literal colour", () => {
    for (let mgdl = 20; mgdl <= 500; mgdl += 7) {
      expect(getGlucoseHeatmapFill(mgdl)).toMatch(
        /^(var\(--glucose-heatmap-\d\)|color-mix\(in srgb, var\(--glucose-heatmap-\d\) [\d.]+%, var\(--glucose-heatmap-\d\)\))$/
      );
    }
  });

  it("returns an anchor's own colour at that anchor", () => {
    for (const stop of GLUCOSE_HEATMAP_LEGEND_STOPS) {
      // Interior anchors mix at 0% of the stop below, which resolves to the anchor itself.
      expect(getGlucoseHeatmapFill(stop.mgdl)).toContain(stop.color);
    }
  });

  it("clamps outside the anchors", () => {
    expect(getGlucoseHeatmapFill(0)).toBe(first.color);
    expect(getGlucoseHeatmapFill(first.mgdl - 1)).toBe(first.color);
    expect(getGlucoseHeatmapFill(last.mgdl + 1)).toBe(last.color);
    expect(getGlucoseHeatmapFill(9999)).toBe(last.color);
  });

  it("weights the lower stop less the closer the value sits to the upper one", () => {
    const share = (fill: string) => Number(fill.match(/ ([\d.]+)%/)![1]);
    expect(share(getGlucoseHeatmapFill(75))).toBeGreaterThan(
      share(getGlucoseHeatmapFill(95))
    );
  });

  it("mixes the pair bracketing the value", () => {
    expect(getGlucoseHeatmapFill(86)).toBe(
      "color-mix(in srgb, var(--glucose-heatmap-3) 50.00%, var(--glucose-heatmap-4))"
    );
  });

});

describe("getGlucoseColorByMode", () => {
  const thresholds = { veryLow: 55, low: 70, high: 180, veryHigh: 250 };

  it("returns var() reference in threshold mode", () => {
    expect(getGlucoseColorByMode(120, "threshold", thresholds)).toMatch(/^var\(--glucose-/);
  });

  it("returns oklch() in continuous mode", () => {
    expect(getGlucoseColorByMode(120, "continuous", thresholds)).toMatch(/^oklch\(/);
  });
});

describe("the distinct range palette's continuous colour", () => {
  const thresholds = { veryLow: 55, low: 70, high: 180, veryHigh: 250 };

  it("applies only to the clarity palette", () => {
    expect(distinctRangeStops("theme", thresholds)).toBeUndefined();
    expect(distinctRangeStops("clarity", thresholds)).toEqual(glucoseRangeStops(thresholds));
  });

  const stops = glucoseRangeStops(thresholds);

  it("is an anchor's own variable exactly at its threshold", () => {
    expect(getGlucoseRangeBlend(70, stops)).toBe("var(--glucose-low)");
    expect(getGlucoseRangeBlend(180, stops)).toBe("var(--glucose-high)");
  });

  it("clamps to the end colours outside the anchors", () => {
    expect(getGlucoseRangeBlend(20, stops)).toBe("var(--glucose-very-low)");
    expect(getGlucoseRangeBlend(400, stops)).toBe("var(--glucose-very-high)");
  });

  it("blends the neighbouring colours in oklch between anchors", () => {
    expect(getGlucoseRangeBlend(62.5, stops)).toBe(
      "color-mix(in oklch, var(--glucose-very-low) 50.00%, var(--glucose-low))"
    );
  });

  it("is chosen by getGlucoseColorContinuous only when given a range", () => {
    expect(getGlucoseColorContinuous(120, stops)).toBe(getGlucoseRangeBlend(120, stops));
    expect(getGlucoseColorContinuous(120)).toMatch(/^oklch\(/);
  });

  it("places in-range at the middle of the target range", () => {
    expect(glucoseRangeStops(thresholds).find((s) => s.color === "var(--glucose-in-range)")?.mgdl).toBe(125);
  });

  it("sorts thresholds given out of order by value", () => {
    const mgdl = glucoseRangeStops({ veryLow: 80, low: 70, high: 180, veryHigh: 100 }).map((s) => s.mgdl);
    expect(mgdl).toEqual([...mgdl].sort((a, b) => a - b));
  });

  it("leaves the heatmap blend in sRGB", () => {
    expect(getGlucoseHeatmapFill(47)).toMatch(/^color-mix\(in srgb, /);
  });
});
