/**
 * Where a chart panel's tracks, axes and marks go: the pixel bands of its tracks, each side's value
 * scale, and every mark's geometry in plot pixels. The live chart and the static image draw from the
 * same geometry, so the picture is the same in both.
 */
import { area, curveMonotoneX, curveStepAfter, line } from "d3";
import { scaleLinear } from "d3-scale";
import { resolveChartColor } from "../../colors.js";
import { isChartColor } from "../../enums.js";
import { cohortBands, type BandColors } from "../percentile-bands.js";
import { boundFields, type ReportField, type ReportLayer, type ReportPanel, type ReportRow, type ReportTrack, type ReportXKind } from "../types.js";

/** Minutes from an event or of the day lie on a time axis from this origin, so one time scale serves every axis. */
export const MINUTE_ORIGIN = Date.UTC(2000, 0, 1);

const DAY = 86_400_000;

/** The pixels between two tracks. */
export const TRACK_GAP = 18;

/** Whether the axis is drawn as time: an instant, a day, minutes of the day or from an event. */
export function isTimeAxis(kind: ReportXKind): boolean {
  return kind === "time" || kind === "day" || kind === "time_of_day" || kind === "offset";
}

/** A row's raw x as the chart's x domain value: a date on a time axis, else the number. */
export function toDomain(kind: ReportXKind, raw: number): Date | number {
  if (kind === "time" || kind === "day") return new Date(raw);
  if (kind === "time_of_day" || kind === "offset") return new Date(MINUTE_ORIGIN + raw * 60_000);
  return raw;
}

/** The raw x of a domain value, for a value the pointer gives back. */
export function fromDomain(kind: ReportXKind, value: Date | number): number {
  const n = value instanceof Date ? value.getTime() : value;
  if (kind === "time_of_day" || kind === "offset") return (n - MINUTE_ORIGIN) / 60_000;
  return n;
}

