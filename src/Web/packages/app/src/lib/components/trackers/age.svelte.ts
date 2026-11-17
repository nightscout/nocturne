// A .svelte.ts module so wuchale extracts its strings; it holds no runes.

/**
 * Hours elapsed from a server timestamp to `now`, so an age moves on the same clock as the
 * schedule steps it is shown beside. Undefined without a timestamp.
 */
export function ageHoursSince(from: string | Date | undefined, now: number): number | undefined {
  if (!from) return undefined;
  const start = typeof from === "string" ? Date.parse(from) : from.getTime();
  return Number.isNaN(start) ? undefined : Math.max(0, now - start) / 3_600_000;
}

/** A tracker's or a legacy age's elapsed hours, as the pills show them. */
export function formatAge(hours: number | undefined | null): string {
  if (hours === undefined || hours === null) return "n/a";
  if (hours < 1) return `${Math.floor(hours * 60)}m`;
  if (hours < 24) return `${Math.floor(hours)}h`;
  const days = Math.floor(hours / 24);
  const h = Math.floor(hours % 24);
  return h > 0 ? `${days}d ${h}h` : `${days}d`;
}

/** A lifespan in hours, in whole days from one day up. */
export function formatLifespan(hours: number | undefined | null): string {
  if (hours === undefined || hours === null) return "Not set";
  if (hours < 24) return `${hours} hours`;
  const days = Math.floor(hours / 24);
  return days === 1 ? "1 day" : `${days} days`;
}
