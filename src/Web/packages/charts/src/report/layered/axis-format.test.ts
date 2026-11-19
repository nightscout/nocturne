import { describe, expect, it } from "vitest";
import { formatAxisValue, formatPointerValue } from "./axis-format.js";

const format = { locale: "en-US", hour12: true, timeZone: "Australia/Sydney" };

describe("a time axis's words", () => {
  it("names the day at local midnight and the clock time elsewhere", () => {
    const midnight = new Date("2026-11-03T00:00:00+11:00");
    const morning = new Date("2026-11-03T09:30:00+11:00");

    expect(formatAxisValue("time", midnight, format)).toBe("Nov 3");
    expect(formatAxisValue("time", morning, format)).toBe("9:30 AM");
  });

  it("gives the pointer's heading the day with the clock time", () => {
    expect(formatPointerValue("time", new Date("2026-11-03T09:30:00+11:00"), format)).toBe("Nov 3, 9:30 AM");
    expect(formatPointerValue("time_of_day", 570, format)).toBe(formatAxisValue("time_of_day", 570, format));
  });
});
