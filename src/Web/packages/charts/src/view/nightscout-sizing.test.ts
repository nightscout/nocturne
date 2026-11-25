import { describe, it, expect } from "vitest";
import {
  bubbleScale,
  focusRangeAdjustment,
  forecastDotRadius,
  formatDose,
  nightscoutTicks,
  sgvDotRadius,
  treatmentBubble,
} from "./nightscout-sizing";

const HOUR = 60 * 60 * 1000;

describe("focusRangeAdjustment", () => {
  it("is 1 up to a three-hour focus range", () => {
    expect(focusRangeAdjustment(2 * HOUR)).toBe(1);
    expect(focusRangeAdjustment(3 * HOUR)).toBe(1);
  });

  it("grows by an eighth for each further three hours", () => {
    expect(focusRangeAdjustment(6 * HOUR)).toBe(1.125);
    expect(focusRangeAdjustment(27 * HOUR)).toBe(2);
  });
});

describe("dot radii", () => {
  it("draws readings at 4, 3 or 2 pixels by chart width", () => {
    expect(sgvDotRadius(1200, 3 * HOUR)).toBe(4);
    expect(sgvDotRadius(600, 3 * HOUR)).toBe(3);
    expect(sgvDotRadius(400, 3 * HOUR)).toBe(2);
  });

  it("shrinks the dots over a longer focus range", () => {
    expect(sgvDotRadius(1200, 27 * HOUR)).toBe(2);
  });

  it("draws forecast dots one pixel smaller, at most 3", () => {
    expect(forecastDotRadius(1200, 3 * HOUR)).toBe(3);
    expect(forecastDotRadius(600, 3 * HOUR)).toBe(2);
    expect(forecastDotRadius(400, 3 * HOUR)).toBe(1);
  });
});

describe("bubbleScale", () => {
  it("divides bubbles by 2, 3 or 4 by chart width", () => {
    expect(bubbleScale(1200, 3 * HOUR)).toBe(2);
    expect(bubbleScale(600, 3 * HOUR)).toBe(3);
    expect(bubbleScale(400, 3 * HOUR)).toBe(4);
  });

  it("scales with the focus range", () => {
    expect(bubbleScale(1200, 27 * HOUR)).toBe(4);
  });
});

describe("treatmentBubble", () => {
  it("sizes the pie by the carbs when the entry has any", () => {
    const bubble = treatmentBubble(16, 2, 10, 2, 10);
    // R1 = sqrt(16) / 2 = 2, drawn at five times R1.
    expect(bubble.radius).toBe(10);
    // The label arc runs from 5*R1 to 5*(R1 + 8/scale).
    expect(bubble.labelOffset).toBe((10 + 30) / 2);
  });

  it("sizes an insulin-only entry by the insulin times the carb ratio", () => {
    expect(treatmentBubble(0, 1.6, 10, 2, 10).radius).toBe(10);
  });

  it("sizes an entry with neither by the carb ratio", () => {
    expect(treatmentBubble(0, 0, 16, 2, 10).radius).toBe(10);
  });

  it("sets the label size from the number of entries", () => {
    expect(treatmentBubble(16, 0, 10, 2, 10).labelSize(5)).toBe(22.5);
    expect(treatmentBubble(16, 0, 10, 2, 40).labelSize(5)).toBe(20);
  });

  it("draws an amount under 1 smaller", () => {
    expect(treatmentBubble(0, 0.35, 10, 2, 10).labelSize(0.35)).toBe(14);
  });
});

describe("formatDose", () => {
  it("rounds to two decimals and drops trailing zeros", () => {
    expect(formatDose(0.35)).toBe("0.35");
    expect(formatDose(3.8)).toBe("3.8");
    expect(formatDose(1.234)).toBe("1.23");
  });
});

describe("nightscoutTicks", () => {
  const limits = { veryLow: 54, low: 70, high: 180, veryHigh: 250 };

  it("puts the limits between Nightscout's fixed mg/dL ticks", () => {
    expect(nightscoutTicks("mg/dl", limits)).toEqual([40, 54, 70, 120, 180, 250, 400]);
  });

  it("uses 2, 6 and 22 mmol/L for the fixed ticks", () => {
    expect(nightscoutTicks("mmol", limits)).toEqual([36, 54, 70, 108, 180, 250, 396]);
  });

  it("leaves out a limit that is not set", () => {
    expect(nightscoutTicks("mg/dl", { ...limits, veryLow: Number.NaN })).toEqual([
      40, 70, 120, 180, 250, 400,
    ]);
  });
});
