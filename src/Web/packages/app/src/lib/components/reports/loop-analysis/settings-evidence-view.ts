import type { SettingsEvidenceEpisode, SettingsEvidenceFigure } from "#lib/api/index.js";
import { SLOT_MINUTES } from "./daily-profile-view";

const SLOTS_PER_HOUR = 60 / SLOT_MINUTES;

/** A column of a block's episode table: its heading and an episode's value as it reads. */
export interface EpisodeColumn<T> {
  label: string;
  value: (episode: T) => string;
  /** What the heading's values mean, where that needs saying, shown as the heading's tooltip. */
  hint?: string;
  /** Hidden on narrow widths. */
  wide?: boolean;
}

/** The slots a block covers on the shared time-of-day axis, from the hours the API gave it. */
export function blockSpan(figure: SettingsEvidenceFigure): { start: number; end: number } {
  return { start: (figure.startHour ?? 0) * SLOTS_PER_HOUR, end: (figure.endHour ?? 0) * SLOTS_PER_HOUR };
}

/** The block a slot of the day falls in. */
export function blockAt<T extends SettingsEvidenceFigure>(blocks: T[], slot: number): T | undefined {
  return blocks.find((block) => {
    const span = blockSpan(block);
    return slot >= span.start && slot < span.end;
  });
}

/** The same block in the comparison range, matched by its hours. */
export function matchingBlock<T extends SettingsEvidenceFigure>(
  blocks: T[] | null | undefined,
  figure: SettingsEvidenceFigure
): T | undefined {
  return blocks?.find((block) => block.startHour === figure.startHour && block.endHour === figure.endHour);
}

/** The Day in Review search for an episode's local day and start. */
export function episodeSearch(episode: SettingsEvidenceEpisode): string {
  return `date=${episode.date ?? ""}&time=${episode.time ?? ""}`;
}

/**
 * The y extent, in the axis's units, that every block's profile value, observed value and middle
 * half fits in, the comparison's too, from zero or below and padded a little above.
 */
export function valueDomain(
  figures: (SettingsEvidenceFigure | null | undefined)[],
  floor: number,
  display: (value: number) => number = (v) => v
): [number, number] {
  const known = figures
    .flatMap((f) => [f?.profile, f?.observed, f?.spread?.p25, f?.spread?.p75])
    .filter((v): v is number => v != null && Number.isFinite(v))
    .map(display);
  const high = Math.max(display(floor), ...known);
  const low = Math.min(0, ...known);
  return [low, high * 1.1];
}
