import { describe, expect, it } from "vitest";
import type { DailyProfileRange, DailyProfileSlot } from "#lib/api/index.js";
import { aggregateRuns, clockTime, contributingDays, covers, signedTwoPlaces } from "./daily-profile-view";
import { bitmapOf } from "./daily-profile.test-fixtures";

describe("coverage bitmaps", () => {
  it("reads slot i from bit i % 8 of byte i / 8, least significant first", () => {
    const map = bitmapOf([0, 9, 287]);
    expect(covers(map, 0)).toBe(true);
    expect(covers(map, 1)).toBe(false);
    expect(covers(map, 8)).toBe(false);
    expect(covers(map, 9)).toBe(true);
    expect(covers(map, 287)).toBe(true);
    expect(covers(undefined, 0)).toBe(false);
  });

  it("lists the days analysed in a slot with the series that covered it", () => {
    const range: DailyProfileRange = {
      days: [
        { date: "2026-10-01", coverage: { analysed: bitmapOf([5]), glucose: bitmapOf([5]), iobCob: bitmapOf([]) } },
        { date: "2026-10-02", coverage: { analysed: bitmapOf([6]), glucose: bitmapOf([5]) } },
      ],
    };
    const days = contributingDays(range, 5);
    expect(days.map((d) => d.day.date)).toEqual(["2026-10-01"]);
    expect(days[0].series).toEqual(["glucose"]);
  });

  it("falls back to the series it carries where the analysed bitmap is withheld", () => {
    const range: DailyProfileRange = {
      days: [
        { date: "2026-10-01", coverage: { glucose: bitmapOf([5]) } },
        { date: "2026-10-02", coverage: { glucose: bitmapOf([6]) } },
      ],
    };
    expect(contributingDays(range, 5).map((d) => d.day.date)).toEqual(["2026-10-01"]);
  });
});

describe("aggregate runs", () => {
  const agg = (sparse: boolean) => ({ p10: 0, p25: 0, p50: 0, p75: 0, p90: 0, mean: 0, n: 3, sparse });
  const slots: DailyProfileSlot[] = [
    { slot: 0, iob: agg(false) },
    { slot: 1, iob: agg(false) },
    { slot: 2, iob: agg(true) },
    { slot: 3 },
    { slot: 4, iob: agg(false) },
  ];

  it("breaks at a gap, so missing is never drawn as zero", () => {
    const runs = aggregateRuns(slots, (s) => s.iob);
    expect(runs.map((r) => r.points.map((p) => p.slot))).toEqual([[0, 1, 2], [2], [4]]);
  });

  it("splits where sparseness changes and joins the two runs at the boundary", () => {
    const runs = aggregateRuns(slots, (s) => s.iob);
    expect(runs.map((r) => r.sparse)).toEqual([false, true, false]);
  });

  it("keeps an all-zero slot as a value", () => {
    expect(aggregateRuns([{ slot: 0, iob: agg(false) }], (s) => s.iob)).toHaveLength(1);
  });
});

describe("day in review link time", () => {
  it("formats a slot's minute of day as HH:mm", () => {
    expect(clockTime(0)).toBe("00:00");
    expect(clockTime(8 * 60 + 35)).toBe("08:35");
    expect(clockTime(23 * 60 + 55)).toBe("23:55");
  });
});

describe("signed two places", () => {
  it("reads a value that rounds to zero as 0.00, with no sign", () => {
    expect(signedTwoPlaces(-0.004)).toBe("0.00");
    expect(signedTwoPlaces(0.004)).toBe("0.00");
  });

  it("signs a value that rounds away from zero", () => {
    expect(signedTwoPlaces(0.005)).toBe("+0.01");
    expect(signedTwoPlaces(-1.234)).toBe("-1.23");
  });
});
