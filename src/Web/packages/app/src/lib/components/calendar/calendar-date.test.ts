import { afterAll, beforeAll, describe, expect, it } from "vitest";
import {
  firstDayOfWeek,
  formatCalendarDate,
  getCalendarDayNumber,
  leadingBlankDays,
  weekdayLabels,
} from "./calendar-date";

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

  it("starts the week on Sunday for US formats and Monday for European ones", () => {
    expect(firstDayOfWeek("en-US")).toBe(0);
    expect(firstDayOfWeek("en-GB")).toBe(1);
    expect(firstDayOfWeek("de-DE")).toBe(1);
    expect(firstDayOfWeek("sv-SE")).toBe(1);
  });

  it("orders weekday labels from the locale's first day of the week", () => {
    expect(weekdayLabels("en-US")[0]).toBe("Sun");
    expect(weekdayLabels("en-GB")[0]).toBe("Mon");
    expect(weekdayLabels("en-GB")[6]).toBe("Sun");
  });

  it("offsets the first of the month by the locale's week start", () => {
    // 1 June 2026 is a Monday.
    expect(leadingBlankDays(2026, 5, "en-US")).toBe(1);
    expect(leadingBlankDays(2026, 5, "en-GB")).toBe(0);
    // 1 November 2026 is a Sunday.
    expect(leadingBlankDays(2026, 10, "en-US")).toBe(0);
    expect(leadingBlankDays(2026, 10, "en-GB")).toBe(6);
  });
});
