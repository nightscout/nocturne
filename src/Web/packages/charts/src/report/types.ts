/**
 * Structural mirror of the API's report result (Nocturne.API/Mcp/Reports/ReportResult.cs), as
 * dto.ts mirrors the dashboard chart. Every value, unit, label and colour arrives in it.
 */
import type { ChartColor } from "../enums.js";
import type { DashboardChartDataDto } from "../dto.js";
import type { ChartFormat } from "../format.js";

export type ReportPrimitive = "time_series" | "stat_tiles" | "table" | "glucose_chart";

export type ReportFieldKind = "time" | "number" | "category" | "percent";

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
}

export interface ReportResult {
  spec: { version: number; title?: string | null };
  units: "mg/dl" | "mmol";
  /** How every glucose line in the report is coloured; one value per report. */
  glucoseColorMode?: "continuous" | "threshold";
  panels: ReportPanel[];
  summary: string;
  warnings?: string[];
}

/** The fields a time series draws, looked up by name in the panel's catalogue entries. */
export function boundFields(panel: Pick<ReportPanel, "fields">, names: readonly string[] | null | undefined): ReportField[] {
  return (names ?? []).flatMap((name) => panel.fields.filter((f) => f.name === name));
}

/** A field's value as printed: numbers in the locale's format, times as local date and time. */
export function formatReportValue(
  value: ReportRow[string],
  field: ReportField,
  format: ChartFormat,
): string {
  if (value === null || value === undefined || value === "") return "–";
  if (field.kind === "time" && typeof value === "number")
    return new Intl.DateTimeFormat(format.locale, { dateStyle: "medium", timeStyle: "short", hour12: format.hour12 }).format(value);
  if (typeof value === "number") return new Intl.NumberFormat(format.locale, { maximumFractionDigits: 2 }).format(value);
  return String(value);
}
