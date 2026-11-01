import type { ReportField, ReportLayer, ReportPanel, ReportRow } from "../types.js";

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

/**
 * A category chart's bars layers as one set of bars for its image, which has room for one chart:
 * each layer's fields side by side in each category, named by the layer where there are several.
 */
export function mergedBars(layers: readonly ReportLayer[]): Pick<ReportLayer, "fields" | "rows" | "bindings"> | null {
  if (layers.length <= 1) return layers[0] ?? null;
  const first = layers[0];
  const xField = first.fields.find((f) => f.name === first.bindings.x);
  const fields: ReportField[] = [{ name: "category", label: xField?.label ?? "", kind: "category" }];
  const rows = new Map<string, ReportRow>();
  layers.forEach((layer, i) => {
    const xName = layer.bindings.x ?? "";
    const own = (layer.bindings.y ?? []).map((n) => layer.fields.find((f) => f.name === n)).filter((f): f is ReportField => f !== undefined);
    for (const field of own)
      fields.push({ ...field, name: `${i}.${field.name}`, label: `${layer.title} · ${field.label}`, color: layer.color ?? field.color });
    for (const row of layer.rows) {
      const category = row[xName];
      if (category === null || category === undefined) continue;
      const merged = rows.get(String(category)) ?? { category: String(category) };
      for (const field of own) merged[`${i}.${field.name}`] = row[field.name];
      rows.set(String(category), merged);
    }
  });
  return {
    fields,
    rows: [...rows.values()],
    bindings: { x: "category", y: fields.slice(1).map((f) => f.name), orientation: first.bindings.orientation },
  };
}
