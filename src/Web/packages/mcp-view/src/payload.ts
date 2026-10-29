import { z } from "zod";
import type { DashboardChartDataDto } from "@nocturne/charts/core";

/**
 * `structuredContent` of the API's `show_glucose` tool (Nocturne.API/Mcp/Tools/GlucoseTools.cs).
 * `data` is the API's DashboardChartData, redacted to the token's scopes.
 */
export const glucoseReportSchema = z.object({
  kind: z.literal("glucose"),
  units: z.enum(["mg/dl", "mmol"]),
  window: z.object({ from: z.number(), to: z.number() }),
  omittedTracks: z.array(z.string()).default([]),
  data: z.custom<DashboardChartDataDto>((value) => typeof value === "object" && value !== null),
});

export type GlucoseReport = z.infer<typeof glucoseReportSchema>;

/** The report a tool result carries, or null when it carries none this view can draw. */
export function readGlucoseReport(structuredContent: unknown): GlucoseReport | null {
  const parsed = glucoseReportSchema.safeParse(structuredContent);
  return parsed.success ? parsed.data : null;
}
