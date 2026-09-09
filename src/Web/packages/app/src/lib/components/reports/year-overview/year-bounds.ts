/**
 * The interval the year heatmap lays out.
 *
 * **Half-open**: local midnight opening 1 January, to local midnight opening
 * the following 1 January. layerchart's `Calendar` builds its cells from
 * `timeDays(start, end)`, and a d3 time range excludes its end, so an end of 31
 * December leaves the last day of the year without a cell — while the December
 * month path, which derives its own month end, still outlines the square where
 * that cell should be.
 */
export function yearCalendarBounds(year: number): { start: Date; end: Date } {
  return { start: new Date(year, 0, 1), end: new Date(year + 1, 0, 1) };
}
