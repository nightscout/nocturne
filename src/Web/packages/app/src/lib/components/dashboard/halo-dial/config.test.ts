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

const {
  defaultHaloDialConfig,
  HaloDialColorMode,
  HaloDialPredictionCurve,
  HaloDialCenterSubElement,
  HaloDialArcElement,
  HaloDialCornerElement,
  DEFAULT_ELEMENT_OPTIONS,
} = await import("./config");

describe("defaultHaloDialConfig", () => {
  it("matches the C# parameterless constructor shape", () => {
    const config = defaultHaloDialConfig();

    expect(config.schemaVersion).toBe(1);
    expect(config.colorMode).toBe(HaloDialColorMode.Discrete);
    expect(config.historyMinutes).toBe(15);
    expect(config.predictionMinutes).toBe(45);
    expect(config.predictionCurve).toBe(HaloDialPredictionCurve.Main);
    expect(config.centerSub).toBe(HaloDialCenterSubElement.MinutesAndDelta);
    expect(config.innerLeftArc).toBe(HaloDialArcElement.Cob);
    expect(config.innerRightArc).toBe(HaloDialArcElement.Iob);
    expect(config.iobMaxUnits).toBe(8.0);
    expect(config.cobMaxGrams).toBe(80.0);

    expect(config.corners.tl).toEqual([]);
    expect(config.corners.tr).toEqual([HaloDialCornerElement.LoopDot]);
    expect(config.corners.bl).toEqual([]);
    expect(config.corners.br).toEqual([
      HaloDialCornerElement.Direction,
      HaloDialCornerElement.Eventual,
      HaloDialCornerElement.LoopLabel,
    ]);

    expect(config.elementConfig).toEqual({});
  });
});

describe("DEFAULT_ELEMENT_OPTIONS", () => {
  it("has an entry for every corner element", () => {
    for (const value of Object.values(HaloDialCornerElement)) {
      expect(DEFAULT_ELEMENT_OPTIONS[value as HaloDialCornerElement]).toBeDefined();
      expect(DEFAULT_ELEMENT_OPTIONS[value as HaloDialCornerElement].kind).toBe(value);
    }
  });
});
