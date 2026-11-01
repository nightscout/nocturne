import type { ReportPanel } from "../types.js";

/**
 * Whether a panel has a static mode: a chart or a comparison. Stat tiles and tables are text, which
 * the report's summary already carries, so they are not drawn as images.
 */
export function staticPanel(panel: Pick<ReportPanel, "kind">): boolean {
  return panel.kind === "chart" || panel.kind === "comparison";
}

/** An image's height when the caller names none: one stacked bar is a strip, a comparison lists its figures, a chart grows with its tracks. */
export function staticDefaultHeight(panel: Pick<ReportPanel, "kind" | "tracks">): number {
  if (panel.kind === "comparison") return 800;
  const layers = panel.tracks.flatMap((t) => t.layers);
  const first = layers[0];
  if (layers.length === 1 && first.mark === "bars" && first.bindings.stacked === true
    && first.bindings.orientation === "horizontal" && first.rows.length === 1)
    return 240;
  const weight = panel.tracks.reduce((sum, t) => sum + t.height, 0);
  return Math.round(Math.min(1200, Math.max(600, 300 + 240 * weight)));
}
