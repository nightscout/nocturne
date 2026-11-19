import { formatLocale } from "#lib/utils/formatting.js";

/** A number of minutes as the locale writes it, e.g. "75 min". */
export function minutes(value: number): string {
  return new Intl.NumberFormat(formatLocale(), {
    style: "unit",
    unit: "minute",
    unitDisplay: "short",
    maximumFractionDigits: 0,
  }).format(value);
}
