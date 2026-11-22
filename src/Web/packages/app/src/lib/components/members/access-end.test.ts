import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  accessEndDate,
  accessEndDay,
  accessEndInstant,
  earliestAccessEndDay,
  hasOfferedAccessEndDay,
  isAccessEndChoiceValid,
  isAccessEndDayOffered,
  latestAccessEndDay,
} from "./access-end.js";

const NEW_YORK = "America/New_York";
const SYDNEY = "Australia/Sydney";

describe("access end day conversion", () => {
  it("ends access at the start of the day after the picked day", () => {
    expect(accessEndInstant("2026-07-14", NEW_YORK)).toBe("2026-07-15T04:00:00.000Z");
    expect(accessEndInstant("2026-07-14", "UTC")).toBe("2026-07-15T00:00:00.000Z");
    expect(accessEndInstant("2026-07-14", SYDNEY)).toBe("2026-07-14T14:00:00.000Z");
  });

  it("reads an end back as the day access still holds", () => {
    expect(accessEndDay("2026-07-15T04:00:00.000Z", NEW_YORK)).toBe("2026-07-14");
    expect(accessEndDay("2026-07-14T14:00:00.000Z", SYDNEY)).toBe("2026-07-14");
    expect(accessEndDay("2026-07-14T23:59:59.999Z", "UTC")).toBe("2026-07-14");
  });

  it("round trips every day around the spring and autumn clock changes in both hemispheres", () => {
    const days = [
      ["2026-03-07", NEW_YORK],
      ["2026-03-08", NEW_YORK],
      ["2026-03-09", NEW_YORK],
      ["2026-10-31", NEW_YORK],
      ["2026-11-01", NEW_YORK],
      ["2026-11-02", NEW_YORK],
      ["2026-04-04", SYDNEY],
      ["2026-04-05", SYDNEY],
      ["2026-04-06", SYDNEY],
      ["2026-10-03", SYDNEY],
      ["2026-10-04", SYDNEY],
      ["2026-10-05", SYDNEY],
    ] as const;
    for (const [day, zone] of days) {
      expect(accessEndDay(accessEndInstant(day, zone), zone), `${day} ${zone}`).toBe(day);
    }
  });

  it("ends a 23-hour day and a 25-hour day at the next local midnight", () => {
    expect(accessEndInstant("2026-03-08", NEW_YORK)).toBe("2026-03-09T04:00:00.000Z");
    expect(accessEndInstant("2026-03-07", NEW_YORK)).toBe("2026-03-08T05:00:00.000Z");
    expect(accessEndInstant("2026-11-01", NEW_YORK)).toBe("2026-11-02T05:00:00.000Z");
    expect(accessEndInstant("2026-10-31", NEW_YORK)).toBe("2026-11-01T04:00:00.000Z");
  });

  it("shows an end set from a mid-day instant as the day it falls in", () => {
    expect(accessEndDay("2026-07-14T09:30:00.000Z", NEW_YORK)).toBe("2026-07-14");
  });

  it("gives local midnight of the day shown for the date formatters", () => {
    const date = accessEndDate(accessEndInstant("2026-03-08", "UTC"), "UTC");
    expect(date.toISOString()).toBe("2026-03-08T00:00:00.000Z");
  });
});

describe("access end bounds", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-07-14T20:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("offers today as the earliest day, in the viewer's zone", () => {
    expect(earliestAccessEndDay(Date.now(), NEW_YORK)).toBe("2026-07-14");
    expect(earliestAccessEndDay(Date.now(), SYDNEY)).toBe("2026-07-15");
  });

  it("has no latest day when the caller's access does not end", () => {
    expect(latestAccessEndDay(undefined, NEW_YORK)).toBeUndefined();
    expect(isAccessEndDayOffered("2031-01-01", { now: Date.now(), ceiling: undefined, timeZone: NEW_YORK })).toBe(true);
  });

  it("makes the caller's own last day the latest day", () => {
    const ceiling = accessEndInstant("2026-08-01", NEW_YORK);
    expect(latestAccessEndDay(ceiling, NEW_YORK)).toBe("2026-08-01");
    expect(isAccessEndDayOffered("2026-08-01", { now: Date.now(), ceiling: ceiling, timeZone: NEW_YORK })).toBe(true);
    expect(isAccessEndDayOffered("2026-08-02", { now: Date.now(), ceiling: ceiling, timeZone: NEW_YORK })).toBe(false);
  });

  it("never offers a day whose end passes a caller's end set mid-day", () => {
    const ceiling = "2026-08-01T15:00:00.000Z";
    expect(latestAccessEndDay(ceiling, NEW_YORK)).toBe("2026-07-31");
    expect(Date.parse(accessEndInstant("2026-07-31", NEW_YORK))).toBeLessThanOrEqual(Date.parse(ceiling));
  });

  it("refuses a day before today", () => {
    expect(isAccessEndDayOffered("2026-07-13", { now: Date.now(), ceiling: undefined, timeZone: NEW_YORK })).toBe(false);
    expect(isAccessEndDayOffered("2026-07-14", { now: Date.now(), ceiling: undefined, timeZone: NEW_YORK })).toBe(true);
  });

  it("offers no day on the last day of a caller whose end is mid-day, and says an empty value is valid", () => {
    const ceiling = "2026-07-14T23:30:00.000Z";
    const bounds = { now: Date.now(), ceiling, timeZone: "UTC" };
    expect(hasOfferedAccessEndDay(bounds)).toBe(false);
    expect(isAccessEndChoiceValid("", bounds)).toBe(true);
    expect(isAccessEndChoiceValid("2026-07-14", bounds)).toBe(false);
  });

  it("offers today on the caller's last day when the caller's end is at midnight", () => {
    const bounds = { now: Date.now(), ceiling: accessEndInstant("2026-07-14", NEW_YORK), timeZone: NEW_YORK };
    expect(hasOfferedAccessEndDay(bounds)).toBe(true);
  });

  it("does not allow an empty value for an end the caller must keep", () => {
    const bounds = { now: Date.now(), ceiling: accessEndInstant("2026-08-01", NEW_YORK), timeZone: NEW_YORK };
    expect(isAccessEndChoiceValid("", bounds, true)).toBe(false);
    expect(isAccessEndChoiceValid("", bounds, false)).toBe(true);
    expect(isAccessEndChoiceValid("", { now: Date.now(), timeZone: NEW_YORK }, true)).toBe(true);
  });

  it("follows a clock that has moved past midnight", () => {
    const before = Date.parse("2026-07-14T20:00:00Z");
    const after = Date.parse("2026-07-15T20:00:00Z");
    expect(isAccessEndDayOffered("2026-07-14", { now: before, timeZone: NEW_YORK })).toBe(true);
    expect(isAccessEndDayOffered("2026-07-14", { now: after, timeZone: NEW_YORK })).toBe(false);
  });
});
