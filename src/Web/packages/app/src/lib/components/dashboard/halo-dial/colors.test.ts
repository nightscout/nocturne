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

const { bgColor, bgColorContinuous, bgColorDiscrete } = await import("./colors");
const { HaloDialColorMode } = await import("$lib/api");

describe("bgColorDiscrete", () => {
  it("returns very-low CSS var below the very-low threshold", () => {
    expect(bgColorDiscrete(35)).toBe("var(--glucose-very-low)");
  });

  it("returns low CSS var between very-low and low thresholds", () => {
    expect(bgColorDiscrete(50)).toBe("var(--glucose-low)");
  });

  it("returns in-range CSS var inside the target band", () => {
    expect(bgColorDiscrete(120)).toBe("var(--glucose-in-range)");
  });

  it("returns high CSS var above target but below very-high", () => {
    expect(bgColorDiscrete(200)).toBe("var(--glucose-high)");
  });

  it("returns very-high CSS var above the very-high threshold", () => {
    expect(bgColorDiscrete(300)).toBe("var(--glucose-very-high)");
  });
});

describe("bgColorContinuous", () => {
  it("returns the anchor's own oklch at the stop value", () => {
    // Stop [40, 25, 0.22, 0.58] → "oklch(0.580 0.220 25.00)"
    expect(bgColorContinuous(40)).toBe("oklch(0.580 0.220 25.00)");
  });

  it("clamps below the lowest stop to the lowest stop's color", () => {
    expect(bgColorContinuous(10)).toBe(bgColorContinuous(40));
  });

  it("clamps above the highest stop to the highest stop's color", () => {
    expect(bgColorContinuous(500)).toBe(bgColorContinuous(320));
  });

  it("interpolates between two stops at the midpoint", () => {
    // Halfway between stops [70,85,0.18,0.78] and [90,150,0.16,0.74] → mgdl 80
    // Hue 117.5, chroma 0.17, lightness 0.76
    expect(bgColorContinuous(80)).toBe("oklch(0.760 0.170 117.50)");
  });
});

describe("bgColor dispatch", () => {
  it("delegates to discrete resolver in Discrete mode", () => {
    expect(bgColor(120, HaloDialColorMode.Discrete)).toBe(bgColorDiscrete(120));
  });

  it("delegates to continuous resolver in Continuous mode", () => {
    expect(bgColor(120, HaloDialColorMode.Continuous)).toBe(bgColorContinuous(120));
  });
});
