import { scaleLinear } from "d3-scale";

/**
 * Top of a right-hand carbs axis that starts at zero: the largest value rounded up to a nice
 * tick, and at least 1 g so an all-zero series still has a scale.
 */
export function carbAxisMax(carbs: readonly number[]): number {
  return scaleLinear()
    .domain([0, Math.max(...carbs, 1)])
    .nice()
    .domain()[1];
}

/**
 * A carbs value expressed on the insulin axis, so a line can be drawn through the chart's own
 * scale: the top of the carbs axis lands on the top of the insulin axis.
 */
export function carbsOnInsulinScale(carbs: number, axisMax: number, insulinMax: number): number {
  return axisMax > 0 ? (carbs / axisMax) * insulinMax : 0;
}
