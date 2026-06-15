import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { formatCalendarDate, getCalendarDayNumber } from "./calendar-date";

describe("calendar date helpers", () => {
  const originalTimezone = process.env.TZ;

  beforeAll(() => {
    process.env.TZ = "America/New_York";
  });

  afterAll(() => {
    if (originalTimezone === undefined) {
      delete process.env.TZ;
    } else {
      process.env.TZ = originalTimezone;
    }
  });

  it("reads the day number from the date string without UTC shifting", () => {
    expect(getCalendarDayNumber("2026-06-01")).toBe(1);
    expect(getCalendarDayNumber("2026-06-14")).toBe(14);
  });

  it("formats the weekday from the local calendar date", () => {
    expect(formatCalendarDate("2026-06-14", "en-US", { weekday: "long" })).toBe(
      "Sunday"
    );
  });
});
