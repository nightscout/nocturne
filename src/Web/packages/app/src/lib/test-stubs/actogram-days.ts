import type { ActogramDay } from "$lib/components/actogram";

const MS_PER_MINUTE = 60_000;

const MS_PER_HOUR = 60 * MS_PER_MINUTE;

/**
 * A served day that began `offsetMinutes` east of UTC, `hours` long, followed by a day `nextHours`
 * long. A day that is not 24 hours changed offset before its 6h mark, so every later mark sits
 * `hours - 24` hours off an even spacing. The same holds for the next day's marks.
 */
export function servedDay(date: string, offsetMinutes = 0, hours = 24, nextHours = 24): ActogramDay {
  const startMills = Date.parse(`${date}T00:00:00Z`) - offsetMinutes * MS_PER_MINUTE;
  const endMills = startMills + hours * MS_PER_HOUR;
  const marksOf = (from: number, shift: number) =>
    [6, 12, 18].map((hour) => from + hour * MS_PER_HOUR + shift);
  return {
    date,
    startMills,
    endMills,
    hourMarks: [
      ...marksOf(startMills, hours - 24),
      ...marksOf(endMills, nextHours - 24),
    ],
  };
}

/** Consecutive served days from `firstDate`, each starting where the last ended. */
export function servedDays(firstDate: string, count: number, offsetMinutes = 0): ActogramDay[] {
  return Array.from({ length: count }, (_, index) => {
    const date = new Date(Date.parse(`${firstDate}T00:00:00Z`) + index * 86_400_000)
      .toISOString()
      .slice(0, 10);
    return servedDay(date, offsetMinutes);
  });
}
