import { describe, it, expect } from "vitest";
import {
  dayCount,
  endOfDay,
  isDayString,
  isTimeZone,
  resolveDayRange,
  startOfDay,
  toDayString,
} from "./date-range";

// A zone well east of UTC and one well west, so a UTC-anchored implementation
// lands on the wrong calendar day in one direction or the other.
const SYDNEY = "Australia/Sydney";
const DENVER = "America/Denver";

describe("startOfDay / endOfDay", () => {
  it("anchors the start on local midnight, not UTC midnight", () => {
    // 2026-07-25 00:00 AEST (UTC+10) is 2026-07-24 14:00Z.
    expect(startOfDay("2026-07-25", SYDNEY).toISOString()).toBe(
      "2026-07-24T14:00:00.000Z"
    );
  });

  it("ends the range on the last millisecond of the last day", () => {
    expect(endOfDay("2026-07-25", SYDNEY).toISOString()).toBe(
      "2026-07-25T13:59:59.999Z"
    );
  });

  it("spans a whole day, so nothing on the final day is lost", () => {
    const start = startOfDay("2026-07-25", DENVER).getTime();
    const end = endOfDay("2026-07-25", DENVER).getTime();
    expect(end - start).toBe(86_400_000 - 1);
  });

  it("covers 25 hours on the day a DST fall-back lengthens", () => {
    // Denver leaves DST on 2026-11-01.
    const start = startOfDay("2026-11-01", DENVER).getTime();
    const end = endOfDay("2026-11-01", DENVER).getTime();
    expect(end - start).toBe(25 * 60 * 60 * 1000 - 1);
  });

  it("covers 23 hours on the day a DST spring-forward shortens", () => {
    // Denver enters DST on 2026-03-08.
    const start = startOfDay("2026-03-08", DENVER).getTime();
    const end = endOfDay("2026-03-08", DENVER).getTime();
    expect(end - start).toBe(23 * 60 * 60 * 1000 - 1);
  });

  it("tolerates a full ISO instant by keeping its date part", () => {
    expect(startOfDay("2026-07-25T09:31:00.000Z", SYDNEY).toISOString()).toBe(
      startOfDay("2026-07-25", SYDNEY).toISOString()
    );
  });
});

describe("dayCount", () => {
  it("counts both end days, so a full week is 7", () => {
    expect(dayCount("2026-07-19", "2026-07-25")).toBe(7);
  });

  it("counts a fortnight as 14", () => {
    expect(dayCount("2026-07-12", "2026-07-25")).toBe(14);
  });

  it("counts a single day as 1", () => {
    expect(dayCount("2026-07-25", "2026-07-25")).toBe(1);
  });

  it("is unaffected by a DST spring-forward inside the range", () => {
    expect(dayCount("2026-03-05", "2026-03-11")).toBe(7);
  });

  it("is unaffected by a DST fall-back inside the range", () => {
    expect(dayCount("2026-10-29", "2026-11-04")).toBe(7);
  });

  it("counts across a month boundary", () => {
    expect(dayCount("2026-01-28", "2026-02-03")).toBe(7);
  });

  it("counts across a leap-day February", () => {
    expect(dayCount("2028-02-01", "2028-02-29")).toBe(29);
  });

  it("counts across a year boundary", () => {
    expect(dayCount("2026-12-28", "2027-01-03")).toBe(7);
  });

  it("counts a full non-leap year", () => {
    expect(dayCount("2026-01-01", "2026-12-31")).toBe(365);
  });

  it("accepts Date instants and reads their local day", () => {
    const from = new Date(2026, 6, 19, 0, 0, 0);
    const to = new Date(2026, 6, 25, 23, 59, 59, 999);
    expect(dayCount(from, to)).toBe(7);
  });

  it("never reports fewer than one day", () => {
    expect(dayCount("2026-07-25", "2026-07-19")).toBe(1);
  });
});

describe("resolveDayRange", () => {
  it("passes explicit from/to through", () => {
    expect(resolveDayRange({ from: "2026-07-01", to: "2026-07-10" }, 14)).toEqual({
      from: "2026-07-01",
      to: "2026-07-10",
    });
  });

  it("resolves a relative window to N inclusive days ending today", () => {
    const range = resolveDayRange({ days: 7 }, 14, SYDNEY);
    expect(dayCount(range.from, range.to)).toBe(7);
  });

  it("falls back to defaultDays when nothing is set", () => {
    const range = resolveDayRange(undefined, 30, SYDNEY);
    expect(dayCount(range.from, range.to)).toBe(30);
  });

  it("ends a relative window on today in the given timezone", () => {
    // Both zones' "today" must be the calendar date each zone is actually on.
    const sydney = resolveDayRange({ days: 1 }, 14, SYDNEY);
    const denver = resolveDayRange({ days: 1 }, 14, DENVER);
    const todayIn = (timeZone: string) =>
      new Intl.DateTimeFormat("en-CA", {
        timeZone,
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
      }).format(new Date());
    expect(sydney.to).toBe(todayIn(SYDNEY));
    expect(denver.to).toBe(todayIn(DENVER));
  });

  it("ignores a half-specified explicit range", () => {
    const range = resolveDayRange({ days: 7, from: "2026-07-01" }, 14, SYDNEY);
    expect(dayCount(range.from, range.to)).toBe(7);
  });

  it("falls back to the relative window for an unresolvable day", () => {
    const range = resolveDayRange({ days: 7, from: "not-a-day", to: "2026-07-25" }, 14, SYDNEY);
    expect(dayCount(range.from, range.to)).toBe(7);
    expect(isDayString(range.from)).toBe(true);
  });

  it("falls back to UTC for an unrecognised timezone", () => {
    const range = resolveDayRange({ days: 3 }, 14, "Mars/Olympus_Mons");
    expect(dayCount(range.from, range.to)).toBe(3);
  });
});

describe("isDayString", () => {
  it("accepts a plain day", () => {
    expect(isDayString("2026-07-25")).toBe(true);
  });

  it("accepts a full ISO instant", () => {
    expect(isDayString("2026-07-25T09:31:00.000Z")).toBe(true);
  });

  it("rejects nothing, empty strings and nonsense", () => {
    expect(isDayString(null)).toBe(false);
    expect(isDayString(undefined)).toBe(false);
    expect(isDayString("")).toBe(false);
    expect(isDayString("not-a-day")).toBe(false);
    expect(isDayString("2026-13-45")).toBe(false);
  });
});

describe("isTimeZone", () => {
  it("accepts an IANA zone", () => {
    expect(isTimeZone(SYDNEY)).toBe(true);
    expect(isTimeZone("UTC")).toBe(true);
  });

  it("rejects nothing and unrecognised zones", () => {
    expect(isTimeZone(null)).toBe(false);
    expect(isTimeZone("")).toBe(false);
    expect(isTimeZone("Mars/Olympus_Mons")).toBe(false);
  });
});

describe("toDayString", () => {
  it("reads the local calendar day, not the UTC one", () => {
    // 23:30 local on the 25th is the 25th, whichever side of UTC midnight it is.
    const late = new Date(2026, 6, 25, 23, 30);
    expect(toDayString(late)).toBe("2026-07-25");
  });

  it("reads an early-morning local time as that same day", () => {
    const early = new Date(2026, 6, 25, 0, 30);
    expect(toDayString(early)).toBe("2026-07-25");
  });

  it("zero-pads month and day", () => {
    expect(toDayString(new Date(2026, 0, 5))).toBe("2026-01-05");
  });
});
