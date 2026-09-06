import { describe, it, expect } from "vitest";
import { formatGlucose, timeAgo, trendArrow } from "./format.js";

describe("formatGlucose", () => {
  it("formats mg/dL as a whole number and mmol/L to one decimal", () => {
    expect(formatGlucose(100, "mg/dL")).toBe("100 mg/dL");
    expect(formatGlucose(100, "mmol/L")).toBe("5.5 mmol/L");
  });
});

describe("timeAgo", () => {
  const agoBy = (ms: number) => timeAgo(Date.now() - ms);

  it("says just now under half a minute, singular at one minute", () => {
    expect(agoBy(0)).toBe("just now");
    expect(agoBy(29_000)).toBe("just now");
    expect(agoBy(31_000)).toBe("1 min ago");
    expect(agoBy(60_000)).toBe("1 min ago");
    expect(agoBy(89_000)).toBe("1 min ago");
  });

  it("counts whole minutes above that", () => {
    expect(agoBy(91_000)).toBe("2 min ago");
    expect(agoBy(12 * 60_000)).toBe("12 min ago");
  });

  it("does not report a future timestamp as elapsed", () => {
    expect(timeAgo(Date.now() + 60_000)).toBe("just now");
  });
});

describe("trendArrow", () => {
  it("maps drawable directions to plain-text arrows", () => {
    expect(trendArrow("Flat")).toBe("->");
    expect(trendArrow("SingleUp")).toBe("^");
    expect(trendArrow("SingleDown")).toBe("v");
    expect(trendArrow("DoubleUp")).toBe("^^");
    expect(trendArrow("DoubleDown")).toBe("vv");
    expect(trendArrow("FortyFiveUp")).toBe("/");
    expect(trendArrow("FortyFiveDown")).toBe("\\");
    expect(trendArrow("TripleUp")).toBe("^^^");
    expect(trendArrow("TripleDown")).toBe("vvv");
  });

  it("marks an unknown trend rather than claiming it is stable", () => {
    expect(trendArrow("None")).toBe("?");
    expect(trendArrow("NotComputable")).toBe("?");
    expect(trendArrow("NOT COMPUTABLE")).toBe("?");
    expect(trendArrow("RATE OUT OF RANGE")).toBe("?");
  });

  it("never echoes the raw direction back", () => {
    for (const direction of ["None", "NotComputable", "RateOutOfRange", "Wat"]) {
      expect(trendArrow(direction)).not.toContain(direction);
    }
  });
});
