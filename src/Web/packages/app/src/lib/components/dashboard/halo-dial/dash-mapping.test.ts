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
vi.mock("@sveltejs/kit", () => ({ error: vi.fn(), redirect: vi.fn() }));

const { predictionDashArray, predictionLineCap } = await import("./dash-mapping");
const { PumpModeState } = await import("$lib/api");

describe("predictionDashArray", () => {
  it.each([
    [PumpModeState.Automatic, undefined],
    [PumpModeState.Boost, undefined],
    [PumpModeState.EaseOff, undefined],
    [PumpModeState.Sleep, undefined],
    [PumpModeState.Exercise, undefined],
    [PumpModeState.Limited, "3 3"],
    [PumpModeState.Manual, "1 4"],
    [PumpModeState.Suspended, "8 4"],
    [PumpModeState.Off, "8 4"],
  ])("returns %s for %s", (mode, expected) => {
    expect(predictionDashArray(mode)).toBe(expected);
  });

  it("returns undefined for null and undefined", () => {
    expect(predictionDashArray(null)).toBeUndefined();
    expect(predictionDashArray(undefined)).toBeUndefined();
  });
});

describe("predictionLineCap", () => {
  it("returns round only for Manual", () => {
    expect(predictionLineCap(PumpModeState.Manual)).toBe("round");
  });

  it("returns butt for every other mapped mode", () => {
    expect(predictionLineCap(PumpModeState.Automatic)).toBe("butt");
    expect(predictionLineCap(PumpModeState.Limited)).toBe("butt");
    expect(predictionLineCap(PumpModeState.Suspended)).toBe("butt");
  });

  it("returns butt for null and undefined", () => {
    expect(predictionLineCap(null)).toBe("butt");
    expect(predictionLineCap(undefined)).toBe("butt");
  });
});
