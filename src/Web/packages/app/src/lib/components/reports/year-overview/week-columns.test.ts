import { describe, it, expect } from "vitest";
import { timeDays, timeWeek } from "d3-time";

import { yearCalendarBounds } from "./year-bounds";
import { getWeekColumns } from "./week-columns";

/** The heatmap cells layerchart lays out for `year`, keyed by week column. */
function cellsForYear(year: number) {
  const { start, end } = yearCalendarBounds(year);
  return timeDays(start, end).map((date) => ({
    x: timeWeek.count(start, date),
    data: { date },
  }));
}

describe("getWeekColumns", () => {
  it("labels the column holding a Monday by that Monday's ISO week", () => {
    const { start } = yearCalendarBounds(2026);
    const monday = new Date(2026, 0, 5);
    const column = getWeekColumns(cellsForYear(2026)).find(
      (c) => c.x === timeWeek.count(start, monday)
    );

    expect(column?.weekNumber).toBe(2);
    expect(column?.from).toBe("2026-01-05");
    expect(column?.to).toBe("2026-01-11");
  });

  it("gives distinct, ascending labels for a year that starts mid-week", () => {
    const numbers = getWeekColumns(cellsForYear(2026)).map((c) => c.weekNumber);

    expect(new Set(numbers).size).toBe(numbers.length);
    expect(numbers).toEqual([...numbers].sort((a, b) => a - b));
  });
});
