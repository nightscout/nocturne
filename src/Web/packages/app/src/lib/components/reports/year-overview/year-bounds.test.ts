import { describe, it, expect } from "vitest";
import { timeDays, timeMonths } from "d3-time";

import { yearCalendarBounds } from "./year-bounds";

/** `YYYY-MM-DD` in the local calendar, matching the cells' own day identity. */
function localDay(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${date.getFullYear()}-${month}-${day}`;
}

/** The days layerchart's `Calendar` lays out for `year`. */
function heatmapDays(year: number): string[] {
  const { start, end } = yearCalendarBounds(year);
  return timeDays(start, end).map(localDay);
}

describe("yearCalendarBounds", () => {
  it("opens on 1 January and ends on the following 1 January", () => {
    const { start, end } = yearCalendarBounds(2025);
    expect(localDay(start)).toBe("2025-01-01");
    expect(localDay(end)).toBe("2026-01-01");
    expect(start.getHours()).toBe(0);
    expect(end.getHours()).toBe(0);
  });

  it("covers 31 December", () => {
    const days = heatmapDays(2025);
    expect(days).toContain("2025-12-31");
    expect(days.at(-1)).toBe("2025-12-31");
    expect(days).toHaveLength(365);
  });

  it("covers 31 December of a leap year, and 29 February with it", () => {
    const days = heatmapDays(2024);
    expect(days).toContain("2024-02-29");
    expect(days).toContain("2024-12-30");
    expect(days).toContain("2024-12-31");
    expect(days.at(-1)).toBe("2024-12-31");
    expect(days).toHaveLength(366);
  });

  it("opens on 1 January and spills into no other year", () => {
    const days = heatmapDays(2025);
    expect(days[0]).toBe("2025-01-01");
    expect(days.every((day) => day.startsWith("2025-"))).toBe(true);
  });

  it("still spans exactly the twelve months the labels are drawn from", () => {
    const { start, end } = yearCalendarBounds(2025);
    const months = timeMonths(start, end).map(localDay);
    expect(months).toHaveLength(12);
    expect(months[0]).toBe("2025-01-01");
    expect(months.at(-1)).toBe("2025-12-01");
  });

  it("covers 31 December whichever weekday it falls on", () => {
    // 2022 ends on a Saturday (last cell of a column), 2023 on a Sunday (first
    // cell of a new one), 2024 on a Tuesday.
    for (const year of [2022, 2023, 2024, 2025, 2026, 2027]) {
      expect(heatmapDays(year).at(-1)).toBe(`${year}-12-31`);
    }
  });
});
