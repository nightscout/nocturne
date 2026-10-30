import { z } from "zod";
import type { LegendToggle, ReportResult } from "@nocturne/charts/core";

/**
 * `structuredContent` of the API's `render_report` and `show_glucose` tools
 * (Nocturne.API/Mcp/Reports/ReportResult.cs). Checked only as deep as the view branches on; the
 * rows, fields and chart payload are drawn as they come.
 */
const reportResultSchema = z.object({
  spec: z.object({ version: z.number(), title: z.string().nullish() }),
  units: z.enum(["mg/dl", "mmol"]),
  summary: z.string(),
  warnings: z.array(z.string()).default([]),
  panels: z.array(
    z.object({
      id: z.string(),
      dataset: z.string(),
      primitive: z.enum(["time_series", "stat_tiles", "table", "glucose_chart", "range_bands"]),
      status: z.enum(["ok", "not_permitted", "timed_out", "failed"]),
      window: z.object({ from: z.number(), to: z.number() }),
      fields: z.array(z.object({ name: z.string(), label: z.string() }).passthrough()).default([]),
      rows: z.array(z.record(z.string(), z.unknown())).default([]),
      summary: z.string(),
      omittedScopes: z.array(z.string()).default([]),
    }).passthrough(),
  ),
});

const reportResult = z.custom<ReportResult>((value) => reportResultSchema.safeParse(value).success);

/** The report a tool result carries, or null when it carries none this view can draw. */
export function readReport(structuredContent: unknown): ReportResult | null {
  const parsed = reportResult.safeParse(structuredContent);
  return parsed.success ? parsed.data : null;
}

/** A range as the report spec sends it. */
export type SpecRange = { hours: number } | { from: string; to: string };

/** One event cohort as the spec carries it; the view sends it back as it came. */
export interface SpecCohort {
  anchor: string;
  [key: string]: unknown;
}

/** A refine_report patch: a new range for every panel, or per panel a range or dataset parameters. */
export interface ReportPatch {
  range?: SpecRange;
  panels?: { id: string; range?: SpecRange; dataset?: Record<string, unknown> }[];
}

const specPanels = z.object({
  panels: z.array(
    z.object({
      id: z.string(),
      dataset: z.object({ cohorts: z.array(z.object({ anchor: z.string() }).passthrough()).nullish() }).passthrough(),
    }).passthrough(),
  ),
});

/** The cohorts an event_response panel of the spec names, as sent; one meal cohort when it names none, as the API reads it. */
export function cohortsOf(spec: unknown, panelId: string): SpecCohort[] {
  const parsed = specPanels.safeParse(spec);
  if (!parsed.success) return [];
  const panel = parsed.data.panels.find((p) => p.id === panelId);
  if (!panel) return [];
  return panel.dataset.cohorts ?? [{ anchor: "Meal" }];
}

/** The range that reaches as far again before the panel's window, to the same end. */
export function earlierRange(window: { from: number; to: number }): SpecRange {
  const span = window.to - window.from;
  return { from: new Date(window.from - span).toISOString(), to: new Date(window.to).toISOString() };
}

/**
 * The legend toggles for tracks the token could not read, mirroring the API's
 * ChartDataReadScopeGuard: activity spans are exercise (treatments) or sleep, so they go only
 * when both are missing.
 */
export function omittedToggles(omittedScopes: readonly string[]): LegendToggle[] {
  const missing = new Set(omittedScopes);
  const toggles: LegendToggle[] = [];
  if (missing.has("treatments.read")) toggles.push("basal", "iob", "cob", "bolus", "carbs", "overrideSpans");
  if (missing.has("devices.read")) toggles.push("pumpModes");
  if (missing.has("therapy.read")) toggles.push("profileSpans");
  if (missing.has("treatments.read") && missing.has("sleep.read")) toggles.push("activitySpans");
  return toggles;
}
