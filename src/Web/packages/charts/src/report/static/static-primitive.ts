import type { ReportPanel } from "../types.js";

/**
 * Whether a panel is a chart that has a static mode. Stat tiles and tables are text, which the
 * report's summary already carries, so they are not drawn as images.
 */
export function staticPrimitive(panel: Pick<ReportPanel, "primitive">): boolean {
  return ["time_series", "range_bands", "bar", "comparison", "glucose_chart"].includes(panel.primitive);
}
