import { describe, expect, it } from "vitest";
import { getGlucoseHeatmapFill } from "./chart-colors";
import {
  addColorBoundary,
  insertSliderSteps,
  intensityScale,
  metricRamp,
  paintRamp,
  rampColor,
  rampPosition,
  rampValue,
  resolveColorBoundaries,
  resolveColorFocusRange,
  scaleFill,
  scaleGradient,
  glucoseScale as buildGlucoseScale,
  GLUCOSE_COLOR_MAX,
  GLUCOSE_COLOR_MIN,
  GLUCOSE_RAMP,
  MAX_COLOR_BOUNDARIES,
  type MetricScale,
} from "./metric-color-focus";

const glucoseScale = (overrides: Partial<MetricScale> = {}): MetricScale => ({
  ramp: GLUCOSE_RAMP,
  ends: {},
  domain: [GLUCOSE_COLOR_MIN, GLUCOSE_COLOR_MAX],
  axis: [GLUCOSE_COLOR_MIN, GLUCOSE_COLOR_MAX],
  window: [GLUCOSE_COLOR_MIN, GLUCOSE_COLOR_MAX],
  boundaries: [],
  dim: 90,
  ...overrides,
});

const doseScale = (overrides: Partial<MetricScale> = {}): MetricScale => ({
  ramp: metricRamp("--insulin"),
  ends: {},
  domain: [10, 70],
  axis: [0, 500],
  window: [10, 70],
  boundaries: [],
  dim: 90,
  ...overrides,
});

describe("resolveColorFocusRange", () => {
  it("accepts increasing bounds within the limits", () => {
    expect(resolveColorFocusRange([10.25, 70.75])).toEqual([10.25, 70.75]);
    expect(resolveColorFocusRange([0, 100], [0, 100])).toEqual([0, 100]);
  });

  it.each([[[]], [[1]], [[70, 10]], [[10, 10]], [[-1, 10]], [[0, 101]], [[NaN, 1]], ["10,70"]])(
    "rejects %j",
    (candidate) => expect(resolveColorFocusRange(candidate, [0, 100])).toBeNull()
  );
});

describe("resolveColorBoundaries", () => {
  it("accepts boundaries increasing in value and position strictly inside the limits", () => {
    const boundaries = [
      { value: 100, position: 0.2 },
      { value: 200, position: 0.8 },
    ];
    expect(resolveColorBoundaries(boundaries, [40, 350])).toEqual(boundaries);
  });

  it.each([
    [[{ value: 40, position: 0.5 }]],
    [[{ value: 100, position: 0 }]],
    [[{ value: 100, position: 1 }]],
    [[{ value: 100, position: 0.5 }, { value: 90, position: 0.6 }]],
    [[{ value: 100, position: 0.5 }, { value: 110, position: 0.4 }]],
    [[{ value: "100", position: 0.5 }]],
    [[null]],
  ])("rejects %j", (candidate) => expect(resolveColorBoundaries(candidate, [40, 350])).toBeNull());

  it("caps how many boundaries a scale carries", () => {
    const many = Array.from({ length: MAX_COLOR_BOUNDARIES + 1 }, (_, i) => ({
      value: 50 + i,
      position: (i + 1) / 100,
    }));
    expect(resolveColorBoundaries(many, [40, 350])).toBeNull();
  });
});

describe("ramp positions", () => {
  const pins = [{ value: 20, position: 0.75 }];

  it("is linear across the domain without boundaries and clamps outside it", () => {
    expect(rampPosition(40, [10, 70])).toBe(0.5);
    expect(rampPosition(0, [10, 70])).toBe(0);
    expect(rampPosition(500, [10, 70])).toBe(1);
    expect(rampPosition(NaN, [10, 70])).toBe(0);
  });

  it("warps around a boundary and inverts back to the same value", () => {
    expect(rampPosition(15, [10, 70], pins)).toBeCloseTo(0.375);
    expect(rampPosition(45, [10, 70], pins)).toBeCloseTo(0.875);
    for (const value of [12, 20, 33, 69]) {
      expect(rampValue(rampPosition(value, [10, 70], pins), [10, 70], pins)).toBeCloseTo(value);
    }
  });

  it("ignores a boundary the domain no longer reaches", () => {
    expect(rampPosition(40, [30, 50], pins)).toBe(0.5);
  });
});

describe("rampColor", () => {
  it("draws the unwarped glucose ramp exactly as the heatmap fill", () => {
    for (const mgdl of [40, 54, 63, 120, 180, 300, 350]) {
      expect(rampColor(GLUCOSE_RAMP, rampPosition(mgdl, [GLUCOSE_COLOR_MIN, GLUCOSE_COLOR_MAX]))).toBe(
        getGlucoseHeatmapFill(mgdl)
      );
    }
  });

  it("blends a single-hue ramp from faint to the metric color", () => {
    const ramp = metricRamp("--carbs");
    expect(rampColor(ramp, 0)).toBe("color-mix(in srgb, var(--carbs) 15%, transparent)");
    expect(rampColor(ramp, 1)).toBe("var(--carbs)");
    expect(rampColor(ramp, 0.25)).toBe(
      "color-mix(in srgb, color-mix(in srgb, var(--carbs) 15%, transparent) 75.00%, var(--carbs))"
    );
  });
});

