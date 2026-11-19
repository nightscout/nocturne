import { ErroneousEpisodeKind, type ErroneousReadingsDay } from "#lib/api/index.js";

/** What a day's card says under its date, e.g. "2 episodes · 29 invalid readings · 1 on its own". */
export function daySummary(day: ErroneousReadingsDay): string {
  const parts: string[] = [];
  const episodes = day.episodes?.length ?? 0;
  if (episodes > 0) parts.push(`${episodes} ${episodes === 1 ? "episode" : "episodes"}`);
  const readings = (day.episodes ?? []).filter((e) => e.kind === ErroneousEpisodeKind.Readings).length;
  const alone = day.isolatedReadingMills?.length ?? 0;
  const invalid = day.invalidReadings ?? 0;
  if (readings > 0 || alone > 0) parts.push(`${invalid} invalid ${invalid === 1 ? "reading" : "readings"}`);
  if (alone > 0) parts.push(`${alone} on ${alone === 1 ? "its" : "their"} own`);
  return parts.join(" · ");
}
