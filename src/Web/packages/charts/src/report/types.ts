/**
 * Structural mirror of the API's report result (Nocturne.API/Reports/ReportResult.cs). Every value,
 * unit, label and colour arrives in it.
 */
import type { ChartColor } from "../enums.js";
import type { ChartFormat } from "../format.js";

export type ReportPanelKind = "chart" | "stat_tiles" | "table" | "comparison";

/** How a chart layer is drawn. */
export type ReportMark = "line" | "area" | "band" | "bars" | "points" | "markers" | "spans";

/** What a chart's horizontal axis holds; every layer of a chart shares it. */
export type ReportXKind = "time" | "offset" | "time_of_day" | "day" | "category" | "value" | "lag";

/** A treatment glyph the dashboard chart draws. */
export type ReportMarkerKind = "bolus" | "carbs" | "bg_check" | "basal_injection";

/**
 * `offset` is minutes from an event, `time_of_day` minutes after local midnight, `day` a local day's
 * midnight, `value` a scatter's first source and `lag` minutes one source is shifted against another.
 */
export type ReportFieldKind = "time" | "number" | "category" | "percent" | "offset" | "time_of_day" | "day" | "value" | "lag";

export interface ReportField {
  name: string;
  label: string;
  kind: ReportFieldKind;
  /** Printed after the value; null for none. */
  unit?: string | null;
  description?: string;
  scope?: string | null;
  /** The theme colour a line of the field is drawn in. */
  color?: ChartColor | null;
  /** Drawn on the glucose scale and coloured by the glucose thresholds. */
  glucoseScale?: boolean;
  /** The dashboard glyph a markers layer draws at each value. */
  marker?: ReportMarkerKind | null;
  /** The value holds until the next row: a line of it is drawn as steps. */
  step?: boolean;
  /** The field each row carries its colour in (a theme colour name), for a bar or a span of this category. */
  colorField?: string | null;
  /** How the field folds onto a chart's grid. */
  aggregate?: "sum" | "last" | "mean" | "max" | null;
  /**
   * A `CHART_TEXTURES` name the field's bars, area or band are hatched with, shown whether or not
   * patterns are on: the API gives one when the field shares its colour and mark with an earlier
   * series of its track, so the two differ by more than colour.
   */
  texture?: string | null;
}

/** A treatment marker of a markers layer: its time, kind and amount (U, g or glucose). */
export interface ReportMarker {
  time: number;
  kind: ReportMarkerKind;
  amount: number;
  /** A bolus the algorithm gave, drawn outlined. */
  automatic?: boolean;
}

export type ReportRow = Record<string, string | number | boolean | null | undefined>;

/** Glucose cut-points in the unit of the panel's glucose fields. */
export interface ReportThresholds {
  veryLow: number;
  low: number;
  high: number;
  veryHigh: number;
  /** mg/dL in one display unit: 1, or 18.0182 for mmol/L. Places the mg/dL glucose spectrum on the axis. */
  mgdlPerUnit: number;
}

/** A figures, table or comparison panel's columns. */
export interface ReportBindings {
  columns?: string[] | null;
}

/** Which fields a chart layer's mark draws. */
export interface ReportLayerBindings {
  x?: string | null;
  y?: string[] | null;
  /** band: two edges, or four (outer low, inner low, inner high, outer high). */
  edges?: string[] | null;
  /** band and line: the category whose values are drawn as separate sets. */
  series?: string | null;
  /** points: a field drawn as a line through the points. */
  fit?: string | null;
  stacked?: boolean | null;
  orientation?: "vertical" | "horizontal" | null;
  /** spans: where each span starts and ends, and the category that names its lane. */
  start?: string | null;
  end?: string | null;
  category?: string | null;
}

export interface ReportResolution {
  requestedIntervalMinutes: number;
  intervalMinutes: number;
  sourceRows: number;
  rows: number;
  downsampled: boolean;
}

