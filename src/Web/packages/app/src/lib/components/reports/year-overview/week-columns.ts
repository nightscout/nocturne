import { timeDay, timeMonday } from "d3-time";
import { uniqueBy } from "$lib/utils/collections";
import { toDayString } from "$lib/utils/date-range";

export type WeekColumn = {
  x: number;
  weekNumber: number;
  from: string;
  to: string;
};

/** ISO 8601 week number of the local date. */
export function isoWeekNumber(date: Date): number {
  const d = new Date(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()));
  d.setUTCDate(d.getUTCDate() + 4 - (d.getUTCDay() || 7));
  const yearStart = new Date(Date.UTC(d.getUTCFullYear(), 0, 1));
  return Math.ceil(((d.getTime() - yearStart.getTime()) / 86400000 + 1) / 7);
}

/** Monday and Sunday, as day strings, of the ISO week containing the date. */
export function weekBounds(date: Date): { from: string; to: string } {
  const monday = timeMonday.floor(date);
  return { from: toDayString(monday), to: toDayString(timeDay.offset(monday, 6)) };
}

/** One column per heatmap x position, labelled by the week of its first dated cell. */
export function getWeekColumns(
  cells: Array<{ x: number; data?: { date?: Date } }>
): WeekColumn[] {
  const dated = cells.flatMap((cell) =>
    cell.data?.date ? [{ x: cell.x, date: cell.data.date }] : []
  );
  return uniqueBy(dated, (cell) => cell.x)
    .map(({ x, date }) => ({ x, weekNumber: isoWeekNumber(date), ...weekBounds(date) }))
    .sort((a, b) => a.x - b.x);
}
