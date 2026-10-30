import { z } from "zod";
import type { LegendToggle } from "../options.js";
import type { ReportResult } from "./types.js";

/**
 * The API's report result (Nocturne.API/Mcp/Reports/ReportResult.cs), as the MCP tools'
 * `structuredContent` and the custom report run carry it. Checked only as deep as the view
 * branches on; the rows, fields and chart payload are drawn as they come.
 */
const reportResultSchema = z.object({
  spec: z.object({ version: z.number(), title: z.string().nullish() }),
  units: z.enum(["mg/dl", "mmol"]),
  summary: z.string(),
  warnings: z.array(z.string()).default([]),
  openUrl: z.string().nullish(),
  panels: z.array(
    z.object({
      id: z.string(),
      dataset: z.string(),
      primitive: z.enum(["time_series", "stat_tiles", "table", "glucose_chart", "range_bands", "bar"]),
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

/** The report `value` carries, or null when it carries none a report view can draw. */
export function readReport(value: unknown): ReportResult | null {
  const parsed = reportResult.safeParse(value);
  return parsed.success ? parsed.data : null;
}

/**
 * The legend toggles for tracks the caller could not read, mirroring the API's
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
