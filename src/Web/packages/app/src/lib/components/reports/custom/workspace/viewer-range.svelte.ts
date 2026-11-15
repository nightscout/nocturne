/**
 * The range a viewer runs a saved report over without changing it: the range it was saved with, or a
 * preset. A preset takes the place of the report's range and of each block's range that is the
 * same as the report's (the stored spec writes every block's range out); a block with a range of its
 * own keeps it.
 */
import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
import { asObject, givenMaxHours, type Json } from "./given.svelte.js";
import { withoutBuilder } from "./saved-state.js";
import { rangeLabel } from "./vocabulary-words.svelte.js";

/** The choice of the range the report was saved with. */
export const SAVED_RANGE = "saved";

export interface ViewerRange {
  options: { id: string; label: string; reason: string | null }[];
  value: string;
  saved: string;
  /** The spec to run. */
  spec: Json;
  /** What the viewer should know about the range shown, or null. */
  note: string | null;
}

function hoursOf(range: unknown): number | null {
  const r = asObject(range);
  return r && typeof r.hours === "number" ? r.hours : null;
}

export function viewerRange(catalogue: CustomReportCatalogue, saved: unknown, choice: string | null): ViewerRange {
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
      reason: p.hours > max ? `This report shows at most ${Math.floor(max / 24)} days.` : null,
    })),
  ];
  const preset = presets.find((p) => p.id === choice && p.hours <= max);
  if (!preset) return { options, value: SAVED_RANGE, saved: SAVED_RANGE, spec, note: null };
  const run: Json = {
    ...spec,
    range: { hours: preset.hours },
    panels: panels.map((p) => (follows(p) ? { ...p, range: { hours: preset.hours } } : p)),
  };
  const kept = panels.some((p) => !follows(p) && p.kind !== "comparison");
  return {
    options,
    value: preset.id,
    saved: SAVED_RANGE,
    spec: run,
    note: kept ? "A block with a range of its own keeps it. Nothing in the saved report changes." : "Nothing in the saved report changes.",
  };
}
