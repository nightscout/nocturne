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

const { getGlucoseColorContinuous, getGlucoseColorByMode } = await import(
  "./chart-colors"
);

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

describe("getGlucoseColorByMode", () => {
  const thresholds = { veryLow: 55, low: 70, high: 180, veryHigh: 250 };

  it("returns var() reference in threshold mode", () => {
    expect(getGlucoseColorByMode(120, "threshold", thresholds)).toMatch(/^var\(--glucose-/);
  });

  it("returns oklch() in continuous mode", () => {
    expect(getGlucoseColorByMode(120, "continuous", thresholds)).toMatch(/^oklch\(/);
  });
});
