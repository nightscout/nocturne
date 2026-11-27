/**
 * A block the report holds as the spec gives it: a panel of any kind that the signal model does not
 * make, such as a ready-made report's chart, a report saved by an older builder, or a chart the member
 * chose to change signal by signal. The panel is the source of truth, so it saves exactly as it came;
 * each change here is a pure rewrite of it.
 */
import type {
  CustomReportCatalogue,
  CustomReportDataset,
  CustomReportDatasetMark,
} from "@nocturne/api-client";
import { formatList } from "#lib/utils/formatting.js";
import { plural } from "#lib/utils/plural.js";
import { bindingsOf, type Cell } from "./catalogue-vocabulary.js";
import { againstText, dividedByText, minusText, type Reason } from "./workspace.svelte.js";

export type Json = Record<string, unknown>;

/** A source of a calculated signal: one field of a dataset query. */
export interface GivenSource {
  dataset: Json;
  field: string;
}

/** A signal of a given chart, as the editor lists it. */
export interface GivenLayer {
  track: number;
  index: number;
  id: string;
  name: string;
  /** The name the member gave it, or "" for the one the engine writes. */
  title: string;
  mark: string;
  /** The ways it can be drawn, its own first. */
  marks: string[];
  /** Whether it has a scale of its own: alone in its row of the chart. */
  own: boolean;
  derived: boolean;
  /** What its horizontal axis holds, or null when that cannot be told. */
  axis: string | null;
  /** Its own range in hours, or null for the chart's; undefined when its axis takes none. */
  rangeHours: number | null | undefined;
}

/** A calculated signal the member can add to a given chart. */
export type CalculatedOp = "difference" | "ratio" | "rolling_mean" | "trend" | "band_sd" | "scatter" | "lag_correlation";

export const CALCULATED_OPS: CalculatedOp[] = ["rolling_mean", "band_sd", "trend", "difference", "ratio", "scatter", "lag_correlation"];

/** The ops that take two sources. */
export const PAIRED_OPS: readonly CalculatedOp[] = ["difference", "ratio", "scatter", "lag_correlation"];

/** The windows offered for a rolling average or a spread, in minutes. */
export const WINDOWS = [60, 120, 240, 480, 1440];

/** The axes a layer's own range applies on: time of day, minutes from an event, and days. */
const RANGED_AXES = ["time_of_day", "offset", "day"];

