/**
 * Where a chart panel's tracks, axes and marks go: the pixel bands of its tracks, each side's value
 * scale, and every mark's geometry in plot pixels. The live chart and the static image draw from the
 * same geometry, so the picture is the same in both.
 */
import { area, curveMonotoneX, curveStepAfter, line } from "d3";
import { scaleLinear } from "d3-scale";
import { resolveChartColor } from "../../colors.js";
import { isChartColor } from "../../enums.js";
import { textureKey, type TextureKey } from "../../print/patterns.js";
import { cohortBands, type BandColors } from "../percentile-bands.js";
import { boundFields, rowSentence, type ReportField, type ReportLayer, type ReportPanel, type ReportRow, type ReportTrack, type ReportXKind } from "../types.js";

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

/** The axis a track's layers are drawn along: its own, else the panel's. */
function trackXKind(track: Pick<ReportTrack, "xKind">, panel: Pick<ReportPanel, "xKind">): ReportXKind {
  return track.xKind ?? panel.xKind ?? "time";
}

/** A run of consecutive tracks along one axis, drawn over its own horizontal scale with its own bottom axis. */
export interface AxisGroup {
  kind: ReportXKind;
  /** The index of the group's first track in the panel. */
  first: number;
  /** The panel cut to the group's tracks and axis, for the geometry that reads a whole panel. */
  panel: ReportPanel;
}

/** Whether tracks along `a` and `b` share one horizontal axis: one kind, or time and days, which is drawn as time. */
function sharesAxis(a: ReportXKind, b: ReportXKind): boolean {
  const calendar = (k: ReportXKind) => k === "time" || k === "day";
  return a === b || (calendar(a) && calendar(b));
}

/**
 * The panel's tracks in runs along one axis, top to bottom; one group for a panel whose tracks share an
 * axis. A track along days beside tracks along time joins them on the time axis, as a layer along days
 * does on a track along time.
 */
export function axisGroups(panel: ReportPanel): AxisGroup[] {
  const groups: AxisGroup[] = [];
  panel.tracks.forEach((track, i) => {
    const kind = trackXKind(track, panel);
    const last = groups.at(-1);
    if (last && sharesAxis(last.kind, kind)) {
      last.panel.tracks.push(track);
      if (last.kind !== kind) last.kind = last.panel.xKind = "time";
    } else groups.push({ kind, first: i, panel: { ...panel, xKind: kind, tracks: [track] } });
  });
  return groups;
}

/** The point the reader is at: a run of tracks (an axis group), its slot there and that slot's pixel. */
export interface PlotFocus {
  group: number;
  raw: number;
  x: number;
}

/** The pixels from the top of a group's first track to the bottom of its last. */
export function groupSpan(groups: readonly AxisGroup[], bands: readonly TrackBand[], g: number): { top: number; bottom: number } {
  const first = groups[g].first;
  const last = g + 1 < groups.length ? groups[g + 1].first - 1 : bands.length - 1;
  return { top: bands[first]?.top ?? 0, bottom: bands[last]?.bottom ?? 0 };
}

/** The group a point `y` pixels down the plot is over: the first whose tracks, or the axis under them, reach it. */
export function groupAt(groups: readonly AxisGroup[], bands: readonly TrackBand[], y: number): number {
  for (let g = 0; g < groups.length - 1; g++) if (y <= groupSpan(groups, bands, g).bottom + AXIS_ROOM) return g;
  return Math.max(0, groups.length - 1);
}

/** The value of `xs` (sorted) nearest `raw`, or null for none. */
export function nearestSlot(xs: readonly number[], raw: number): number | null {
  return xs.length === 0 ? null : nearestBy(xs, (x) => x, raw);
}

/**
 * The raw x in each group at the horizontal position of `raw` in the first: every axis maps its raw
 * values to pixels linearly, so a position is the same fraction of each group's extent.
 */
export function rawAcross(groups: readonly AxisGroup[], raw: number): number[] {
  if (groups.length === 0) return [];
  const [min, max] = xExtent(groups[0].panel);
  const fraction = max === min ? 0 : (raw - min) / (max - min);
  return groups.map((group, i) => {
    if (i === 0) return raw;
    const [from, to] = xExtent(group.panel);
    return from + fraction * (to - from);
  });
}