describe("scaleFill", () => {
  it("dims values outside the window but not past an end that reaches the axis", () => {
    const scale = glucoseScale({ window: [70, GLUCOSE_COLOR_MAX] });
    expect(scaleFill(120, scale)).toBe(getGlucoseHeatmapFill(120));
    expect(scaleFill(60, scale)).toBe(`color-mix(in srgb, ${getGlucoseHeatmapFill(60)} 10%, transparent)`);
    expect(scaleFill(400, scale)).toBe(getGlucoseHeatmapFill(400));
  });

  it("saturates a dose outlier at the window's top color", () => {
    const scale = doseScale();
    expect(scaleFill(500, scale)).toBe("color-mix(in srgb, var(--insulin) 10%, transparent)");
    expect(scaleFill(70, scale)).toBe("var(--insulin)");
  });
});

describe("scaleGradient", () => {
  it("stops at the axis ends, the domain ends and every boundary", () => {
    const gradient = scaleGradient(doseScale({ boundaries: [{ value: 20, position: 0.75 }] }));
    expect(gradient).toMatch(/^linear-gradient\(to right in srgb, /);
    for (const at of ["0.000%", "2.000%", "4.000%", "14.000%", "100.000%"]) {
      expect(gradient).toContain(at);
    }
  });
});

describe("addColorBoundary", () => {
  it("pins the color already drawn at the middle of the widest gap", () => {
    const scale = glucoseScale({ window: [70, 180] });
    const [added] = addColorBoundary(scale, Math.round)!;
    expect(added.value).toBe(125);
    expect(added.position).toBeCloseTo(rampPosition(125, scale.domain));
    expect(rampColor(GLUCOSE_RAMP, added.position)).toBe(scaleFill(125, scale));
  });

  it("splits the widest remaining gap next", () => {
    const scale = doseScale({ boundaries: [{ value: 20, position: 0.5 }] });
    expect(addColorBoundary(scale)!.map((b) => b.value)).toEqual([20, 45]);
  });

  it("refuses past the cap", () => {
    const boundaries = Array.from({ length: MAX_COLOR_BOUNDARIES }, (_, i) => ({
      value: 100 + i,
      position: (i + 1) / 100,
    }));
    expect(addColorBoundary(glucoseScale({ boundaries }))).toBeNull();
  });
});

describe("insertSliderSteps", () => {
  it("inserts exact bounds without mutating or sorting the base again", () => {
    const base = [0, 1, 2, 3];
    expect(insertSliderSteps(base, [1.5, 2, 3.25])).toEqual([0, 1, 1.5, 2, 3, 3.25]);
    expect(base).toEqual([0, 1, 2, 3]);
  });
});

describe("chosen colors", () => {
  it("accepts a hex color on a boundary and refuses any other", () => {
    expect(resolveColorBoundaries([{ value: 100, position: 0.5, color: "#ff0000" }], [40, 350])).toEqual([
      { value: 100, position: 0.5, color: "#ff0000" },
    ]);
    expect(resolveColorBoundaries([{ value: 100, position: 0.5, color: null }], [40, 350])).toEqual([
      { value: 100, position: 0.5 },
    ]);
    expect(resolveColorBoundaries([{ value: 100, position: 0.5, color: "red" }], [40, 350])).toBeNull();
  });

  it("paints the ends and each colored boundary the domain reaches", () => {
    const ramp = paintRamp(
      metricRamp("--insulin"),
      { low: "#ffffff", high: "#000000" },
      [
        { value: 20, position: 0.25, color: "#ff0000" },
        { value: 90, position: 0.9, color: "#00ff00" },
      ],
      [10, 70]
    );
    expect(ramp).toEqual([
      { position: 0, color: "#ffffff" },
      { position: 0.25, color: "#ff0000" },
      { position: 1, color: "#000000" },
    ]);
  });

  it("leaves the theme ramp untouched without chosen colors", () => {
    expect(paintRamp(GLUCOSE_RAMP, {}, [{ value: 100, position: 0.3 }], [40, 350])).toBe(GLUCOSE_RAMP);
  });

  it("mirrors a dose scale when its end colors are swapped", () => {
    const scale = intensityScale("--insulin", [0, Infinity], 50, null, null, 90, {
      low: "#000000",
      high: "#ffffff",
    });
    expect(scaleFill(0, scale)).toBe("#000000");
    expect(scaleFill(50, scale)).toBe("#ffffff");
  });

  it("drops an end color that is not hex but keeps the other", () => {
    expect(buildGlucoseScale(null, null, 90, { low: "blue", high: "#123456" }).ends).toEqual({
      high: "#123456",
    });
  });

  it("adds a boundary that holds the chosen color already drawn", () => {
    const scale = intensityScale("--insulin", [0, Infinity], 40, null, null, 90, {
      low: "#000000",
      high: "#ffffff",
    });
    const [added] = addColorBoundary(scale)!;
    expect(added.color).toBeUndefined();
    expect(rampColor(scale.ramp, added.position)).toBe(scaleFill(added.value, scale));
  });
});