function isJson(value: unknown): value is Json {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

export function asObject(value: unknown): Json | null {
  return isJson(value) ? value : null;
}

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function asString(value: unknown): string | null {
  return typeof value === "string" ? value : null;
}

function strings(value: unknown): string[] {
  return asArray(value).filter((v): v is string => typeof v === "string");
}

/** A deep copy of a JSON value, unwrapped from any state proxy. */
export function copyJson(value: Json): Json {
  const copy: unknown = structuredClone($state.snapshot(value));
  return asObject(copy) ?? {};
}

function clone(panel: Json): Json {
  return copyJson(panel);
}

export function tracksOf(panel: Json): Json[] {
  return asArray(panel.tracks).map((t) => asObject(t) ?? {});
}

function layersOf(track: Json): Json[] {
  return asArray(track.layers).map((l) => asObject(l) ?? {});
}

/** Every layer of the panel. */
export function allLayers(panel: Json): Json[] {
  return tracksOf(panel).flatMap(layersOf);
}

/** The panel kind as the member reads it. */
export function givenKind(panel: Json): "chart" | "figures" | "table" | "comparison" {
  switch (panel.kind) {
    case "stat_tiles":
      return "figures";
    case "table":
      return "table";
    case "comparison":
      return "comparison";
    default:
      return "chart";
  }
}

function datasetOf(catalogue: CustomReportCatalogue, dataset: unknown): CustomReportDataset | undefined {
  const name = asString(asObject(dataset)?.name);
  return name ? catalogue.datasets.find((d) => d.name === name) : undefined;
}

function fieldLabel(catalogue: CustomReportCatalogue, dataset: unknown, field: string): string {
  return datasetOf(catalogue, dataset)?.fields.find((f) => f.name === field)?.label ?? field;
}

function fieldKind(catalogue: CustomReportCatalogue, dataset: unknown, field: string | null): string | null {
  if (!field) return null;
  return datasetOf(catalogue, dataset)?.fields.find((f) => f.name === field)?.kind ?? null;
}

/** A dataset layer's bindings as drawn: its own, else its mark's defaults. */
function effectiveBindings(catalogue: CustomReportCatalogue, layer: Json): Json {
  const own = asObject(layer.bindings);
  const given = own ? Object.fromEntries(Object.entries(own).filter(([, v]) => v !== null && v !== undefined)) : {};
  if (Object.keys(given).length > 0) return given;
  const mark = datasetOf(catalogue, layer.dataset)?.marks.find((m) => m.name === layer.mark);
  return mark ? { ...bindingsOf({ mark: mark.name, word: mark.name, bindings: mark.defaultBindings }) } : {};
}

/** The mark a layer is drawn as: its own, else the first its data allows. */
function markOf(catalogue: CustomReportCatalogue, layer: Json): string {
  const own = asString(layer.mark);
  if (own) return own;
  const derived = asObject(layer.derived);
  if (derived) return catalogue.ops.find((o) => o.name === derived.op)?.marks[0]?.name ?? "line";
  return datasetOf(catalogue, layer.dataset)?.marks[0]?.name ?? "line";
}

/** What a layer's horizontal axis holds: a field kind, "value" for a scatter, "lag" for a correlation. */
export function axisOf(catalogue: CustomReportCatalogue, layer: Json): string | null {
  const derived = asObject(layer.derived);
  if (derived) {
    if (derived.op === "scatter") return "value";
    if (derived.op === "lag_correlation") return "lag";
    const a = asObject(derived.a);
    if (!a) return null;
    if (a.derived) return axisOf(catalogue, { derived: a.derived });
    const ds = datasetOf(catalogue, a.dataset);
    const x = asString(ds?.marks[0]?.defaultBindings.x);
    return fieldKind(catalogue, a.dataset, x);
  }
  if (markOf(catalogue, layer) === "spans") return "time";
  return fieldKind(catalogue, layer.dataset, asString(effectiveBindings(catalogue, layer).x));
}

/** Whether layers along these axes may share a row of the chart: the same axis, or days and time. */
function shareAxis(a: string | null, b: string | null): boolean {
  if (a === null || b === null) return true;
  const calendar = (k: string) => k === "time" || k === "day";
  return a === b || (calendar(a) && calendar(b));
}

/** The words for a source of a calculated signal: its field's label. */
function sourceName(catalogue: CustomReportCatalogue, source: unknown): string {
  const s = asObject(source);
  if (!s) return "";
  const field = asString(s.field) ?? "";
  if (s.derived) return derivedName(catalogue, asObject(s.derived) ?? {});
  return fieldLabel(catalogue, s.dataset, field);
}

/** A calculated signal in words. */
function derivedName(catalogue: CustomReportCatalogue, derived: Json): string {
  const a = sourceName(catalogue, derived.a);
  const b = sourceName(catalogue, derived.b);
  const op = CALCULATED_OPS.find((o) => o === derived.op);
  if (!op) return a ? `Calculated from ${a}` : "Calculated signal";
  return opPhrase(op, a, b, typeof derived.minutes === "number" ? derived.minutes : null);
}

/** What a calculated signal shows, from the names of its sources: each one whole message to translate. */
function opPhrase(op: CalculatedOp, a: string, b: string, minutes: number | null): string {
  const window = minutes === null ? null : windowWords(minutes);
  switch (op) {
    case "difference":
      return minusText(a, b);
    case "ratio":
      return dividedByText(a, b);
    case "scatter":
      return againstText(b, a);
    case "lag_correlation":
      return `How ${a} and ${b} move together when one is shifted`;
    case "rolling_mean":
      return window === null ? `Rolling average of ${a}` : `Rolling average of ${a} over ${window}`;
    case "trend":
      return `Trend of ${a}`;
    case "band_sd":
      return window === null ? `Average and spread of ${a}` : `Average and spread of ${a} over ${window}`;
  }
}

/** The name of each kind of calculated signal, for the menu that adds one. */
export function opLabel(op: CalculatedOp): string {
  switch (op) {
    case "difference":
      return "Difference of two signals";
    case "ratio":
      return "Ratio of two signals";
    case "rolling_mean":
      return "Rolling average";
    case "trend":
      return "Trend line";
    case "band_sd":
      return "Average and spread";
    case "scatter":
      return "One signal against another";
    case "lag_correlation":
      return "How two signals move together when one is shifted";
  }
}

/**
 * A copy of a panel held as given with its title trimmed, or without a title when none is left: the
 * title field stores what the member types, spaces and all, and the spec saves it trimmed.
 */
export function withTrimmedTitle(panel: Json): Json {
  const copy = copyJson(panel);
  const title = typeof copy.title === "string" ? copy.title.trim() : null;
  if (title) copy.title = title;
  else delete copy.title;
  return copy;
}

export function windowWords(minutes: number): string {
  if (minutes % 1440 === 0) return plural(minutes / 1440, ["# day", "# days"]);
  if (minutes % 60 === 0) return plural(minutes / 60, ["# hour", "# hours"]);
  return plural(minutes, ["# minute", "# minutes"]);
}

/** A layer's name: the one the member gave it, else what it shows. */
function layerName(catalogue: CustomReportCatalogue, layer: Json): string {
  const title = asString(layer.title)?.trim();
  if (title) return title;
  const derived = asObject(layer.derived);
  if (derived) return derivedName(catalogue, derived);
  const ds = datasetOf(catalogue, layer.dataset);
  const bindings = effectiveBindings(catalogue, layer);
  const fields = [...strings(bindings.y), ...(strings(bindings.y).length === 0 ? strings(bindings.edges) : [])];
  const labels = fields.map((f) => fieldLabel(catalogue, layer.dataset, f));
  if (labels.length === 0) return ds?.label ?? String(asObject(layer.dataset)?.name ?? "");
  return labels.length === 1 ? labels[0] : formatList(labels);
}

function rangeHoursOf(range: unknown): number | null {
  const r = asObject(range);
  return r && typeof r.hours === "number" ? r.hours : null;
}

/** The ways a layer can be drawn: a calculated one by its op's marks, a dataset's by the marks along its axis. */
function markChoices(catalogue: CustomReportCatalogue, layer: Json): string[] {
  const own = markOf(catalogue, layer);
  const derived = asObject(layer.derived);
  let names: string[];
  if (derived) names = catalogue.ops.find((o) => o.name === derived.op)?.marks.map((m) => m.name) ?? [];
  else {
    const ds = datasetOf(catalogue, layer.dataset);
    const x = asString(effectiveBindings(catalogue, layer).x);
    names = (ds?.marks ?? []).filter((m) => m.name === own || fitsX(m, x)).map((m) => m.name);
  }
  return [own, ...names.filter((n) => n !== own)];
}

function fitsX(mark: CustomReportDatasetMark, x: string | null): boolean {
  const slot = mark.bindings.find((s) => s.key === "x");
  return x !== null && slot !== undefined && slot.fields.includes(x);
}

/** The signals of a given chart, row by row. */
export function givenLayers(catalogue: CustomReportCatalogue, panel: Json): GivenLayer[] {
  return tracksOf(panel).flatMap((track, t) => {
    const layers = layersOf(track);
    return layers.map((layer, l): GivenLayer => {
      const axis = axisOf(catalogue, layer);
      const ranged = !layer.derived && axis !== null && RANGED_AXES.includes(axis);
      return {
        track: t,
        index: l,
        id: asString(layer.id) ?? `${t}-${l}`,
        name: layerName(catalogue, layer),
        title: asString(layer.title) ?? "",
        mark: markOf(catalogue, layer),
        marks: markChoices(catalogue, layer),
        own: layers.length === 1,
        derived: Boolean(layer.derived),
        axis,
        rangeHours: ranged || rangeHoursOf(layer.range) !== null ? rangeHoursOf(layer.range) : undefined,
      };
    });
  });
}

function edit(panel: Json, change: (tracks: Json[]) => void): Json {
  const next = clone(panel);
  const tracks = tracksOf(next);
  change(tracks);
  next.tracks = tracks.filter((t) => layersOf(t).length > 0);
  return next;
}

function freshId(taken: string[], prefix: string): string {
  for (let n = 1; ; n++) if (!taken.includes(`${prefix}${n}`)) return `${prefix}${n}`;
}

function layerIds(panel: Json): string[] {
  return allLayers(panel).map((l) => asString(l.id) ?? "");
}

function trackIds(panel: Json): string[] {
  return tracksOf(panel).map((t) => asString(t.id) ?? "");
}

export function renameLayer(panel: Json, t: number, l: number, title: string): Json {
  return edit(panel, (tracks) => {
    const layer = layersOf(tracks[t])[l];
    const trimmed = title.trim();
    if (trimmed) layer.title = trimmed;
    else delete layer.title;
    tracks[t].layers = layersOf(tracks[t]).map((x, i) => (i === l ? layer : x));
  });
}

function validValue(value: unknown, fields: string[], counts: number[]): boolean {
  if (typeof value === "string") return fields.length === 0 || fields.includes(value);
  if (Array.isArray(value))
    return value.length > 0 && value.every((v) => typeof v === "string" && fields.includes(v)) && (counts.length === 0 || counts.includes(value.length));
  return typeof value === "boolean";
}

/** The layer drawn as `mark`: bindings that still fit kept, and the mark's defaults for the rest it needs. */
export function setLayerMark(catalogue: CustomReportCatalogue, panel: Json, t: number, l: number, mark: string): Json {
  return edit(panel, (tracks) => {
    const layers = layersOf(tracks[t]);
    const layer = layers[l];
    layer.mark = mark;
    if (!layer.derived) {
      const target = datasetOf(catalogue, layer.dataset)?.marks.find((m) => m.name === mark);
      if (target) {
        const current = effectiveBindings(catalogue, layer);
        const defaults = bindingsOf({ mark: target.name, word: target.name, bindings: target.defaultBindings });
        const next: Json = {};
        for (const slot of target.bindings) {
          const value = current[slot.key];
          if (value !== undefined && validValue(value, slot.fields, slot.counts)) next[slot.key] = value;
          else if (slot.required && defaults[slot.key] !== undefined) next[slot.key] = defaults[slot.key];
        }
        layer.bindings = next;
      }
    }
    tracks[t].layers = layers;
  });
}

/** Why the layer cannot take, or give up, a scale of its own; null when it can. */
export function ownScaleReason(catalogue: CustomReportCatalogue, panel: Json, t: number, l: number, on: boolean, maxTracks: number): Reason {
  const tracks = tracksOf(panel);
  const layers = layersOf(tracks[t] ?? {});
  if (on) {
    if (layers.length === 1) return null;
    if (tracks.length >= maxTracks) return `A chart has at most ${maxTracks} scales. Give another signal a shared scale first.`;
    return null;
  }
  if (layers.length > 1) return null;
  const neighbour = tracks[t - 1] ?? tracks[t + 1];
  if (!neighbour) return "No other signal is on this chart to share a scale with.";
  const axis = axisOf(catalogue, layers[l] ?? {});
  if (!layersOf(neighbour).every((x) => shareAxis(axis, axisOf(catalogue, x))))
    return "The signal next to it has another horizontal axis, so they cannot share a scale.";
  return null;
}

/** The layer alone in a new row under its own (on), or moved into the row above it, else below (off). */
export function setOwnScale(panel: Json, t: number, l: number, on: boolean): Json {
  const ids = trackIds(panel);
  return edit(panel, (tracks) => {
    const layers = layersOf(tracks[t]);
    if (on) {
      if (layers.length === 1) return;
      const [layer] = layers.splice(l, 1);
      tracks[t].layers = layers;
      tracks.splice(t + 1, 0, { id: freshId(ids, "t"), layers: [layer] });
      return;
    }
    if (layers.length > 1) return;
    const into = t > 0 ? t - 1 : t + 1;
    if (!tracks[into]) return;
    tracks[into].layers = t > 0 ? [...layersOf(tracks[into]), ...layers] : [...layers, ...layersOf(tracks[into])];
    tracks[t].layers = [];
  });
}

export function removeLayerReason(panel: Json): Reason {
  return allLayers(panel).length <= 1 ? "A chart keeps at least one signal. To remove the chart, remove the block." : null;
}

export function removeLayer(panel: Json, t: number, l: number): Json {
  return edit(panel, (tracks) => {
    tracks[t].layers = layersOf(tracks[t]).filter((_, i) => i !== l);
  });
}

export function setLayerRange(panel: Json, t: number, l: number, hours: number | null): Json {
  return edit(panel, (tracks) => {
    const layers = layersOf(tracks[t]);
    layers[l].range = hours === null ? null : { hours };
    tracks[t].layers = layers;
  });
}

/** The numbers drawn on the chart that a calculated signal can read: each dataset layer's lines, once. */
export function givenSources(catalogue: CustomReportCatalogue, panel: Json): { key: string; label: string; source: GivenSource }[] {
  const out: { key: string; label: string; source: GivenSource }[] = [];
  for (const layer of allLayers(panel)) {
    const dataset = asObject(layer.dataset);
    if (!dataset) continue;
    const ds = datasetOf(catalogue, dataset);
    const bindings = effectiveBindings(catalogue, layer);
    for (const field of strings(bindings.y)) {
      const f = ds?.fields.find((x) => x.name === field);
      if (!f || (f.kind !== "number" && f.kind !== "percent") || !f.aggregate) continue;
      const key = JSON.stringify([dataset, field]);
      if (out.some((o) => o.key === key)) continue;
      out.push({ key, label: f.label, source: { dataset: copyJson(dataset), field } });
    }
  }
  return out;
}

/** The track a source is drawn in, or -1. */
function trackOfSource(catalogue: CustomReportCatalogue, panel: Json, source: GivenSource): number {
  const key = JSON.stringify(source.dataset);
  return tracksOf(panel).findIndex((track) =>
    layersOf(track).some((l) => JSON.stringify(l.dataset) === key && strings(effectiveBindings(catalogue, l).y).includes(source.field))
  );
}

/**
 * The chart with a calculated signal added: an average, a spread or a trend beside its source on the
 * same scale, and every other in a row of its own at the end.
 */
export function addCalculated(
  catalogue: CustomReportCatalogue,
  panel: Json,
  op: CalculatedOp,
  a: GivenSource,
  b: GivenSource | null,
  minutes: number | null
): Json {
  const id = freshId(layerIds(panel), `${op.replace("_", "-")}-`);
  const mark = catalogue.ops.find((o) => o.name === op)?.marks[0]?.name ?? "line";
  const derived: Json = {
    op,
    a: { dataset: a.dataset, field: a.field },
    ...(b && PAIRED_OPS.includes(op) ? { b: { dataset: b.dataset, field: b.field } } : {}),
    ...(minutes !== null && (op === "rolling_mean" || op === "band_sd") ? { minutes } : {}),
  };
  const layer: Json = { id, derived, mark };
  const ids = trackIds(panel);
  return edit(panel, (tracks) => {
    const beside = op === "rolling_mean" || op === "band_sd" || op === "trend" ? trackOfSource(catalogue, panel, a) : -1;
    if (beside >= 0) tracks[beside].layers = [...layersOf(tracks[beside]), layer];
    else tracks.push({ id: freshId(ids, "t"), layers: [layer] });
  });
}

/** The chart with a signal of the catalogue added, as its cell draws it, in a row of its own. */
export function addSignalLayer(panel: Json, signal: string, cell: Cell): Json {
  const choice = cell.marks[0];
  const id = freshId(layerIds(panel), `${signal}-`);
  const dataset: Json = {
    name: cell.dataset,
    ...(asObject(cell.parameters) ?? {}),
    ...(cell.spanKind ? { kinds: [cell.spanKind] } : {}),
  };
  const layer: Json = { id, dataset, ...(choice ? { mark: choice.mark, bindings: bindingsOf(choice) } : {}) };
  const ids = trackIds(panel);
  return edit(panel, (tracks) => {
    tracks.push({ id: freshId(ids, "t"), layers: [layer] });
  });
}

/** The lens whose cells draw along the axis of the chart's first row; timeline when none matches. */
export function lensForAxis(axis: string | null): string {
  switch (axis) {
    case "time_of_day":
      return "typical_day";
    case "day":
      return "daily";
    default:
      return "timeline";
  }
}

/** A figures block's or a table's columns: the dataset's own for its kind, and any the panel names besides. */
export function givenColumns(catalogue: CustomReportCatalogue, panel: Json): { field: string; label: string; on: boolean }[] {
  const ds = datasetOf(catalogue, panel.dataset);
  if (!ds) return [];
  const chosen = asObject(panel.bindings)?.columns;
  const named = Array.isArray(chosen) ? strings(chosen) : null;
  const offered = ds.panelKinds.find((k) => k.name === panel.kind)?.columns ?? [];
  const fields = ds.fields.map((f) => f.name).filter((f) => offered.includes(f) || (named ?? []).includes(f));
  return fields.map((field) => ({ field, label: fieldLabel(catalogue, panel.dataset, field), on: named === null || named.includes(field) }));
}

export function toggleGivenColumn(catalogue: CustomReportCatalogue, panel: Json, field: string): Json {
  const columns = givenColumns(catalogue, panel);
  const next = clone(panel);
  const on = columns.filter((c) => (c.field === field ? !c.on : c.on)).map((c) => c.field);
  next.bindings = { ...(asObject(next.bindings) ?? {}), columns: on };
  return next;
}

/** Every dataset query the panel reads, layers' sources included, as JSON. */
export function givenReads(panel: Json): string[] {
  const out: string[] = [];
  const visit = (value: unknown) => {
    const o = asObject(value);
    if (Array.isArray(value)) value.forEach(visit);
    if (!o) return;
    for (const [key, v] of Object.entries(o)) {
      if (key === "dataset" && asObject(v)) {
        const text = JSON.stringify(v);
        if (!out.includes(text)) out.push(text);
      } else visit(v);
    }
  };
  visit(panel);
  return out;
}

/** The longest range every dataset the panel reads can cover, in hours. */
export function givenMaxHours(catalogue: CustomReportCatalogue, panel: Json): number {
  const limits = givenReads(panel).map((r) => datasetOf(catalogue, JSON.parse(r))?.maxRangeHours ?? catalogue.maxRangeHours);
  return Math.min(catalogue.maxRangeHours, ...limits);
}

/** What a given block shows, as one sentence. */
export function givenCaption(catalogue: CustomReportCatalogue, panel: Json): string {
  switch (givenKind(panel)) {
    case "chart": {
      const names = allLayers(panel).map((l) => layerName(catalogue, l));
      return names.length <= 1 ? (names[0] ?? "") : formatList(names);
    }
    case "comparison":
      return "Two periods side by side";
    default:
      return datasetOf(catalogue, panel.dataset)?.label ?? "";
  }
}
