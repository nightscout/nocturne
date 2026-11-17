import { describe, expect, it } from "vitest";
import { formatPlotSize, parsePlotSize } from "./plot-size";

describe("plot size cookie", () => {
  it("reads back what it writes, rounding to whole pixels", () => {
    const value = formatPlotSize({ width: 1203.6, height: 450, compact: false });
    expect(value).toBe("1204x450x0");
    expect(parsePlotSize(value)).toEqual({ width: 1204, height: 450, compact: false });
  });

  it("reads the compact flag", () => {
    expect(parsePlotSize("390x400x1")).toEqual({ width: 390, height: 400, compact: true });
  });

  it.each([undefined, "", "abc", "1200x450", "1200x450x2", "50x450x0", "1200x5000x0", "1200x450x0; x"])(
    "refuses %s",
    (value) => {
      expect(parsePlotSize(value)).toBeNull();
    }
  );
});
