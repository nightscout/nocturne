import { describe, expect, it } from "vitest";
import { beganIn, dayInReviewSearch, hourOfSlot, hourSpan, longestFirst, shareBars } from "./loop-health-view";

describe("loop-health-view", () => {
  it("draws each measured hour as a bar, keeping a zero and skipping an unmeasured hour", () => {
    const bars = shareBars({
      hours: [
        { hour: 0, percent: 0, daily: { n: 14, sparse: false } },
        { hour: 1 },
        { hour: 23, percent: 50, daily: { n: 2, sparse: true } },
      ],
    });

    expect(bars).toEqual([
      { hour: 0, value: 0, sparse: false },
      { hour: 23, value: 50, sparse: true },
    ]);
  });

  it("places an hour over its twelve slots", () => {
    expect(hourSpan(0)).toEqual({ start: 0, end: 12 });
    expect(hourSpan(23)).toEqual({ start: 276, end: 288 });
  });

  it("lists a kind of gap longest first, and narrows it to the hour it began in", () => {
    const gaps = longestFirst({
      gaps: [
        { date: "2026-10-05", startMinuteOfDay: 190, minutes: 25 },
        { date: "2026-10-03", startMinuteOfDay: 1410, minutes: 90 },
      ],
    });

    expect(gaps.map((g) => g.minutes)).toEqual([90, 25]);
    expect(beganIn(gaps, 3).map((g) => g.minutes)).toEqual([25]);
    expect(beganIn(gaps, null)).toHaveLength(2);
  });

  it("opens Day in Review on the local day and time a gap began", () => {
    expect(dayInReviewSearch({ date: "2026-10-03", startMinuteOfDay: 1410 })).toBe("date=2026-10-03&time=23:30");
  });

  it("reads the hour a slot falls in", () => {
    expect(hourOfSlot(0)).toBe(0);
    expect(hourOfSlot(287)).toBe(23);
  });
});
