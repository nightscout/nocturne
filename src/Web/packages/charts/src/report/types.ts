/**
 * Structural mirror of the API's report result (Nocturne.API/Mcp/Reports/ReportResult.cs), as
 * dto.ts mirrors the dashboard chart. Every value, unit, label and colour arrives in it.
 */
import type { ChartColor } from "../enums.js";
import type { DashboardChartDataDto } from "../dto.js";
import type { ChartFormat } from "../format.js";

export type ReportPrimitive = "time_series" | "stat_tiles" | "table" | "glucose_chart" | "range_bands";

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
}

export interface ReportResolution {
  requestedIntervalMinutes: number;
  intervalMinutes: number;
  sourceRows: number;
  rows: number;
  downsampled: boolean;
}

export type ReportPanelStatus = "ok" | "not_permitted" | "timed_out";

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
}

/** The fields a time series draws, looked up by name in the panel's catalogue entries. */
export function boundFields(panel: Pick<ReportPanel, "fields">, names: readonly string[] | null | undefined): ReportField[] {
  return (names ?? []).flatMap((name) => panel.fields.filter((f) => f.name === name));
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
  if (field.kind === "time_of_day" && typeof value === "number") return formatMinuteOfDay(value, format);
  if (field.kind === "offset" && typeof value === "number") return formatOffset(value, format);
  if (typeof value === "number") return new Intl.NumberFormat(format.locale, { maximumFractionDigits: 2 }).format(value);
  return String(value);
}