export type ReportPanelStatus = "ok" | "not_permitted" | "timed_out" | "failed";

export type ReportAxisSide = "left" | "right" | "none";

/** A band's span: the API's words for it and its low and high edge fields. */
export interface ReportBandSpan {
  label: string;
  low: string;
  high: string;
}

/** One layer of a chart: its rows, its mark and the axis it is drawn on. */
export interface ReportLayer {
  id: string;
  /** The layer's name in the legend, the API's. */
  title: string;
  mark: ReportMark;
  bindings: ReportLayerBindings;
  xKind: ReportXKind;
  unit?: string | null;
  side: ReportAxisSide;
  status: ReportPanelStatus;
  window: { from: number; to: number };
  fields: ReportField[];
  rows: ReportRow[];
  /** A series hue the API gave the layer, in place of its fields' own colours. */
  color?: ChartColor | null;
  /** A `CHART_TEXTURES` name for the whole layer, as on a field ({@link ReportField.texture}); a field's own wins. */
  texture?: string | null;
  facts?: ReportFact[];
  sentence?: string | null;
  /** How the API folded the layer onto the panel's grid ("Basal rate, last value per 1 day"); null when drawn as read. */
  context?: string | null;
  summary?: string;
  /** A band's spans, inner first. */
  spans?: ReportBandSpan[];
  /** A markers layer's markers. */
  markers?: ReportMarker[];
  cohorts?: ReportCohort[];
  /** The days counted, on a day-filtered layer or table. */
  days?: ReportDayCounts | null;
  omitted?: { field: string; scope: string }[];
  resolution?: ReportResolution | null;
}

/** One track of a chart: its layers, over the chart's shared horizontal axis. */
export interface ReportTrack {
  id: string;
  title?: string | null;
  /** The track's share of the chart's height. */
  height: number;
  layers: ReportLayer[];
}

export interface ReportPanel {
  id: string;
  title?: string | null;
  /** The set the panel is one of: consecutive panels with the same group are one chart's small multiples. */
  group?: string | null;
  kind: ReportPanelKind;
  status: ReportPanelStatus;
  window: { from: number; to: number };
  /** A figures, table or comparison panel's dataset and columns. */
  dataset?: string | null;
  bindings?: ReportBindings | null;
  fields: ReportField[];
  rows: ReportRow[];
  /** A chart's horizontal axis, which its layers share. */
  xKind?: ReportXKind | null;
  /** A chart's grid in minutes: 0 for every reading, null for each dataset's own spacing. */
  grid?: number | null;
  tracks: ReportTrack[];
  thresholds?: ReportThresholds | null;
  summary: string;
  /** The range was cut to the last 24 hours, all a history-clamped caller reads. */
  limited?: boolean;
  omitted?: { field: string; scope: string }[];
  resolution?: ReportResolution | null;
  /** The web report that shows the panel's data, with its range and parameters. */
  link?: string | null;
  /** At most six figures the API picked for the panel, label above value. */
  facts?: ReportFact[];
  /** The API's one sentence under the facts; for a panel or a layer that was not drawn, why. */
  sentence?: string | null;
  /** The panel's range and how much it read, as one line. */
  context?: string | null;
  /** The range is the longest the panel's data covers: no earlier data can be added. */
  atRangeLimit?: boolean;
  /** The API's sentence saying how long a range the panel covers, when it is at that limit. */
  rangeLimit?: string | null;
  /** Every event cohort the panel asked for, with the API's label naming its filter. */
  cohorts?: ReportCohort[];
  /** The days counted, on a day-filtered layer or table. */
  days?: ReportDayCounts | null;
}

/** One figure under a panel: its words, its value as the API wrote it, its unit and an optional note. */
export interface ReportFact {
  label: string;
  value: string;
  unit?: string | null;
  note?: string | null;
}

