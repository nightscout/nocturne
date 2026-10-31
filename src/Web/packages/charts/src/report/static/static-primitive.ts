import type { ReportPanel } from "../types.js";

/**
 * Whether a panel is a chart that has a static mode. Stat tiles and tables are text, which the
 * report's summary already carries, so they are not drawn as images.
 */
export function staticPrimitive(panel: Pick<ReportPanel, "primitive">): boolean {
  return ["time_series", "range_bands", "bar", "comparison", "glucose_chart"].includes(panel.primitive);
}

/** An image's height when the caller names none: one stacked bar is a strip, a comparison lists its figures. */
export function staticDefaultHeight(panel: Pick<ReportPanel, "primitive" | "bindings" | "rows">): number {
  if (panel.primitive === "bar" && panel.bindings.stacked === true && panel.bindings.orientation === "horizontal" && panel.rows.length === 1)
    return 240;
  if (panel.primitive === "comparison") return 800;
  return 600;
}
