import type { GradientStop } from "../view/glucose-gradient-stops.js";
import { cssColorToHex, type CustomProperties } from "./css-color.js";

/**
 * The theme a chart drawn with no stylesheet (a canvas on the server) paints in: every custom
 * property the chart theme declares for one colour scheme, unresolved, so a colour expression the
 * live chart writes (`var(--glucose-low)`, `oklch(from var(--chart-1) l c h / 0.35)`) is evaluated
 * to the colour a browser computes for it.
 */
export interface StaticTheme {
  mode: "light" | "dark";
  variables: CustomProperties;
  /** The family every label is set in; the host registers it with its canvas. */
  fontFamily: string;
}

/** The colour `value` (any expression the charts write) paints in `theme`, as hex; `fallback` when it is none. */
export function paint(theme: StaticTheme, value: string | null | undefined, fallback = "var(--muted-foreground)"): string {
  const resolved = value ? cssColorToHex(value, theme.variables) : null;
  return resolved ?? cssColorToHex(fallback, theme.variables) ?? "#808080";
}

/**
 * What a primitive needs to draw itself onto a canvas rather than into the page: the theme, the
 * image's size in CSS pixels, and layerchart's capture target, which the host hands to
 * `renderCapturedChart`. A static chart has no tooltip, highlight or HTML legend; its words are drawn
 * on the canvas.
 */
export interface StaticRender {
  theme: StaticTheme;
  width: number;
  height: number;
  /** Filled by the chart's canvas layer during the server render. */
  capture: object;
  /**
   * A vertical gradient on the host's canvas, from `y0` to `y1` in the plot's coordinates, for a line
   * coloured by glucose. Without it such a line is drawn in the in-range colour.
   */
  gradient?: (y0: number, y1: number, stops: GradientStop[]) => CanvasGradient;
  /** The words above a chart that is not a report panel (which brings its own title and range line). */
  title?: string;
  context?: string | null;
}

/** The label sizes and colours of a static chart, in the theme. */
export function staticText(theme: StaticTheme) {
  return {
    font: `font-family: ${theme.fontFamily}`,
    strong: `font-family: ${theme.fontFamily}; font-weight: 600`,
    foreground: paint(theme, "var(--foreground)"),
    muted: paint(theme, "var(--muted-foreground)"),
    border: paint(theme, "var(--border)"),
    background: paint(theme, "var(--background)"),
  };
}

/** A canvas gradient where layerchart's `Path` types its stroke as a colour string; on a canvas it paints the gradient as given. */
export function gradientStroke(gradient: CanvasGradient): string {
  // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- layerchart's canvas Path paints any stroke it is given; only its type says string
  return gradient as unknown as string;
}
