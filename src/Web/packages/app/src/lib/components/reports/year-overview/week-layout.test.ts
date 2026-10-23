import { describe, it, expect } from "vitest";
import { timeDay } from "d3-time";

import { firstDayOfWeek } from "$lib/components/calendar/calendar-date";
import { toDayString } from "$lib/utils/date-range";
import { yearCalendarBounds } from "./year-bounds";
import { layoutYear, monthOutlinePath } from "./week-layout";

function yearLayout(year: number, firstDay: number) {
  const { start, end } = yearCalendarBounds(year);
  return layoutYear(start, end, firstDay);
}

describe("layoutYear", () => {
  it.each([
    ["en-US", "2026-01-04", "2026-01-10"],
    ["en-GB", "2026-01-05", "2026-01-11"],
    ["de-DE", "2026-01-05", "2026-01-11"],
  ])("runs the second %s column %s to %s", (locale, from, to) => {
    const column = yearLayout(2026, firstDayOfWeek(locale)).columns[1];

    expect(column.from).toBe(from);
    expect(column.to).toBe(to);
  });

  it("places Sunday in the first row of a Sunday-first week and the last of a Monday-first week", () => {
    const sunday = new Date(2026, 0, 4);
    const rowOfSunday = (firstDay: number) =>
      yearLayout(2026, firstDay).days.find((d) => d.date.getTime() === sunday.getTime())?.row;

    expect(rowOfSunday(0)).toBe(0);
    expect(rowOfSunday(1)).toBe(6);
  });

  it.each([0, 1, 2, 3, 4, 5, 6])(
    "gives every column of a year starting on day %i exactly its own seven days",
    (firstDay) => {
      const { days, columns } = yearLayout(2026, firstDay);

      for (const column of columns) {
        const inColumn = days.filter((d) => d.column === column.column);
        expect(inColumn.length).toBeGreaterThan(0);
        for (const day of inColumn) {
          const key = toDayString(day.date);
          expect(key >= column.from && key <= column.to).toBe(true);
        }
        expect(column.start.getDay()).toBe(firstDay);
        expect(toDayString(timeDay.offset(column.start, 6))).toBe(column.to);
      }
      expect(columns[0].from <= "2026-01-01").toBe(true);
      expect(columns.at(-1)!.to >= "2026-12-31").toBe(true);
    }
  );

  it.each([2024, 2025, 2026, 2027])(
    "labels %i's columns by start dates that never repeat or go backwards, for every first day",
    (year) => {
      for (const firstDay of [0, 1, 2, 3, 4, 5, 6]) {
        const starts = yearLayout(year, firstDay).columns.map((c) => c.from);

        for (let i = 1; i < starts.length; i++) {
          expect(starts[i] > starts[i - 1]).toBe(true);
        }
        expect(new Set(starts).size).toBe(starts.length);
      }
    }
  );
});

describe("monthOutlinePath", () => {
  const { start } = yearCalendarBounds(2026);
  const january = new Date(2026, 0, 1);

  it("outlines January from Thursday to Saturday on a Sunday-first grid", () => {
    expect(monthOutlinePath(january, start, 0, [1, 1])).toBe(
      "M1,4 H0 V7 H4 V7 H5 V0 H1 Z"
    );
  });

  it("outlines January from Thursday to Saturday on a Monday-first grid", () => {
    expect(monthOutlinePath(january, start, 1, [1, 1])).toBe(
      "M1,3 H0 V7 H4 V6 H5 V0 H1 Z"
    );
  });

  it("scales the outline by the cell size", () => {
    expect(monthOutlinePath(january, start, 0, [10, 20])).toBe(
      "M10,80 H0 V140 H40 V140 H50 V0 H10 Z"
    );
  });
});
