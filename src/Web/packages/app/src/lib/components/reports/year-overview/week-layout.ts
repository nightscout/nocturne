import {
  timeDay,
  timeDays,
  timeFriday,
  timeMonday,
  timeMonth,
  timeSaturday,
  timeSunday,
  timeThursday,
  timeTuesday,
  timeWednesday,
} from "d3-time";
import { weekdayIndex } from "$lib/components/calendar/calendar-date";
import { toDayString } from "$lib/utils/date-range";

const WEEKS = [
  timeSunday,
  timeMonday,
  timeTuesday,
  timeWednesday,
  timeThursday,
  timeFriday,
  timeSaturday,
];

export type DayCell = { date: Date; column: number; row: number };

export type WeekColumn = {
  column: number;
  start: Date;
  from: string;
  to: string;
};

export type YearLayout = { days: DayCell[]; columns: WeekColumn[] };

/** The first and last columns can reach past the range, so a column's `from` and `to` are always its own seven days. */
export function layoutYear(start: Date, end: Date, firstDay: number): YearLayout {
  const week = WEEKS[firstDay];
  const days = timeDays(start, end).map((date) => ({
    date,
    column: week.count(start, date),
    row: weekdayIndex(date, firstDay),
  }));
  const columnCount = days.length === 0 ? 0 : days[days.length - 1].column + 1;
  const firstColumnStart = week.floor(start);
  const columns = Array.from({ length: columnCount }, (_, column) => {
    const columnStart = week.offset(firstColumnStart, column);
    return {
      column,
      start: columnStart,
      from: toDayString(columnStart),
      to: toDayString(timeDay.offset(columnStart, 6)),
    };
  });
  return { days, columns };
}

export function monthOutlinePath(
  monthStart: Date,
  rangeStart: Date,
  firstDay: number,
  cellSize: [number, number]
): string {
  const week = WEEKS[firstDay];
  const monthEnd = timeDay.offset(timeMonth.offset(monthStart, 1), -1);
  const startColumn = week.count(rangeStart, monthStart);
  const endColumn = week.count(rangeStart, monthEnd);
  const startRow = weekdayIndex(monthStart, firstDay);
  const endRow = weekdayIndex(monthEnd, firstDay);
  const [w, h] = cellSize;
  return [
    `M${(startColumn + 1) * w},${startRow * h}`,
    `H${startColumn * w}`,
    `V${7 * h}`,
    `H${endColumn * w}`,
    `V${(endRow + 1) * h}`,
    `H${(endColumn + 1) * w}`,
    "V0",
    `H${(startColumn + 1) * w}`,
    "Z",
  ].join(" ");
}
