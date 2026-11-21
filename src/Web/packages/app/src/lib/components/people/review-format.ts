import { formatLocale } from "#lib/utils/formatting.js";

function percent(value: number, digits: number): string {
  return value.toLocaleString(formatLocale(), {
    style: "unit",
    unit: "percent",
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  });
}

/** Shown where a figure is null. */
export const NO_FIGURE = "-";

export function wholePercent(value: number): string {
  return percent(value, 0);
}

export function onePlacePercent(value: number): string {
  return percent(value, 1);
}

export type ChangeDirection = "up" | "down" | "none";

export interface Change {
  direction: ChangeDirection;
  /** The size in whole points, as shown. */
  points: number;
  /** A real change that rounds to no whole point: shown as under 1, with its direction. */
  underOne: boolean;
}

/** The change as displayed. Only a change of exactly zero has no direction. Null when there is none. */
export function displayChange(change: number | null | undefined): Change | null {
  if (change == null) return null;
  if (change === 0) return { direction: "none", points: 0, underOne: false };
  const direction = change > 0 ? "up" : "down";
  const points = Math.round(Math.abs(change));
  return { direction, points, underOne: points === 0 };
}
