/**
 * NSwag-generated fields typed `Date` arrive over the wire as ISO strings
 * (the client's jsonParseReviver is a no-op), so coerce defensively, and return
 * null rather than an Invalid Date so callers can take their empty branch
 * instead of feeding NaN to Intl.
 */
export function toDate(value: Date | string | number | undefined | null): Date | null {
  if (value == null) return null;
  const d = value instanceof Date ? value : new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

export interface RelativeTimeOptions {
  /** Reference time. Pass a ticking value where the result is rendered, or the text freezes. */
  now?: number;
  /** Returned when `at` is missing or not a date. The caller owns that wording. */
  fallback?: string;
}

function cachedRelativeFormatter(numeric: Intl.RelativeTimeFormatNumeric) {
  let formatter: Intl.RelativeTimeFormat | null = null;
  let cachedFor: string | null = null;
  return (locale: string) => {
    // Keyed on the requested tag, not on `resolvedOptions().locale`: ICU answers
    // "nb-NO" with "nb", so comparing against the resolved tag never matches and
    // rebuilds the formatter on every call.
    if (!formatter || locale !== cachedFor) {
      cachedFor = locale;
      formatter = new Intl.RelativeTimeFormat(locale, {
        style: "narrow",
        numeric,
      });
    }
    return formatter;
  };
}

const pastFormatter = cachedRelativeFormatter("auto");
// "auto" words a day ahead as "tomorrow", which is wrong for 47 hours away.
const futureFormatter = cachedRelativeFormatter("always");

/**
 * How long ago a moment was, in the largest whole unit: "now", "5m ago", "3h ago",
 * "2d ago", then the absolute date from a week on. The words are the locale's own.
 * A moment in the future reads as "now".
 */
export function relativeAge(
  at: Date | string | number | null | undefined,
  locale: string,
  opts: RelativeTimeOptions = {}
): string {
  const { now = Date.now() } = opts;
  const d = at ? toDate(at) : null;
  return relativeTime(d && d.getTime() > now ? now : at, locale, { ...opts, now });
}

/**
 * How far a moment is from `now`, in either direction: "3h ago" or "in 3h". A
 * moment behind reads in the largest whole unit, as in `relativeAge`. A moment
 * ahead reads in the nearest whole unit, so 47 hours is "in 2d", never "tomorrow".
 */
export function relativeTime(
  at: Date | string | number | null | undefined,
  locale: string,
  opts: RelativeTimeOptions = {}
): string {
  const { now = Date.now(), fallback = "" } = opts;
  if (!at) return fallback;
  const d = toDate(at);
  if (!d) return fallback;

  const ahead = d.getTime() > now;
  const sign = ahead ? 1 : -1;
  const round = ahead ? Math.round : Math.floor;
  const formatter = (ahead ? futureFormatter : pastFormatter)(locale);
  const minutes = round(Math.abs(now - d.getTime()) / 60000);
  if (minutes < 1) return pastFormatter(locale).format(0, "second");
  if (minutes < 60) return formatter.format(sign * minutes, "minute");
  const hours = round(minutes / 60);
  if (hours < 24) return formatter.format(sign * hours, "hour");
  const days = round(hours / 24);
  if (days < 7) return formatter.format(sign * days, "day");
  return d.toLocaleDateString(locale);
}
