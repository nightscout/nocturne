/**
 * The range a viewer runs a saved report over without changing it: the range it was saved with, a
 * preset, or two dates. A preset or two dates take the place of the report's range and of each block's range that is the
 * same as the report's (the stored spec writes every block's range out); a block with a range of its
 * own keeps it.
 */
import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
import { asObject, givenMaxHours, type Json } from "./given.svelte.js";
import { withoutBuilder } from "./saved-state.js";
import { rangeLabel } from "./vocabulary-words.svelte.js";
import { spanRange } from "./date-span.js";
import type { DateSpan } from "./workspace.svelte.js";

/** The choice of the range the report was saved with. */
export const SAVED_RANGE = "saved";

/** The choice of two dates the viewer picks. */
export const CUSTOM_RANGE = "custom";

export interface ViewerRange {
  options: { id: string; label: string; reason: string | null }[];
  value: string;
  saved: string;
  /** The spec to run. */
  spec: Json;
  /** What the viewer should know about the range shown, or null. */
  note: string | null;
  /** Why the two dates picked cannot be shown, or null; the report then runs over its saved range. */
  spanReason: string | null;
}

function daysText(hours: number): string {
  const days = Math.floor(hours / 24);
  return days === 1 ? "1 day" : `${days} days`;
}

function hoursOf(range: unknown): number | null {
  const r = asObject(range);
  return r && typeof r.hours === "number" ? r.hours : null;
}

export function viewerRange(
  catalogue: CustomReportCatalogue,
  saved: unknown,
  choice: string | null,
  span: DateSpan | null = null,
  now: number = Date.now()
): ViewerRange {
  const spec = withoutBuilder(saved);
  const reportHours = hoursOf(spec.range);
  const panels = (Array.isArray(spec.panels) ? spec.panels : []).map((p) => asObject(p) ?? {});
  /** A block follows the report when it or the report has no range, or it has the report's. */
  const follows = (panel: Json) =>
    panel.kind !== "comparison" &&
    (panel.range === null || panel.range === undefined || reportHours === null || hoursOf(panel.range) === reportHours);
  const following = panels.filter(follows);
  const max = following.length === 0 ? catalogue.maxRangeHours : Math.min(...following.map((p) => givenMaxHours(catalogue, p)));
  const presets = catalogue.workspace.rangePresets;
  const options = [
    { id: SAVED_RANGE, label: "As saved", reason: null },
    ...presets.map((p) => ({
      id: p.id,
      label: rangeLabel(p.id),
      reason: p.hours > max ? `This report shows at most ${daysText(max)}.` : null,
    })),
  ];
  const asSaved = { options, value: SAVED_RANGE, saved: SAVED_RANGE, spec, note: null, spanReason: null };
  const kept = panels.some((p) => !follows(p) && p.kind !== "comparison");
  const note = kept ? "A block with a range of its own keeps it. Nothing in the saved report changes." : "Nothing in the saved report changes.";
  const runOver = (range: Json): Json => ({
    ...spec,
    range,
    panels: panels.map((p) => (follows(p) ? { ...p, range } : p)),
  });
  if (choice === CUSTOM_RANGE && span) {
    const range = spanRange(span);
    const hours = (Date.parse(range.to) - Date.parse(range.from)) / 3_600_000;
    const reason =
      Date.parse(range.from) > now
        ? "There is no data after today. Choose a first date of today or before."
        : hours > max
          ? `This report shows at most ${daysText(max)}. Choose fewer days.`
          : null;
    if (reason) return { ...asSaved, spanReason: reason };
    return { options, value: CUSTOM_RANGE, saved: SAVED_RANGE, spec: runOver(range), note, spanReason: null };
  }
  const preset = presets.find((p) => p.id === choice && p.hours <= max);
  if (!preset) return asSaved;
  return { options, value: preset.id, saved: SAVED_RANGE, spec: runOver({ hours: preset.hours }), note, spanReason: null };
}
