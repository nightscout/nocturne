/** Series keys in `Date.getDay()` order, so a reading's weekday indexes straight in. */
export const DAY_KEYS = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"] as const;

export type DayKey = (typeof DAY_KEYS)[number];

/** A chart row: the time-of-day plus one mean per weekday that has readings. */
export type WeekdayBucketRow = { time: Date } & Partial<Record<DayKey, number>>;

const BUCKET_MINUTES = 5;

/**
 * Group readings into 5-minute time-of-day buckets per weekday and average each
 * cell. Readings accumulate as a sum and a count and are divided once — folding
 * each new reading in as `(previous + next) / 2` weights the last reading at half
 * the cell and halves every earlier one again, so any cell with more than two
 * readings is not the mean.
 *
 * @param convert - Maps a mg/dL reading into the displayed unit.
 * @param anchor - Date supplying the calendar day for the x-axis, which reads
 *   only the time-of-day part.
 */
export function buildWeekdayBuckets(
  entries: readonly { mills?: number; mgdl?: number }[],
  convert: (mgdl: number) => number,
  anchor: Date = new Date()
): WeekdayBucketRow[] {
  type Bucket = { time: Date; means: Map<DayKey, { sum: number; count: number }> };
  const buckets = new Map<number, Bucket>();

  for (const entry of entries) {
    const at = new Date(entry.mills ?? 0);
    const dayKey = DAY_KEYS[at.getDay()];
    const minutesInDay = at.getHours() * 60 + at.getMinutes();
    const bucket = Math.round(minutesInDay / BUCKET_MINUTES) * BUCKET_MINUTES;

    let slot = buckets.get(bucket);
    if (!slot) {
      slot = {
        time: new Date(
          anchor.getFullYear(),
          anchor.getMonth(),
          anchor.getDate(),
          Math.floor(bucket / 60),
          bucket % 60
        ),
        means: new Map(),
      };
      buckets.set(bucket, slot);
    }

    const running = slot.means.get(dayKey) ?? { sum: 0, count: 0 };
    running.sum += convert(entry.mgdl ?? 0);
    running.count += 1;
    slot.means.set(dayKey, running);
  }

  return Array.from(buckets.values())
    .sort((a, b) => a.time.getTime() - b.time.getTime())
    .map(({ time, means }) => {
      const row: WeekdayBucketRow = { time };
      for (const [dayKey, { sum, count }] of means) row[dayKey] = sum / count;
      return row;
    });
}
