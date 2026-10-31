/**
 * Structural mirror of the API's report result (Nocturne.API/Reports/ReportResult.cs), as
 * dto.ts mirrors the dashboard chart. Every value, unit, label and colour arrives in it.
 */
import type { ChartColor } from "../enums.js";
import type { DashboardChartDataDto } from "../dto.js";
import type { ChartFormat } from "../format.js";

export type ReportPrimitive = "time_series" | "stat_tiles" | "table" | "glucose_chart" | "range_bands" | "bar" | "comparison";

/** A treatment marker the dashboard chart draws. */
export type ReportMarkerKind = "bolus" | "carbs";

/** `offset` is minutes from an event, `time_of_day` minutes after local midnight. */
export type ReportFieldKind = "time" | "number" | "category" | "percent" | "offset" | "time_of_day";

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
  /** Drawn on a time series as the dashboard's bolus or carb marker at each value, not as a line. */
  marker?: ReportMarkerKind | null;
  /** The value holds until the next row: a line of it is drawn as steps. */
  step?: boolean;
  /** The field each row carries its bar colour in (a theme colour name), for a bar of this category. */
  colorField?: string | null;
}

/** A treatment marker on a time series panel: its time, kind and amount (U or g). */
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

export interface ReportBindings {
  x?: string | null;
  y?: string[] | null;
  band?: string[] | null;
  columns?: string[] | null;
  /** range_bands: the category field whose values are drawn as separate bands. */
  series?: string | null;
  /** bar: stack the y fields in one bar per category. */
  stacked?: boolean | null;
  /** bar: bars up from a category axis along the bottom (vertical, the default), or across from one down the side. */
  orientation?: "vertical" | "horizontal" | null;
}

export interface ReportResolution {
  requestedIntervalMinutes: number;
  intervalMinutes: number;
  sourceRows: number;
  rows: number;
  downsampled: boolean;
}

export type ReportPanelStatus = "ok" | "not_permitted" | "timed_out" | "failed";

export interface ReportPanel {
  id: string;
  title?: string | null;
  dataset: string;
  primitive: ReportPrimitive;
  bindings: ReportBindings;
  status: ReportPanelStatus;
  window: { from: number; to: number };
  fields: ReportField[];
  rows: ReportRow[];
  thresholds?: ReportThresholds | null;
  chart?: DashboardChartDataDto | null;
  omittedScopes?: string[];
  summary: string;
  /** The range was cut to the last 24 hours, all a history-clamped caller reads. */
  limited?: boolean;
  omitted?: { field: string; scope: string }[];
  resolution?: ReportResolution | null;
  /** The web report that shows the panel's data, with its range and parameters. */
  link?: string | null;
  /** Treatment markers a time series panel draws on its line. */
  markers?: ReportMarker[];
  /** A band panel's spans, inner first: the API's words for each and its edge fields. */
  spans?: { label: string; low: string; high: string }[];
  /** At most six figures the API picked for the panel, label above value. */
  facts?: ReportFact[];
  /** The API's one sentence under the facts; for a panel that was not drawn, why. */
  sentence?: string | null;
  /** The panel's range and how much it read, as one line. */
  context?: string | null;
  /** The range is the longest the dataset covers drawn so: no earlier data can be added. */
  atRangeLimit?: boolean;
  /** The API's sentence saying how long a range the panel covers, when it is at that limit. */
  rangeLimit?: string | null;
  /** Every event cohort the panel asked for, with the API's label naming its filter. */
  cohorts?: ReportCohort[];
  /** A glucose chart of event cohorts: single events' glucose, drawn faintly under each cohort's band. */
  traces?: ReportTrace[];
}

/** One event's glucose over the window, aligned on its anchor: [minutes from it, glucose] pairs. */
export interface ReportTrace {
  cohort: number;
  at: number;
  points: [number, number][];
  sentence: string;
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

/** The fields a time series draws, looked up by name in the panel's catalogue entries. */
export function boundFields(panel: Pick<ReportPanel, "fields">, names: readonly string[] | null | undefined): ReportField[] {
  return (names ?? []).flatMap((name) => panel.fields.filter((f) => f.name === name));
}

/** Minutes after midnight as a clock time in the locale's format; the date is arbitrary and not shown. */
export function formatMinuteOfDay(minute: number, format: ChartFormat): string {
  if (!Number.isFinite(minute)) return "";
  const m = ((Math.round(minute) % 1440) + 1440) % 1440;
  return new Intl.DateTimeFormat(format.locale, { hour: "numeric", minute: "2-digit", hour12: format.hour12, timeZone: "UTC" }).format(
    Date.UTC(2000, 0, 1, Math.floor(m / 60), m % 60),
  );
}

/** Minutes from an event, signed: "-30", "0", "+90". */
export function formatOffset(minutes: number, format: ChartFormat): string {
  if (!Number.isFinite(minutes)) return "";
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
  if (field.kind === "time_of_day" && typeof value === "number") return formatMinuteOfDay(value, format);
  if (field.kind === "offset" && typeof value === "number") return formatOffset(value, format);
  if (typeof value === "number") return new Intl.NumberFormat(format.locale, { maximumFractionDigits: 2 }).format(value);
  return String(value);
}
