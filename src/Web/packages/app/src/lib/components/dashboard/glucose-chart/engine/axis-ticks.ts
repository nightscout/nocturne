import { timeHour } from "d3-time";

/**
 * The part of a scale this needs. Written structurally rather than as
 * layerchart's `AnyScale`, which the package does not export from its root, and
 * loosely enough that `AnyScale` is assignable to it — a `ticks` prop is called
 * with the scale, so a narrower parameter would not typecheck.
 */
type TickScale = {
  range: () => unknown[];
  ticks?: (count?: number) => unknown[];
};

/**
 * Ticks for an hour-labelled time axis: the scale's own ticks, thinned to the
 * ones that land on an hour.
 *
 * layerchart derives this itself from `format="hour"`, but only for its built-in
 * format names — `filterTicksByFormat` hands a custom formatter's ticks back
 * untouched, so an hour label would repeat on every sub-hour tick. `spacing`
 * mirrors Axis's own default for a bottom placement.
 *
 * Assumes a local-time scale, matching the chart's `scaleTime()`; a `scaleUtc()`
 * would need `utcHour`, whose UTC-midnight ticks local intervals never floor to.
 */
export function hourTicks(scale: TickScale, spacing = 80): Date[] {
  if (typeof scale.ticks !== "function") return [];

  const [start, end] = scale.range() as number[];
  const count = Math.max(2, Math.round(Math.abs(end - start) / spacing));
  return (scale.ticks(count) as Date[]).filter((d) => +timeHour.floor(d) === +d);
}
