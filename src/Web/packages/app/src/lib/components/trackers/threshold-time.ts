import { TrackerMode } from "#lib/api/index.js";

export type ThresholdUnit = "hours" | "days";

/** The threshold units by name, for a component that also shows a translated word for the unit. */
export const ThresholdUnits = { Hours: "hours", Days: "days" } as const satisfies Record<string, ThresholdUnit>;

/** `before` counts back from the end of the lifespan, or from an Event tracker's date. */
export type ThresholdSide = "before" | "after";

/** A threshold's stored hours as a person enters them: an amount, its unit and which way it counts. */
export interface ThresholdTime {
  amount: number | undefined;
  unit: ThresholdUnit;
  side: ThresholdSide;
}

/** Whole days read as days. A threshold with no time yet counts back from an Event tracker's date. */
export function splitHours(hours: number | undefined, mode: TrackerMode): ThresholdTime {
  if (hours === undefined) {
    return { amount: undefined, unit: "days", side: mode === TrackerMode.Event ? "before" : "after" };
  }
  const size = Math.abs(hours);
  const days = size !== 0 && size % 24 === 0;
  return {
    amount: days ? size / 24 : size,
    unit: days ? "days" : "hours",
    side: hours < 0 ? "before" : "after",
  };
}

/** The stored hours, which are whole: a part of an hour is rounded. Undefined with no amount. */
export function joinHours({ amount, unit, side }: ThresholdTime): number | undefined {
  if (amount === undefined || !Number.isFinite(amount) || amount < 0) return undefined;
  const size = Math.round(amount * (unit === "days" ? 24 : 1));
  // No negative zero: it would not equal the zero that comes back from the server.
  return side === "before" && size !== 0 ? -size : size;
}

/**
 * Why the server refuses a threshold at `hours`, or null: one that counts back from the end of a
 * Duration tracker needs a lifespan longer than itself.
 */
export function thresholdIssue(
  hours: number | undefined,
  mode: TrackerMode,
  lifespanHours: number | undefined,
): "no-lifespan" | "before-start" | null {
  if (hours === undefined || hours >= 0 || mode !== TrackerMode.Duration) return null;
  if (lifespanHours === undefined) return "no-lifespan";
  return -hours >= lifespanHours ? "before-start" : null;
}
