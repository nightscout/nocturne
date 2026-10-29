import { z } from "zod";
import type { DashboardChartDataDto, LegendToggle } from "@nocturne/charts/core";

/**
 * `structuredContent` of the API's `show_glucose` tool (Nocturne.API/Mcp/Tools/GlucoseTools.cs).
 * `data` is the API's DashboardChartData, redacted to the token's scopes.
 */
export const glucoseReportSchema = z.object({
  kind: z.literal("glucose"),
  units: z.enum(["mg/dl", "mmol"]),
  window: z.object({ from: z.number(), to: z.number() }),
  /** The read scopes the token lacks; the tracks they govern are absent from `data`. */
  omittedScopes: z.array(z.string()).default([]),
  data: z.custom<DashboardChartDataDto>((value) => typeof value === "object" && value !== null),
});

export type GlucoseReport = z.infer<typeof glucoseReportSchema>;

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

/** The report a tool result carries, or null when it carries none this view can draw. */
export function readGlucoseReport(structuredContent: unknown): GlucoseReport | null {
  const parsed = glucoseReportSchema.safeParse(structuredContent);
  return parsed.success ? parsed.data : null;
}
