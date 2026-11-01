import { z } from "zod";
import type { ReportResult } from "./types.js";

/**
 * The API's report result (Nocturne.API/Reports/ReportResult.cs), as the custom report run carries
 * it. Checked only as deep as the view branches on; the rows and fields are drawn as they come.
 */
const reportResultSchema = z.object({
  spec: z.object({ version: z.number(), title: z.string().nullish() }),
  units: z.enum(["mg/dl", "mmol"]),
  summary: z.string(),
  warnings: z.array(z.string()).default([]),
  panels: z.array(
    z.object({
      id: z.string(),
      dataset: z.string().nullish(),
      kind: z.enum(["chart", "stat_tiles", "table", "comparison"]),
      status: z.enum(["ok", "not_permitted", "timed_out", "failed"]),
      window: z.object({ from: z.number(), to: z.number() }),
      fields: z.array(z.object({ name: z.string(), label: z.string() }).passthrough()).default([]),
      rows: z.array(z.record(z.string(), z.unknown())).default([]),
      summary: z.string(),
      tracks: z.array(
        z.object({
          id: z.string(),
          height: z.number(),
          layers: z.array(
            z.object({
              id: z.string(),
              title: z.string(),
              mark: z.enum(["line", "area", "band", "bars", "points", "markers", "spans"]),
              status: z.enum(["ok", "not_permitted", "timed_out", "failed"]),
              side: z.enum(["left", "right", "none"]),
              fields: z.array(z.object({ name: z.string(), label: z.string() }).passthrough()).default([]),
              rows: z.array(z.record(z.string(), z.unknown())).default([]),
            }).passthrough(),
          ),
        }).passthrough(),
      ).default([]),
    }).passthrough(),
  ),
});

const reportResult = z.custom<ReportResult>((value) => reportResultSchema.safeParse(value).success);

/** The report `value` carries, or null when it carries none a report view can draw. */
export function readReport(value: unknown): ReportResult | null {
  const parsed = reportResult.safeParse(value);
  return parsed.success ? parsed.data : null;
}
