import { bg } from "$lib/utils/formatting";
import {
  CHART_TEXTURES,
  patternClass,
  type GlucoseRange,
} from "$lib/components/charts/print/chart-print-patterns";
import type { ExtendedTimeInRangePercentages, GlycemicThresholds } from "$lib/api";

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
 * The per-hour bands the API partitions `AveragedStats.timeInRange` on, lowest
 * first so a stack reads upward, labelled from the edges the API says it used.
 * Built per call: the labels follow the viewer's glucose unit. Without
 * thresholds the labels are empty, for marks that show no key.
 */
export function hourlyBandSeries(thresholds?: GlycemicThresholds): HourlyBandSeries[] {
  const edge = (mgdl: number | undefined) => (mgdl == null ? "" : String(bg(mgdl)));
  const span = (low: number | undefined, high: number | undefined) =>
    thresholds ? `${edge(low)}-${edge(high)}` : "";
  const t = thresholds;

  return [
    band("veryLow", t ? `<${edge(t.veryLow)}` : "", "very-low"),
    band("low", span(t?.veryLow, t?.low), "low"),
    band("tightTarget", span(t?.tightTargetBottom, t?.tightTargetTop), "tight-range"),
    band("aboveTightTarget", span(t?.tightTargetTop, t?.targetTop), "in-range"),
    band("high", span(t?.targetTop, t?.veryHigh), "high"),
    band("veryHigh", t ? `>${edge(t.veryHigh)}` : "", "very-high"),
  ];
}