export function num(row: ReportRow | undefined, name: string | null | undefined): number | null {
  const value = name ? row?.[name] : undefined;
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

/** The x field a layer's rows lie along: its x, or a span's start. */
export function xFieldOf(layer: Pick<ReportLayer, "mark" | "bindings">): string {
  return (layer.mark === "spans" ? layer.bindings.start : layer.bindings.x) ?? "";
}

/** The layers of a chart that were read and can be drawn. */
export function drawnLayers(track: ReportTrack): ReportLayer[] {
  return track.layers.filter((l) => l.status === "ok");
}

/** The chart's horizontal extent: the window over time, the day over a time of day, else the rows' extent. */
export function xExtent(panel: Pick<ReportPanel, "tracks" | "window" | "xKind">): [number, number] {
  const kind = panel.xKind ?? "time";
  if (kind === "time") return [panel.window.from, panel.window.to];
  if (kind === "time_of_day") return [0, 1440];
  const xs: number[] = [];
  for (const track of panel.tracks)
    for (const layer of drawnLayers(track)) {
      const name = xFieldOf(layer);
      for (const row of layer.rows) {
        const x = num(row, name);
        if (x !== null) xs.push(x);
      }
    }
  if (xs.length === 0) return kind === "lag" ? [-180, 180] : [0, 1];
  let min = Math.min(...xs);
  let max = Math.max(...xs);
  if (kind === "day") return [min, max + DAY];
  if (min === max) {
    min -= 1;
    max += 1;
  }
  if (kind === "value") {
    const pad = (max - min) * 0.05;
    return [min - pad, max + pad];
  }
  return [min, max];
}

/** The fields whose values a layer puts on its axis: its lines, edges and fit. */
export function valueFields(layer: ReportLayer): ReportField[] {
  if (layer.mark === "markers" || layer.mark === "spans") return [];
  const names = [...(layer.bindings.y ?? []), ...(layer.bindings.edges ?? []), ...(layer.bindings.fit ? [layer.bindings.fit] : [])];
  return boundFields(layer, names);
}

/** A track's band of plot pixels. */
export interface TrackBand {
  top: number;
  bottom: number;
  height: number;
}

/** Each track's band in a plot of `height` pixels, sized by its share. */
export function trackBands(tracks: readonly Pick<ReportTrack, "height">[], height: number, gap = TRACK_GAP): TrackBand[] {
  const weights = tracks.map((t) => Math.max(0.1, t.height));
  const total = weights.reduce((a, b) => a + b, 0);
  const room = Math.max(0, height - gap * Math.max(0, tracks.length - 1));
  let top = 0;
  return weights.map((w) => {
    const h = (room * w) / total;
    const band = { top, bottom: top + h, height: h };
    top += h + gap;
    return band;
  });
}

/** One side's value axis on a track: its unit, domain, and the pixel of a value. */
export interface SideScale {
  unit: string | null;
  domain: [number, number];
  toPixel: (value: number) => number;
}

/** A track's left and right value scales, each over its layers' values and from zero; a glucose side reaches the high limit. */
export function sideScales(
  track: ReportTrack,
  band: TrackBand,
  highLimit: number | null,
): { left: SideScale | null; right: SideScale | null } {
  function side(which: "left" | "right"): SideScale | null {
    const layers = drawnLayers(track).filter((l) => l.side === which);
    const placed = track.layers.find((l) => l.side === which);
    if (!placed) return null;
    let min = 0;
    let max = 0;
    let glucose = false;
    for (const layer of layers)
      for (const field of valueFields(layer)) {
        glucose ||= field.glucoseScale === true;
        for (const row of layer.rows) {
          const v = num(row, field.name);
          if (v === null) continue;
          min = Math.min(min, v);
          max = Math.max(max, v);
        }
      }
    if (glucose && highLimit !== null) max = Math.max(max, highLimit);
    if (max === min) max = min + 1;
    const span = max - min;
    const domain: [number, number] = [min < 0 ? min - span * 0.05 : min, max + span * 0.08];
    const scale = scaleLinear().domain(domain).range([band.bottom, band.top]);
    return { unit: placed.unit ?? null, domain, toPixel: (v: number) => scale(v) };
  }
  return { left: side("left"), right: side("right") };
}

/** The colour a field of a layer is drawn in: the layer's series hue where the API gave one, else the field's own. */
export function fieldColor(layer: Pick<ReportLayer, "color">, field: Pick<ReportField, "color"> | undefined): string {
  if (layer.color && isChartColor(layer.color)) return resolveChartColor(layer.color);
  if (field?.color && isChartColor(field.color)) return resolveChartColor(field.color);
  return "var(--foreground)";
}

/** Whether a line of the field takes the report's glucose colouring: a glucose field the API gave no hue. */
export function glucoseColoured(layer: Pick<ReportLayer, "color">, field: Pick<ReportField, "glucoseScale">): boolean {
  return !layer.color && field.glucoseScale === true;
}

/** A layer's own colour: its first drawn field's. */
export function layerColor(layer: ReportLayer): string {
  return fieldColor(layer, valueFields(layer)[0]);
}

type XPixel = (raw: number) => number;
type YPixel = (value: number) => number;

/** A line through the rows' values of one field, in pixels, a step field drawn as steps. */
export function linePath(rows: readonly ReportRow[], xName: string, field: ReportField, x: XPixel, y: YPixel): string {
  return (
    line<ReportRow>()
      .defined((r) => num(r, xName) !== null && num(r, field.name) !== null)
      .x((r) => x(num(r, xName)!))
      .y((r) => y(num(r, field.name)!))
      .curve(field.step ? curveStepAfter : curveMonotoneX)(sortedBy(rows, xName)) ?? ""
  );
}

/** The area between two fields (or a field and a baseline), in pixels. */
export function areaPath(
  rows: readonly ReportRow[],
  xName: string,
  low: string | number,
  high: string,
  x: XPixel,
  y: YPixel,
  step = false,
): string {
  const lowOf = (r: ReportRow) => (typeof low === "number" ? low : num(r, low));
  return (
    area<ReportRow>()
      .defined((r) => num(r, xName) !== null && lowOf(r) !== null && num(r, high) !== null)
      .x((r) => x(num(r, xName)!))
      .y0((r) => y(lowOf(r)!))
      .y1((r) => y(num(r, high)!))
      .curve(step ? curveStepAfter : curveMonotoneX)(sortedBy(rows, xName)) ?? ""
  );
}

function sortedBy(rows: readonly ReportRow[], name: string): ReportRow[] {
  return [...rows].sort((a, b) => (num(a, name) ?? 0) - (num(b, name) ?? 0));
}

/** One set of a band layer: a series value's rows and its colours. */
export interface BandSet {
  key: string;
  label: string | null;
  rows: ReportRow[];
  /** Drawn dashed over the rest: a cohort's comparison range. */
  comparison: boolean;
  outer: string;
  inner: string;
  line: string;
}

/**
 * A band layer's sets: one per series value (event cohorts, each in its cohort's hue, a comparison
 * range apart), else the layer's one set in its colour.
 */
export function bandSets(layer: ReportLayer): BandSet[] {
  const series = layer.bindings.series;
  if (!series) {
    const color = layerColor(layer);
    const hue = tints(color);
    return [{ key: layer.id, label: null, rows: layer.rows, comparison: false, outer: hue.outer, inner: hue.inner, line: hue.median }];
  }
  const sets = new Map<string, BandSet>();
  for (const row of layer.rows) {
    const comparison = row.range === "comparison";
    const label = String(row[series] ?? "");
    const key = `${label}|${comparison}`;
    let set = sets.get(key);
    if (!set) {
      const cohort = num(row, "cohort");
      const hue = layer.color ? tints(layerColor(layer)) : cohortBands(cohort !== null ? cohort - 1 : sets.size);
      set = { key, label, rows: [], comparison, outer: hue.outer, inner: hue.inner, line: hue.median };
      sets.set(key, set);
    }
    set.rows.push(row);
  }
  const all = [...sets.values()];
  // One group and no comparison range: nothing to tell apart, so the band keeps the field's colour.
  if (all.length === 1 && !all[0].comparison && !layer.color) {
    const hue = tints(layerColor(layer));
    return [{ ...all[0], outer: hue.outer, inner: hue.inner, line: hue.median }];
  }
  return all;
}


function tints(color: string): BandColors {
  return {
    outer: `color-mix(in oklab, ${color} 18%, transparent)`,
    inner: `color-mix(in oklab, ${color} 36%, transparent)`,
    median: color,
  };
}

/** One bar of a bars layer, in pixels. */
export interface BarRect {
  key: string;
  x: number;
  y: number;
  width: number;
  height: number;
  color: string;
}

/** A bars layer over a continuous axis: each row's fields side by side, or stacked, a slot wide. */
export function barRects(layer: ReportLayer, x: XPixel, y: YPixel, plotWidth: number): BarRect[] {
  const xName = layer.bindings.x ?? "";
  const fields = boundFields(layer, layer.bindings.y);
  const xs = [...new Set(layer.rows.map((r) => num(r, xName)).filter((v): v is number => v !== null))].sort((a, b) => a - b);
  const pixels = xs.map(x);
  let slot = plotWidth / Math.max(1, xs.length);
  for (let i = 1; i < pixels.length; i++) slot = Math.min(slot, pixels[i] - pixels[i - 1]);
  const width = Math.max(1, slot * 0.8);
  const stacked = layer.bindings.stacked === true;
  const each = stacked ? width : width / Math.max(1, fields.length);
  const zero = y(0);
  const rects: BarRect[] = [];
  const centre = (at: number) => x(layer.xKind === "day" ? at + DAY / 2 : at);
  for (const row of layer.rows) {
    const at = num(row, xName);
    if (at === null) continue;
    let base = 0;
    fields.forEach((field, i) => {
      const value = num(row, field.name);
      if (value === null) return;
      const from = stacked ? base : 0;
      const to = from + value;
      if (stacked) base = to;
      const top = y(Math.max(from, to));
      const bottom = stacked ? y(Math.min(from, to)) : zero;
      rects.push({
        key: `${at}-${field.name}`,
        x: centre(at) - width / 2 + (stacked ? 0 : i * each),
        y: Math.min(top, bottom),
        width: each,
        height: Math.abs(bottom - top),
        color: fieldColor(layer, field),
      });
    });
  }
  return rects;
}

/** One point of a points layer, in pixels. */
export interface PointMark {
  key: string;
  cx: number;
  cy: number;
  color: string;
}

export function pointMarks(layer: ReportLayer, x: XPixel, y: YPixel): PointMark[] {
  const xName = layer.bindings.x ?? "";
  return boundFields(layer, layer.bindings.y).flatMap((field) =>
    layer.rows.flatMap((row, i) => {
      const at = num(row, xName);
      const value = num(row, field.name);
      return at === null || value === null ? [] : [{ key: `${i}-${field.name}`, cx: x(at), cy: y(value), color: fieldColor(layer, field) }];
    }),
  );
}

/** A treatment glyph placed in pixels. */
export interface PlacedGlyph {
  key: string;
  x: number;
  y: number;
  kind: "bolus" | "carbs" | "bg_check" | "basal_injection";
  amount: number;
  automatic: boolean;
}

/**
 * Where a markers layer's glyphs sit: on the track's first line at the reading nearest each one, a
 * carb raised above a bolus at the same time; with no line on the track, along its top.
 */
export function placeGlyphs(layer: ReportLayer, track: ReportTrack, band: TrackBand, x: XPixel, yOf: (l: ReportLayer) => YPixel | null): PlacedGlyph[] {
  const host = drawnLayers(track).find((l) => (l.mark === "line" || l.mark === "area") && (l.bindings.y ?? []).length > 0);
  const hostY = host ? yOf(host) : null;
  const hostX = host?.bindings.x ?? "";
  const hostField = host?.bindings.y?.[0] ?? "";
  const along = host ? sortedBy(host.rows, hostX).filter((r) => num(r, hostX) !== null && num(r, hostField) !== null) : [];
  return (layer.markers ?? []).map((m, i) => {
    let y = band.top + 22;
    if (hostY && along.length > 0) {
      const nearest = nearestBy(along, (r) => num(r, hostX)!, m.time);
      y = hostY(num(nearest, hostField)!);
    }
    return {
      key: `${m.kind}-${m.time}-${i}`,
      x: x(m.time),
      y: m.kind === "carbs" ? y - 34 : y,
      kind: m.kind,
      amount: m.amount,
      automatic: m.automatic ?? false,
    };
  });
}

/** One span of a spans layer, in pixels, in its lane. */
export interface SpanRect {
  key: string;
  x: number;
  y: number;
  width: number;
  height: number;
  color: string;
  label: string;
  lane: string;
}

/** A spans layer's spans, a lane for each value of its category, in the order they first appear. */
export function spanRects(layer: ReportLayer, band: TrackBand, x: XPixel): { lanes: { name: string; y: number; height: number }[]; spans: SpanRect[] } {
  const { start, end, category } = layer.bindings;
  const colorField = layer.fields.find((f) => f.colorField)?.colorField ?? null;
  const labelField = layer.fields.find((f) => f.colorField)?.name ?? category ?? "";
  const names = [...new Set(layer.rows.map((r) => String(r[category ?? ""] ?? "")))];
  const laneHeight = band.height / Math.max(1, names.length);
  const lanes = names.map((name, i) => ({ name, y: band.top + i * laneHeight, height: laneHeight }));
  const spans = layer.rows.flatMap((row, i) => {
    const from = num(row, start);
    const to = num(row, end);
    if (from === null || to === null) return [];
    const lane = lanes[names.indexOf(String(row[category ?? ""] ?? ""))];
    const theme = colorField ? row[colorField] : null;
    return [{
      key: `${i}`,
      x: x(from),
      y: lane.y + 1,
      width: Math.max(1, x(to) - x(from)),
      height: Math.max(1, lane.height - 2),
      color: isChartColor(theme) ? resolveChartColor(theme) : "var(--muted-foreground)",
      label: String(row[labelField] ?? ""),
      lane: lane.name,
    }];
  });
  return { lanes, spans };
}

function nearestBy<T>(items: readonly T[], at: (item: T) => number, target: number): T {
  let lo = 0;
  let hi = items.length - 1;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (at(items[mid]) < target) lo = mid + 1;
    else hi = mid;
  }
  if (lo > 0 && Math.abs(at(items[lo - 1]) - target) <= Math.abs(at(items[lo]) - target)) return items[lo - 1];
  return items[lo];
}

