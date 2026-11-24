/** The main plot's padding. */
export interface PlotPadding {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

/**
 * Axis gutters. The desktop 48px each side is a quarter of a phone's width,
 * so a phone gets only what the tick labels need. Left: a three-digit or
 * one-decimal glucose value at 12px. Right: a one- or two-character
 * basal/IOB tick at 9px plus its 4px tick mark. The overview strip shares
 * the horizontal values so its brush lines up with the main plot. Paper
 * widens the left gutter, where the track names print.
 */
export function plotPadding(compact: boolean, printing: boolean): PlotPadding {
  if (printing) return { left: 56, right: 48, top: 8, bottom: 30 };
  return compact
    ? { left: 36, right: 22, top: 8, bottom: 28 }
    : { left: 48, right: 48, top: 8, bottom: 30 };
}
