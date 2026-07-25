import { describe, it, expect } from "vitest";
import { buildWeekdayBuckets } from "./week-to-week.utils";

const identity = (mgdl: number) => mgdl;
const ANCHOR = new Date(2026, 6, 25);

/** 2026-07-20 is a Monday. */
function monday(hour: number, minute: number, mgdl: number) {
  return { mills: new Date(2026, 6, 20, hour, minute).getTime(), mgdl };
}

describe("buildWeekdayBuckets", () => {
  it("keys a reading by its weekday", () => {
    const rows = buildWeekdayBuckets([monday(8, 0, 120)], identity, ANCHOR);
    expect(rows).toHaveLength(1);
    expect(rows[0].mon).toBe(120);
  });

  it("means two readings in the same cell", () => {
    const rows = buildWeekdayBuckets(
      [monday(8, 0, 100), monday(8, 1, 200)],
      identity,
      ANCHOR
    );
    expect(rows[0].mon).toBe(150);
  });

  it("means three readings in the same cell rather than halving the earlier ones", () => {
    // The running-half formula gave 60/4 + 120/4 + 240/2 = 165 for these.
    const rows = buildWeekdayBuckets(
      [monday(8, 0, 60), monday(8, 1, 120), monday(8, 2, 240)],
      identity,
      ANCHOR
    );
    expect(rows[0].mon).toBe(140);
  });

  it("means four readings in the same cell", () => {
    const rows = buildWeekdayBuckets(
      [monday(8, 0, 100), monday(8, 0, 100), monday(8, 1, 100), monday(8, 1, 300)],
      identity,
      ANCHOR
    );
    expect(rows[0].mon).toBe(150);
  });

  it("means same-weekday readings from different weeks into one cell", () => {
    const mondayA = { mills: new Date(2026, 6, 13, 8, 0).getTime(), mgdl: 100 };
    const mondayB = { mills: new Date(2026, 6, 20, 8, 0).getTime(), mgdl: 200 };
    const rows = buildWeekdayBuckets([mondayA, mondayB], identity, ANCHOR);
    expect(rows).toHaveLength(1);
    expect(rows[0].mon).toBe(150);
  });

  it("keeps weekdays in separate series within one cell", () => {
    const tuesday = { mills: new Date(2026, 6, 21, 8, 0).getTime(), mgdl: 200 };
    const rows = buildWeekdayBuckets([monday(8, 0, 100), tuesday], identity, ANCHOR);
    expect(rows).toHaveLength(1);
    expect(rows[0].mon).toBe(100);
    expect(rows[0].tue).toBe(200);
  });

  it("rounds times into 5-minute cells", () => {
    const rows = buildWeekdayBuckets(
      [monday(8, 1, 100), monday(8, 2, 200), monday(8, 8, 300)],
      identity,
      ANCHOR
    );
    // 8:01 and 8:02 both round to 8:00; 8:08 rounds to 8:10.
    expect(rows).toHaveLength(2);
    expect(rows[0].mon).toBe(150);
    expect(rows[1].mon).toBe(300);
    expect(rows.map((r) => r.time.getMinutes())).toEqual([0, 10]);
  });

  it("sorts cells by time of day", () => {
    const rows = buildWeekdayBuckets(
      [monday(20, 0, 100), monday(6, 0, 100)],
      identity,
      ANCHOR
    );
    expect(rows.map((r) => r.time.getHours())).toEqual([6, 20]);
  });

  it("anchors every cell on the anchor's calendar day", () => {
    const rows = buildWeekdayBuckets([monday(8, 0, 100)], identity, ANCHOR);
    expect(rows[0].time.getFullYear()).toBe(2026);
    expect(rows[0].time.getMonth()).toBe(6);
    expect(rows[0].time.getDate()).toBe(25);
    expect(rows[0].time.getHours()).toBe(8);
  });

  it("applies the unit conversion before averaging", () => {
    const toMmol = (mgdl: number) => mgdl / 18;
    const rows = buildWeekdayBuckets(
      [monday(8, 0, 90), monday(8, 0, 180)],
      toMmol,
      ANCHOR
    );
    expect(rows[0].mon).toBeCloseTo(7.5, 5);
  });

  it("returns no rows for no readings", () => {
    expect(buildWeekdayBuckets([], identity, ANCHOR)).toEqual([]);
  });
});
