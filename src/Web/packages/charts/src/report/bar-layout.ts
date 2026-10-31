import type { ScaleBand } from "d3-scale";
import { resolveChartColor } from "../colors.js";
import { isChartColor } from "../enums.js";
import type { GlucoseRange } from "../print/patterns.js";
import { boundFields, type ReportField, type ReportPanel } from "./types.js";

/** One drawn piece of a bar: its category, the field it shows, where it starts and ends, and its colour. */
export interface BarSegment {
  key: string;
  category: string;
  field: ReportField;
  /** Its place among the category's side-by-side bars; 0 for every segment of a stack. */
  slot: number;
  start: number;
  end: number;
  value: number;
  color: string;
  /** The print texture of a glucose range's bar, so paper still tells the ranges apart. */
  pattern: GlucoseRange | null;
}

export interface BarLayout {
  categories: string[];
  fields: ReportField[];
  segments: BarSegment[];
  stacked: boolean;
  horizontal: boolean;
  /** The top of the value axis. */
  max: number;
}

/** The print texture of each glucose range's theme colour. */
export const GLUCOSE_PATTERNS: Partial<Record<string, GlucoseRange>> = {
  "glucose-very-low": "very-low",
  "glucose-low": "low",
  "glucose-in-range": "in-range",
  "glucose-high": "high",
  "glucose-very-high": "very-high",
};

/** A bar that neither the row nor the field gives a colour: chrome, not a clinical colour. */
export const NEUTRAL_BAR = "var(--muted-foreground)";

/**
 * A bar panel's segments. Every colour comes from the panel: a category whose field names a colour
 * field (glucose bands) colours its one bar from that row's value; otherwise each y field's bar takes
 * the field's theme colour; with neither, or with a value that is not a theme colour, the bar is neutral.
 */
export function barLayout(panel: Pick<ReportPanel, "fields" | "rows" | "bindings">): BarLayout {
  const horizontal = panel.bindings.orientation === "horizontal";
  const xName = panel.bindings.x ?? "";
  const xField = panel.fields.find((f) => f.name === xName);
  const fields = boundFields(panel, panel.bindings.y);
  const stacked = panel.bindings.stacked === true;
  const rowColor = fields.length === 1 && xField?.colorField ? xField.colorField : null;

  const categories: string[] = [];
  const segments: BarSegment[] = [];
  let max = 0;
  for (const row of panel.rows) {
    const category = row[xName];
    if (category === null || category === undefined) continue;
    const name = String(category);
    categories.push(name);
    let base = 0;
    fields.forEach((field, i) => {
      const value = row[field.name];
      if (typeof value !== "number") return;
      const fromRow = rowColor ? row[rowColor] : null;
      // Only a theme colour the API names may reach a CSS variable; anything else is drawn neutral.
      const theme = isChartColor(fromRow) ? fromRow : rowColor === null && isChartColor(field.color) ? field.color : null;
      const color = theme ? resolveChartColor(theme) : NEUTRAL_BAR;
      const start = stacked ? base : 0;
      const end = start + value;
      if (stacked) base = end;
      max = Math.max(max, end);
      segments.push({ key: `${name}-${field.name}`, category: name, field, slot: stacked ? 0 : i, start, end, value, color,
        pattern: theme ? (GLUCOSE_PATTERNS[theme] ?? null) : null });
    });
  }
  return { categories, fields, segments, stacked, horizontal, max: max > 0 ? max * 1.05 : 1 };
}

function isBandScale(scale: unknown): scale is ScaleBand<string> {
  return typeof scale === "function" && "bandwidth" in scale && typeof scale.bandwidth === "function";
}

/** The band scale a bar chart was given, recovered from layerchart's context type. */
export function bandScale(scale: unknown): ScaleBand<string> {
  if (isBandScale(scale)) return scale;
  throw new TypeError("Expected a band scale");
}

/** The value scale, recovered from layerchart's context type. */
export function valueScale(scale: unknown): (value: number) => number {
  if (typeof scale === "function") return (value: number) => Number(scale(value));
  throw new TypeError("Expected a scale");
}
