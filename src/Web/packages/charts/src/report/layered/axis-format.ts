import { formatClockTime, type ChartFormat } from "../../format.js";
import { formatMinuteOfDay, formatOffset, type ReportXKind } from "../types.js";
import { fromDomain } from "./layered-layout.js";

/** A tick or a pointer position on a chart's horizontal axis, in the reader's format. */
export function formatAxisValue(kind: ReportXKind, value: unknown, format: ChartFormat): string {
  if (!(value instanceof Date) && typeof value !== "number") return String(value);
  const raw = fromDomain(kind, value);
  switch (kind) {
    case "time":
      return formatClockTime(new Date(raw), format);
    case "day":
      return new Intl.DateTimeFormat(format.locale, { month: "short", day: "numeric", timeZone: format.timeZone }).format(raw);
    case "time_of_day":
      return formatMinuteOfDay(raw, format);
    case "offset":
    case "lag":
      return formatOffset(raw, format);
    default:
      return new Intl.NumberFormat(format.locale, { maximumFractionDigits: 2 }).format(raw);
  }
}