/** The order marks are painted in on a track: fills under strokes, so a later bar or area never hides a line or a point. */
const PAINT_ORDER: Record<ReportLayer["mark"], number> = { spans: 0, area: 1, band: 1, bars: 2, line: 3, points: 4, markers: 5 };

/** A track's drawn layers in paint order: fills first, then lines, points and markers; the spec's order within each. */
export function paintOrder(track: ReportTrack): ReportLayer[] {
  return drawnLayers(track)
    .map((layer, i) => ({ layer, i }))
    .sort((a, b) => PAINT_ORDER[a.layer.mark] - PAINT_ORDER[b.layer.mark] || a.i - b.i)
    .map((x) => x.layer);
}

/** The layers of a chart that were read and can be drawn. */
export function drawnLayers(track: ReportTrack): ReportLayer[] {
  return track.layers.filter((l) => l.status === "ok");
}

/** The chart's horizontal extent: the window over time, the day over a time of day, else the rows' extent. */
export function xExtent(panel: Pick<ReportPanel, "tracks" | "window" | "xKind">): [number, number] {
  const kind = panel.xKind ?? "time";
  if (kind === "time") {
    const days = panel.tracks.flatMap((track) => drawnLayers(track).filter((layer) => layer.xKind === "day")
      .flatMap((layer) => layer.rows.map((row) => num(row, xFieldOf(layer))).filter((value): value is number => value !== null)));
    return [Math.min(panel.window.from, ...days), panel.window.to];
  }
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
  if (kind === "offset") return [min, max + smallestStep(xs)];
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

/** The least distance between two distinct values: the width of a slot that starts at each. */
function smallestStep(xs: readonly number[]): number {
  const sorted = [...new Set(xs)].sort((a, b) => a - b);
  let step = Infinity;
  for (let i = 1; i < sorted.length; i++) step = Math.min(step, sorted[i] - sorted[i - 1]);
  return Number.isFinite(step) ? step : 0;
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

/**
 * The pixels left clear under the lowest track, so the lowest value axis label ("0") sits above the
 * time axis's labels instead of on them; on a small chart they would touch.
 */
export const AXIS_CLEARANCE = 8;

/** The pixels a bottom axis takes between two axis groups: its tick labels and the clearance above them. */
export const AXIS_ROOM = 24;

/**
 * Each track's band in a plot of `height` pixels, sized by its share, clear of the time axis. A track
 * that starts a new axis group (`breaks[i]`) is set below room for the axis of the group above it.
 */
export function trackBands(tracks: readonly Pick<ReportTrack, "height">[], height: number, gap = TRACK_GAP, breaks: readonly boolean[] = []): TrackBand[] {
  const weights = tracks.map((t) => Math.max(0.1, t.height));
  const total = weights.reduce((a, b) => a + b, 0);
  const axes = tracks.reduce((n, _, i) => n + (i > 0 && breaks[i] ? 1 : 0), 0);
  const room = Math.max(0, height - AXIS_CLEARANCE - gap * Math.max(0, tracks.length - 1) - AXIS_ROOM * axes);
  let top = 0;
  return weights.map((w, i) => {
    if (i > 0 && breaks[i]) top += AXIS_ROOM;
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

/** The values one side of a track spans, from zero, in its unit; a glucose side reaches the high limit. */
interface SideExtent {
  unit: string | null;
  min: number;
  max: number;
}

function sideExtent(track: ReportTrack, which: "left" | "right", highLimit: number | null): SideExtent | null {
  const placed = track.layers.find((l) => l.side === which);
  if (!placed) return null;
  let min = 0;
  let max = 0;
  let glucose = false;
  for (const layer of drawnLayers(track).filter((l) => l.side === which))
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
  return { unit: placed.unit ?? null, min, max };
}

/** The values each side of a track must cover beyond its own, as `sharedValueExtents` gives them. */
export interface SharedSides {
  left?: [number, number];
  right?: [number, number];
}

/**
 * A track's left and right value scales, each over its layers' values and from zero; a glucose side
 * reaches the high limit, and a side given a shared extent covers it too.
 */
export function sideScales(
  track: ReportTrack,
  band: TrackBand,
  highLimit: number | null,
  shared?: SharedSides,
): { left: SideScale | null; right: SideScale | null } {
  function side(which: "left" | "right"): SideScale | null {
    const own = sideExtent(track, which, highLimit);
    if (!own) return null;
    const also = shared?.[which];
    const min = also ? Math.min(own.min, also[0]) : own.min;
    let max = also ? Math.max(own.max, also[1]) : own.max;
    if (max === min) max = min + 1;
    const span = max - min;
    const domain: [number, number] = [min < 0 ? min - span * 0.05 : min, max + span * 0.08];
    const scale = scaleLinear().domain(domain).range([band.bottom, band.top]);
    return { unit: own.unit, domain, toPixel: (v: number) => scale(v) };
  }
  return { left: side("left"), right: side("right") };
}

/**
 * One value scale for each side of each track across panels drawn side by side as small multiples,
 * keyed by track id: the values that side spans in every panel, where every panel draws it in one
 * unit. A side whose unit differs between panels keeps its own scale in each.
 */
export function sharedValueExtents(panels: readonly Pick<ReportPanel, "tracks" | "thresholds">[]): Record<string, SharedSides> {
  type Seen = SideExtent & { mixed: boolean };
  const tracks = new Map<string, { left?: Seen; right?: Seen }>();
  for (const panel of panels)
    for (const track of panel.tracks) {
      const sides = tracks.get(track.id) ?? {};
      tracks.set(track.id, sides);
      for (const which of ["left", "right"] as const) {
        const extent = sideExtent(track, which, panel.thresholds?.high ?? null);
        if (!extent) continue;
        const seen = sides[which];
        if (!seen) sides[which] = { ...extent, mixed: false };
        else {
          seen.mixed ||= seen.unit !== extent.unit;
          seen.min = Math.min(seen.min, extent.min);
          seen.max = Math.max(seen.max, extent.max);
        }
      }
    }
  const out: Record<string, SharedSides> = {};
  for (const [id, sides] of tracks) {
    const shared: SharedSides = {};
    for (const which of ["left", "right"] as const) {
      const side = sides[which];
      if (side && !side.mixed) shared[which] = [side.min, side.max];
    }
    out[id] = shared;
  }
  return out;
}

/** The colour a field of a layer is drawn in: the layer's series hue where the API gave one, else the field's own. */
export function fieldColor(layer: Pick<ReportLayer, "color">, field: Pick<ReportField, "color"> | undefined): string {
  if (layer.color && isChartColor(layer.color)) return resolveChartColor(layer.color);
  if (field?.color && isChartColor(field.color)) return resolveChartColor(field.color);
  return "var(--foreground)";
}

/** The texture a field's fill is hatched with: its own, else its layer's, else none. */
export function fieldTexture(layer: Pick<ReportLayer, "texture">, field: Pick<ReportField, "texture"> | undefined): TextureKey | null {
  return textureKey(field?.texture) ?? textureKey(layer.texture);
}

/** A layer's own texture: its first drawn field's, else the layer's. */
export function layerTexture(layer: ReportLayer): TextureKey | null {
  return fieldTexture(layer, valueFields(layer)[0]);
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
  /** The hatch over the set's ranges, when the layer's one set shares its colour with another band. */
  texture: TextureKey | null;
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
    return [{ key: layer.id, label: null, rows: layer.rows, comparison: false, outer: hue.outer, inner: hue.inner, line: hue.median, texture: bandTexture(layer) }];
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
      set = { key, label, rows: [], comparison, outer: hue.outer, inner: hue.inner, line: hue.median, texture: null };
      sets.set(key, set);
    }
    set.rows.push(row);
  }
  const all = [...sets.values()];
  // One group and no comparison range: nothing to tell apart, so the band keeps the field's colour.
  if (all.length === 1 && !all[0].comparison && !layer.color) {
    const hue = tints(layerColor(layer));
    return [{ ...all[0], outer: hue.outer, inner: hue.inner, line: hue.median, texture: bandTexture(layer) }];
  }
  return all;
}

/** The hatch over a layer's fill as its swatch shows it: a band's, or a bars or area layer's first field's; none for other marks. */
export function fillTexture(layer: ReportLayer): TextureKey | null {
  if (layer.mark === "band") return bandTexture(layer);
  return layer.mark === "bars" || layer.mark === "area" ? layerTexture(layer) : null;
}

/** Every texture the panel's bars, areas and bands are hatched with, once each. */
export function panelTextures(panel: Pick<ReportPanel, "tracks">): TextureKey[] {
  const keys = new Set<TextureKey>();
  for (const track of panel.tracks)
    for (const layer of drawnLayers(track)) {
      if (layer.mark === "band") {
        const key = bandTexture(layer);
        if (key) keys.add(key);
      } else if (layer.mark === "bars" || layer.mark === "area") {
        for (const field of boundFields(layer, layer.bindings.y)) {
          const key = fieldTexture(layer, field);
          if (key) keys.add(key);
        }
      }
    }
  return [...keys];
}

/** A band's texture: the layer's, else its middle field's, else its edges'. */
function bandTexture(layer: ReportLayer): TextureKey | null {
  const named = [...(layer.bindings.y ?? []), ...(layer.bindings.edges ?? [])];
  return textureKey(layer.texture) ?? boundFields(layer, named).map((f) => textureKey(f.texture)).find((t) => t !== null) ?? null;
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
  texture: TextureKey | null;
}

/**
 * A bars layer over a continuous axis: each row's fields side by side, or stacked, a slot wide, cut to
 * the plot: a day on a time axis whose window starts or ends inside it is drawn only within the window.
 */
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
      const left = Math.max(0, centre(at) - width / 2 + (stacked ? 0 : i * each));
      const right = Math.min(plotWidth, centre(at) - width / 2 + (stacked ? 0 : i * each) + each);
      if (right <= left) return;
      rects.push({
        key: `${at}-${field.name}`,
        x: left,
        y: Math.min(top, bottom),
        width: right - left,
        height: Math.abs(bottom - top),
        color: fieldColor(layer, field),
        texture: fieldTexture(layer, field),
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

/**
 * The glyphs whose amounts are written: from the left, each whose kind's last written amount is at least `gap`
 * pixels before it, so close glyphs do not write over each other. The rest show their amounts in the tooltip.
 */
export function labelledGlyphs(glyphs: readonly PlacedGlyph[], gap: number): Set<string> {
  const last = new Map<PlacedGlyph["kind"], number>();
  const keep = new Set<string>();
  for (const glyph of [...glyphs].sort((a, b) => a.x - b.x)) {
    const before = last.get(glyph.kind);
    if (before !== undefined && glyph.x - before < gap) continue;
    last.set(glyph.kind, glyph.x);
    keep.add(glyph.key);
  }
  return keep;
}

/** The pixels a lane name of `text` takes at the lanes' 10px type, with the room around it. */
export function laneNameWidth(text: string): number {
  return Math.ceil(text.length * 5.6) + 12;
}

/** The names of every spans lane on the panel's drawn tracks, in the order they are drawn. */
export function laneNames(panel: Pick<ReportPanel, "tracks">): string[] {
  return panel.tracks.flatMap((track) =>
    drawnLayers(track)
      .filter((l) => l.mark === "spans")
      .flatMap((l) => [...new Set(l.rows.map((r) => String(r[l.bindings.category ?? ""] ?? "")))]),
  );
}

/** Whether a drawn layer has a value in some row: a chart with none draws no plot. */
export function hasValues(panel: Pick<ReportPanel, "tracks">): boolean {
  return panel.tracks.some((track) =>
    drawnLayers(track).some((layer) => {
      if (layer.mark === "markers") return (layer.markers ?? []).length > 0;
      if (layer.mark === "spans") return layer.rows.length > 0;
      const fields = valueFields(layer);
      return layer.rows.some((row) => fields.some((f) => num(row, f.name) !== null));
    }),
  );
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

/** The narrowest a span is drawn, in pixels, so a span of one instant (a site change, an alarm) shows as a tick. */
const MIN_SPAN_WIDTH = 2;

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
      width: Math.max(MIN_SPAN_WIDTH, x(to) - x(from)),
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
 * A layer's rows under the pointer at `raw`: the spans that cover it, and a span shorter than a marker's tolerance
 * within that tolerance of it; for a line or area of step fields
 * the last row of each series at or before it, which is the value the step draws there; for a layer
 * along days the day that holds it, on a day or a time axis alike; on a gridded panel the rows on that
 * slot; else every series' row at the nearest x, within the match's tolerance.
 */
export function rowsAt(layer: ReportLayer, raw: number, match: RowMatch): ReportRow[] {
  if (layer.mark === "spans") {
    const { start, end } = layer.bindings;
    return layer.rows.filter((r) => {
      const from = num(r, start) ?? Infinity;
      const to = num(r, end) ?? -Infinity;
      const pad = to - from < match.markerTolerance ? match.markerTolerance / 2 : 0;
      return from - pad <= raw && raw <= to + pad;
    });
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
  if (layer.xKind === "day") return placed.filter((r) => num(r, xName)! <= raw && raw < num(r, xName)! + DAY);
  if (match.gridded) return placed.filter((r) => num(r, xName) === raw);
  const tolerance = layer.mark === "markers" ? match.markerTolerance : match.tolerance;
  const nearest = num(nearestBy(sortedBy(placed, xName), (r) => num(r, xName)!, raw), xName)!;
  if (Math.abs(nearest - raw) > tolerance) return [];
  return placed.filter((r) => num(r, xName) === nearest);
}

/** One row of the tooltip: a layer's value at the pointer, its range where it is a band, and the API's sentences about it. */
export interface TipEntry {
  key: string;
  label: string;
  value: string;
  /** A band's middle half (or its one shaded span) at the pointer, "118–185"; null for every other mark. */
  range: string | null;
  color: string | null;
  /** The API's sentences about the layer's rows here, under its last entry. */
  notes: string[];
}

/** A value's words: with its unit, or the bare number for a range's ends. */
export type TipValue = (value: ReportRow[string], field: ReportField, unit: boolean) => string;

/**
 * What the tooltip lists for a layer at its `rows` under the pointer. A band of one set is one entry: the
 * layer's name, its middle line's value and its shaded span's ends, since its edges are parts of one band; a band of
 * edges only is its name and its outer ends.
 * Spans name their state; every other mark lists each drawn field. The rows' sentences sit under the
 * layer's last entry, so each says which layer it is about.
 */
export function tipEntries(layer: ReportLayer, rows: readonly ReportRow[], text: TipValue): TipEntry[] {
  const notes = rows.map(rowSentence).filter((s): s is string => s !== null);
  const withNotes = (entries: TipEntry[]) => (entries.length > 0 ? entries.map((e, i) => (i === entries.length - 1 ? { ...e, notes } : e)) : entries);
  if (layer.mark === "spans") {
    const name = layer.fields.find((f) => f.colorField)?.name ?? layer.bindings.category ?? "";
    const lane = layer.bindings.category ?? "";
    return withNotes(
      rows.map((row, i) => ({ key: `${layer.id}-${i}`, label: String(row[lane] ?? layer.title), value: String(row[name] ?? ""), range: null, color: null, notes: [] })),
    );
  }
  const title = layer.group ? `${layer.title} · ${layer.group}` : layer.title;
  const series = layer.bindings.series;
  const severalSeries = !!series && new Set(layer.rows.map((r) => r[series])).size > 1;
  const edges = layer.bindings.edges ?? [];
  const middle = boundFields(layer, layer.bindings.y)[0];
  if (layer.mark === "band" && !severalSeries && middle && (edges.length === 2 || edges.length === 4)) {
    const [low, high] = boundFields(layer, edges.length === 4 ? [edges[1], edges[2]] : edges);
    return withNotes(
      rows
        .filter((row) => row[middle.name] !== null && row[middle.name] !== undefined)
        .map((row, i) => ({
          key: `${layer.id}-${i}`,
          label: title,
          value: text(row[middle.name], middle, true),
          range: low && high && row[low.name] != null && row[high.name] != null ? `${text(row[low.name], low, false)}–${text(row[high.name], high, false)}` : null,
          color: fieldColor(layer, middle),
          notes: [],
        })),
    );
  }
  if (layer.mark === "band" && !severalSeries && !middle && (edges.length === 2 || edges.length === 4)) {
    const [low, high] = boundFields(layer, edges.length === 4 ? [edges[0], edges[3]] : edges);
    if (!low || !high) return [];
    return withNotes(
      rows
        .filter((row) => row[low.name] != null && row[high.name] != null)
        .map((row, i) => ({
          key: `${layer.id}-${i}`,
          label: title,
          value: `${text(row[low.name], low, false)}–${text(row[high.name], high, true)}`,
          range: null,
          color: fieldColor(layer, low),
          notes: [],
        })),
    );
  }
  const fields = layer.mark === "markers" ? layer.fields.filter((f) => (layer.bindings.y ?? []).includes(f.name)) : valueFields(layer);
  return withNotes(
    rows.flatMap((row, i) =>
      fields
        .filter((f) => row[f.name] !== null && row[f.name] !== undefined)
        .map((field) => ({
          key: `${layer.id}-${i}-${field.name}`,
          label: [title, severalSeries ? String(row[series!] ?? "") : null, fields.length > 1 ? field.label : null].filter(Boolean).join(" · "),
          value: text(row[field.name], field, true),
          range: null,
          color: fieldColor(layer, field),
          notes: [],
        })),
    ),
  );
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

/** One part of a band's legend entry: its middle line or one of its shaded spans, in the API's words. */
export interface LegendPart {
  key: string;
  label: string;
  shape: "line" | "fill";
  /** Null for a middle line coloured by glucose, which has no one colour. */
  color: string | null;
}

/**
 * What a band of one set draws, part by part: its middle line, then each shaded span inner first, each
 * in the colour it is drawn in. None for a band of several sets, whose sets the entry's title names.
 */
export function bandParts(layer: ReportLayer): LegendPart[] {
  const sets = bandSets(layer);
  if (layer.mark !== "band" || sets.length !== 1) return [];
  const [set] = sets;
  const middle = boundFields(layer, layer.bindings.y)[0];
  const spans = layer.spans ?? [];
  const parts: LegendPart[] = [];
  if (middle) parts.push({ key: "middle", label: middle.label, shape: "line", color: glucoseColoured(layer, middle) ? null : set.line });
  spans.forEach((span, i) =>
    parts.push({ key: `span-${i}`, label: span.label, shape: "fill", color: i === 0 && (spans.length > 1 || middle) ? set.inner : set.outer }),
  );
  return parts;
}

/** A legend entry: the layer's words and colour; no colour for a line coloured by glucose, which has none. */
export interface LegendEntry {
  id: string;
  title: string;
  unit: string | null;
  color: string | null;
  /** The hatch the layer's fill carries over its colour, shown in the swatch. */
  texture: TextureKey | null;
  mark: ReportLayer["mark"];
  /** How the layer was folded onto the grid, or null. */
  context: string | null;
  /** The group the layer's rows are, where the chart draws more than one; else null. */
  group: string | null;
  /** A band's middle line and shaded spans, each with its swatch; empty for every other mark. */
  parts: LegendPart[];
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
        texture: fillTexture(layer),
        mark: layer.mark,
        context: layer.context ?? null,
        group: layer.group ?? null,
        parts: bandParts(layer),
      };
    }),
  );
}

/**
 * The panels in runs: each run of consecutive panels with the same `group` is one chart's small
 * multiples; every other panel is a run of its own.
 */
export function panelRuns<P extends Pick<ReportPanel, "group">>(panels: readonly P[]): P[][] {
  const runs: P[][] = [];
  for (const panel of panels) {
    const last = runs.at(-1);
    if (last && panel.group && last[0].group === panel.group) last.push(panel);
    else runs.push([panel]);
  }
  return runs;
}
