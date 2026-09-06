import { describe, it, expect, vi } from "vitest";

vi.mock("$app/environment", () => ({ browser: false, dev: false }));
vi.mock("$app/navigation", () => ({}));
vi.mock("$app/state", () => ({}));
vi.mock("$app/server", () => ({
  getRequestEvent: vi.fn(),
  query: (fn: unknown) => fn,
  command: (fn: unknown) => fn,
  form: (fn: unknown) => fn,
}));
vi.mock("@sveltejs/kit", () => ({
  error: vi.fn(),
  redirect: vi.fn(),
}));

const {
  getGlucoseColorContinuous,
  getGlucoseColorByMode,
  getGlucoseHeatmapFill,
  GLUCOSE_HEATMAP_LEGEND_STOPS,
} = await import("./chart-colors");

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
    // 85 mg/dL sits between the 70 and 100 anchors: stops 3 and 4.
    expect(getGlucoseHeatmapFill(85)).toBe(
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