/** An event cohort a panel asked for: its number (1 to 3) and the label that names its filter. */
export interface ReportCohort {
  number: number;
  label: string;
  /** Why the group's events are not shown; absent when they are. */
  omitted?: ReportCohortOmission | null;
}

/** Why a group of events is not shown: the reader's access lacks a scope, or another group stands in for it. */
export type ReportCohortOmission = { reason: "scope"; scope: string } | { reason: "sibling"; group: number };

/** The days a day-filtered layer or table counted: those kept, of all in the range, and why the rest are not. */
export interface ReportDayCounts {
  kept: number;
  total: number;
  matching: number;
  notMatching: number;
  /** Days only partly in the range, in neither group. */
  partial: number;
  /** Days before the history the reader's access reads, in neither group. */
  undecided: number;
}

/** The row field holding the API's one sentence about the row. */
export const SENTENCE_FIELD = "sentence";

/** The sentence the API wrote for a row, or null. */
export function rowSentence(row: ReportRow | null | undefined): string | null {
  const value = row?.[SENTENCE_FIELD];
  return typeof value === "string" && value !== "" ? value : null;
}

export interface ReportResult {
  spec: { version: number; title?: string | null };
  units: "mg/dl" | "mmol";
  /** How every glucose line in the report is coloured; one value per report. */
  glucoseColorMode?: "continuous" | "threshold";
  /** The tenant's IANA zone, the one the summaries state local times in; null for UTC. */
  timeZone?: string | null;
  panels: ReportPanel[];
  summary: string;
  warnings?: string[];
  /** The tenant's regional format (BCP-47), which an image writes dates and numbers in; null for the host's. */
  locale?: string | null;
  /** Whether the tenant reads a 12-hour clock; null for the host's. */
  hour12?: boolean | null;
}

/** The fields named, looked up in a panel's or a layer's catalogue entries. */
export function boundFields(owner: { fields: ReportField[] }, names: readonly string[] | null | undefined): ReportField[] {
  return (names ?? []).flatMap((name) => owner.fields.filter((f) => f.name === name));
}

/** Minutes after midnight as a clock time in the locale's format; the date is arbitrary and not shown. */
export function formatMinuteOfDay(minute: number, format: ChartFormat): string {
  const m = ((Math.round(minute) % 1440) + 1440) % 1440;
  return new Intl.DateTimeFormat(format.locale, { hour: "numeric", minute: "2-digit", hour12: format.hour12, timeZone: "UTC" }).format(
    Date.UTC(2000, 0, 1, Math.floor(m / 60), m % 60),
  );
}

/** Minutes from an event, signed: "-30", "0", "+90". */
export function formatOffset(minutes: number, format: ChartFormat): string {
  const text = new Intl.NumberFormat(format.locale, { maximumFractionDigits: 0 }).format(Math.abs(minutes));
  return minutes > 0 ? `+${text}` : minutes < 0 ? `-${text}` : text;
}

/** A field's value as printed: numbers in the locale's format, times as local date and time. */
export function formatReportValue(
  value: ReportRow[string],
  field: ReportField,
  format: ChartFormat,
): string {
  if (value === null || value === undefined || value === "") return "–";
  if (field.kind === "time" && typeof value === "number")
    return new Intl.DateTimeFormat(format.locale, { dateStyle: "medium", timeStyle: "short", hour12: format.hour12, timeZone: format.timeZone }).format(value);
  if (field.kind === "day" && typeof value === "number")
    return new Intl.DateTimeFormat(format.locale, { dateStyle: "medium", timeZone: format.timeZone }).format(value);
  if (field.kind === "time_of_day" && typeof value === "number") return formatMinuteOfDay(value, format);
  if ((field.kind === "offset" || field.kind === "lag") && typeof value === "number") return formatOffset(value, format);
  if (typeof value === "number") return new Intl.NumberFormat(format.locale, { maximumFractionDigits: 2 }).format(value);
  return String(value);
}
