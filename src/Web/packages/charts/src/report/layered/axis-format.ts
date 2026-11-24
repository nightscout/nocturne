import { formatClockTime, type ChartFormat } from "../../format.js";
import { formatMinuteOfDay, formatOffset, type ReportXKind } from "../types.js";
import { fromDomain } from "./layered-layout.js";

/** A tick or a pointer position on a chart's horizontal axis, in the reader's format. */
export function formatAxisValue(kind: ReportXKind, value: unknown, format: ChartFormat): string {
  if (!(value instanceof Date) && typeof value !== "number") return String(value);
  const raw = fromDomain(kind, value);
  switch (kind) {
    case "time":
      return isLocalMidnight(raw, format) ? formatDay(raw, format) : formatClockTime(new Date(raw), format);
    case "day":
      return formatDay(raw, format);
    case "time_of_day":
      return formatMinuteOfDay(raw, format);
    case "offset":
    case "lag":
      return formatOffset(raw, format);
    default:
      return new Intl.NumberFormat(format.locale, { maximumFractionDigits: 2 }).format(raw);
  }
}

/** The pointer's position in the tooltip's heading: on a time axis the day as well as the clock time. */
export function formatPointerValue(kind: ReportXKind, value: unknown, format: ChartFormat): string {
  if (kind !== "time" || (!(value instanceof Date) && typeof value !== "number")) return formatAxisValue(kind, value, format);
  const raw = fromDomain(kind, value);
  return isLocalMidnight(raw, format) ? formatDay(raw, format) : `${formatDay(raw, format)}, ${formatClockTime(new Date(raw), format)}`;
}

function formatDay(raw: number, format: ChartFormat): string {
  return new Intl.DateTimeFormat(format.locale, { month: "short", day: "numeric", timeZone: format.timeZone }).format(raw);
}

/** Whether an instant is midnight where it is read: a time axis names the day there, as ticks over several days fall. */
function isLocalMidnight(raw: number, format: ChartFormat): boolean {
  const parts = new Intl.DateTimeFormat("en-GB", { hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23", timeZone: format.timeZone }).formatToParts(raw);
  return parts.filter((p) => p.type === "hour" || p.type === "minute" || p.type === "second").every((p) => Number(p.value) === 0);
}