/** How the tooltip finds a layer's row under the pointer. */
export interface RowMatch {
  /** How far a row may be from the pointer's slot and still be that slot's: the widest gap between slots. */
  tolerance: number;
  /** The same for a treatment marker, which is a single moment: a few pixels' width of the axis. */
  markerTolerance: number;
  /** The panel folds its layers onto a grid: a row is the slot's only when it lies on that slot. */
  gridded: boolean;
}

/** The match a panel's tooltip uses: a marker's tolerance is 1% of the axis. */
export function rowMatch(panel: Pick<ReportPanel, "tracks" | "window" | "xKind" | "grid">, tolerance: number): RowMatch {
  const [min, max] = xExtent(panel);
  const kind = panel.xKind ?? "time";
  return {
    tolerance,
    markerTolerance: (max - min) / 100,
    gridded: (panel.grid ?? 0) > 0 && (kind === "time" || kind === "offset" || kind === "time_of_day"),
  };
}

/**
 * A layer's rows under the pointer at `raw`: the spans that cover it; for a line or area of step fields
 * the last row of each series at or before it, which is the value the step draws there; on a gridded
 * panel the rows on that slot; else every series' row at the nearest x, within the match's tolerance.
 */
export function rowsAt(layer: ReportLayer, raw: number, match: RowMatch): ReportRow[] {
  if (layer.mark === "spans") {
    const { start, end } = layer.bindings;
    return layer.rows.filter((r) => (num(r, start) ?? Infinity) <= raw && raw <= (num(r, end) ?? -Infinity));
  }
  const xName = layer.bindings.x ?? "";
  const placed = layer.rows.filter((r) => num(r, xName) !== null);
  if (placed.length === 0) return [];
  const values = valueFields(layer);
  if ((layer.mark === "line" || layer.mark === "area") && values.length > 0 && values.every((f) => f.step)) {
    const series = layer.bindings.series;
    const last = new Map<unknown, ReportRow>();
    for (const row of sortedBy(placed, xName)) if (num(row, xName)! <= raw) last.set(series ? row[series] : null, row);
    return [...last.values()];
  }
  if (match.gridded) return placed.filter((r) => num(r, xName) === raw);
  const tolerance = layer.mark === "markers" ? match.markerTolerance : match.tolerance;
  const nearest = num(nearestBy(sortedBy(placed, xName), (r) => num(r, xName)!, raw), xName)!;
  if (Math.abs(nearest - raw) > tolerance) return [];
  return placed.filter((r) => num(r, xName) === nearest);
}

