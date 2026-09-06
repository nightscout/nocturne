/**
 * Shared source of truth for the glucose-icon look: a rounded-rect tile filled
 * with the status color, BG value centered on top. The web app favicon
 * (`title-favicon-service`) and the desktop companion tray icon both mirror it.
 *
 * Colors are passed in by the caller (it does NOT resolve CSS variables) so the
 * renderer stays reusable across contexts.
 */

/** Status levels for glucose values. */
export type GlucoseStatus = "very-high" | "high" | "in-range" | "low" | "very-low";

export interface GlucoseThresholds {
  high: number;
  low: number;
  targetTop: number;
  targetBottom: number;
}

export function getGlucoseStatus(value: number, t: GlucoseThresholds): GlucoseStatus {
  if (value >= t.high) return "very-high";
  if (value <= t.low) return "very-low";
  if (value > t.targetTop) return "high";
  if (value < t.targetBottom) return "low";
  return "in-range";
}

export interface GlucoseIconOptions {
  /** Text to render centered in the icon, or null to draw just the background. */
  text: string | null;
  bgColor: string;
  fgColor: string;
  /** Square size in px. Defaults to 32. */
  size?: number;
  /** Corner radius in px. Defaults to 6. */
  radius?: number;
}

/**
 * Render a glucose icon to a PNG data URL. Returns "" when there is no DOM
 * (non-browser).
 */
export function renderGlucoseIcon(opts: GlucoseIconOptions): string {
  if (typeof document === "undefined") return "";

  const size = opts.size ?? 32;
  const radius = opts.radius ?? 6;

  const canvas = document.createElement("canvas");
  canvas.width = size;
  canvas.height = size;
  const ctx = canvas.getContext("2d");
  if (!ctx) return "";

  ctx.clearRect(0, 0, size, size);

  ctx.beginPath();
  ctx.roundRect(0, 0, size, size, radius);
  ctx.fillStyle = opts.bgColor;
  ctx.fill();

  if (opts.text !== null) {
    ctx.fillStyle = opts.fgColor;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";

    // Font shrinks as the value gets longer so it always fits.
    if (opts.text.length <= 2) {
      ctx.font = "bold 18px system-ui, sans-serif";
    } else if (opts.text.length <= 3) {
      ctx.font = "bold 14px system-ui, sans-serif";
    } else {
      ctx.font = "bold 11px system-ui, sans-serif";
    }

    ctx.fillText(opts.text, size / 2, size / 2 + 1);
  }

  return canvas.toDataURL("image/png");
}
