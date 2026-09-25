import { bg } from "$lib/utils/formatting";
import {
  CHART_TEXTURES,
  patternClass,
  type GlucoseRange,
} from "$lib/components/charts/print/chart-print-patterns";
import type { ExtendedTimeInRangePercentages } from "$lib/api";

export type HourlyBandKey = keyof ExtendedTimeInRangePercentages;

export interface HourlyBandSeries {
  key: HourlyBandKey;
  label: string;
  texture: GlucoseRange;
  color: string;
  props: { class: string };
}

const band = (key: HourlyBandKey, label: string, texture: GlucoseRange): HourlyBandSeries => ({
  key,
  label,
  texture,
  color: CHART_TEXTURES[texture].color,
  props: { class: patternClass(texture) },
});

/**
 * The per-hour consensus bands the API partitions `AveragedStats.timeInRange`
 * on, lowest first so a stack reads upward. Built per call: the labels follow
 * the viewer's glucose unit.
 */
export function hourlyBandSeries(): HourlyBandSeries[] {
  return [
    band("veryLow", `<${bg(54)}`, "very-low"),
    band("low", `${bg(54)}-${bg(70)}`, "low"),
    band("tightTarget", `${bg(70)}-${bg(140)}`, "tight-range"),
    band("aboveTightTarget", `${bg(140)}-${bg(180)}`, "in-range"),
    band("high", `${bg(180)}-${bg(250)}`, "high"),
    band("veryHigh", `>${bg(250)}`, "very-high"),
  ];
}
