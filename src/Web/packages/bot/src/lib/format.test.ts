import { describe, it, expect } from "vitest";
import { formatGlucose, timeAgo, trendArrow } from "./format.js";

describe("formatGlucose", () => {
  it("formats mg/dL as a whole number and mmol/L to one decimal", () => {
    expect(formatGlucose(100, "mg/dL")).toBe("100 mg/dL");
    expect(formatGlucose(100, "mmol/L")).toBe("5.5 mmol/L");
  });
});

describe("timeAgo", () => {
  const locale = new Intl.DateTimeFormat().resolvedOptions().locale;
  const narrow = new Intl.RelativeTimeFormat(locale, { style: "narrow", numeric: "auto" });
  const agoBy = (ms: number) => timeAgo(Date.now() - ms);

  it("words the age in the instance locale, in whole minutes", () => {
    expect(agoBy(5 * 60_000 + 30_000)).toBe(narrow.format(-5, "minute"));
    expect(agoBy(3 * 3_600_000)).toBe(narrow.format(-3, "hour"));
  });

  it("words the age in the locale it is given", () => {
    const at = Date.now() - 5 * 60_000;
    const de = new Intl.RelativeTimeFormat("de", { style: "narrow", numeric: "auto" });
    expect(timeAgo(at, "de")).toBe(de.format(-5, "minute"));
    expect(timeAgo(at, "de")).not.toBe(timeAgo(at, "en"));
  });

  it("does not report a future timestamp as elapsed", () => {
    expect(timeAgo(Date.now() + 60_000)).toBe(narrow.format(0, "second"));
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
