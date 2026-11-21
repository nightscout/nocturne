/**
 * The range a viewer runs a saved report over without changing it: the range it was saved with, a
 * preset, or two dates. A preset or two dates take the place of the report's range and of the range
 * of each block that follows the report's; a block with a range of its own keeps it. The stored spec
 * writes every block's range out, so which blocks follow is read from the workspace's state saved
 * with the spec (`followsReport`).
 */
import type { CustomReportCatalogue } from "@nocturne/api-client";
import { asObject, givenMaxHours, type Json } from "./given.svelte.js";
import { BUILDER_VERSION, builderOf, withoutBuilder } from "./saved-state.js";
import { daysText, rangeLabel } from "./vocabulary-words.svelte.js";
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

/** A range in one form for comparison: its hours, or its two instants; null for none. */
function rangeKey(range: unknown): string | null {
  const r = asObject(range);
  if (!r) return null;
  if (typeof r.hours === "number") return `h${r.hours}`;
  if (typeof r.from === "string" && typeof r.to === "string") return `${Date.parse(r.from)}-${Date.parse(r.to)}`;
  return null;
}

/**
 * Whether a panel of `spec` follows the report's range. A panel the workspace's saved state names
 * follows when its block has no range of its own (a block held as given, when its range is none or the
 * report's). A panel with no saved state to read follows when it has no range, or the report's.
 */
function followsReport(saved: unknown, spec: Json): (panel: Json) => boolean {
  const reportRange = rangeKey(spec.range);
  const sameAsReport = (range: unknown) => range === null || range === undefined || rangeKey(range) === reportRange;
  const builder = builderOf(saved);
  const blocks = builder?.version === BUILDER_VERSION ? asObject(builder.state)?.blocks : null;
  /** Whether a block follows, by the exact id of its one panel, or by the prefix of its panels' ids. */
  const byId: Record<string, boolean> = {};
  const byPrefix: [string, boolean][] = [];
  for (const raw of Array.isArray(blocks) ? blocks : []) {
    const block = asObject(raw);
    if (!block || typeof block.key !== "number") continue;
    const prefix = `b${block.key}-`;
    if (block.kind === "given") {
      const panel = asObject(block.panel) ?? {};
      byId[typeof panel.id === "string" ? panel.id : `${prefix}panel`] = sameAsReport(panel.range);
    } else if (block.kind === "chart") {
      byPrefix.push([prefix, (asObject(block.chart)?.range ?? null) === null]);
    } else {
      const follows = (block.range ?? null) === null;
      if (typeof block.panelId === "string") byId[block.panelId] = follows;
      else byPrefix.push([prefix, follows]);
    }
  }
  return (panel) => {
    if (panel.kind === "comparison") return false;
    const id = typeof panel.id === "string" ? panel.id : "";
    const known = (Object.hasOwn(byId, id) ? byId[id] : undefined) ?? byPrefix.find(([prefix]) => id.startsWith(prefix))?.[1];
    return known ?? sameAsReport(panel.range);
  };
}

/**
 * The range to run `saved` over for the viewer's `choice`. Two dates are read as days in `timeZone`,
 * the patient's zone the report's days are cut in.
 */
export function viewerRange(
  catalogue: CustomReportCatalogue,
  saved: unknown,
  choice: string | null,
  span: DateSpan | null = null,
  timeZone: string | null = null,
  now: number = Date.now()
): ViewerRange {
  const spec = withoutBuilder(saved);
  const panels = (Array.isArray(spec.panels) ? spec.panels : []).map((p) => asObject(p) ?? {});
  const follows = followsReport(saved, spec);
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
    const range = spanRange(span, timeZone);
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