/** The x values every drawn layer has a row at, in order: the slots the pointer moves between. */
export function slots(panel: Pick<ReportPanel, "tracks">): number[] {
  const xs = new Set<number>();
  for (const track of panel.tracks)
    for (const layer of drawnLayers(track)) {
      const name = xFieldOf(layer);
      for (const row of layer.rows) {
        const x = num(row, name);
        if (x !== null) xs.add(x);
      }
    }
  return [...xs].sort((a, b) => a - b);
}

/** A legend entry: the layer's words and colour; no colour for a line coloured by glucose, which has none. */
export interface LegendEntry {
  id: string;
  title: string;
  unit: string | null;
  color: string | null;
  mark: ReportLayer["mark"];
  /** How the layer was folded onto the grid, or null. */
  context: string | null;
}

export function legend(panel: Pick<ReportPanel, "tracks">): LegendEntry[] {
  return panel.tracks.flatMap((track) =>
    drawnLayers(track).map((layer) => {
      const lead = valueFields(layer)[0];
      return {
        id: layer.id,
        title: layer.title,
        unit: layer.unit ?? null,
        color: layer.mark === "markers" || layer.mark === "spans" || (lead && glucoseColoured(layer, lead) && layer.mark === "line") ? null : layerColor(layer),
        mark: layer.mark,
        context: layer.context ?? null,
      };
    }),
  );
}
