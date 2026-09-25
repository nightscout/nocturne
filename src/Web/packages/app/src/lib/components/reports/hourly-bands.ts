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

const edge = (mgdl: number | undefined) => (mgdl == null ? null : String(bg(mgdl)));

const span = (low: number | undefined, high: number | undefined) => {
  const from = edge(low);
  const to = edge(high);
  return from && to ? `${from}-${to}` : "";
};

const beyond = (sign: "<" | ">", mgdl: number | undefined) => {
  const at = edge(mgdl);
  return at ? `${sign}${at}` : "";
};

/**
 * The per-hour bands the API partitions `AveragedStats.timeInRange` on, lowest
 * first so a stack reads upward. The edges mirror the API's scale: the tight
 * band starts at `low`, and the high band at `targetTop`. Built per call, as
 * the labels follow the viewer's glucose unit. A label whose edge the API did
 * not send is empty rather than a made-up number.
 */
export function hourlyBandSeries(thresholds?: GlycemicThresholds): HourlyBandSeries[] {
  const t = thresholds;

  return [
    band("veryLow", beyond("<", t?.veryLow), "very-low"),
    band("low", span(t?.veryLow, t?.low), "low"),
    band("tightTarget", span(t?.low, t?.tightTargetTop), "tight-range"),
    band("aboveTightTarget", span(t?.tightTargetTop, t?.targetTop), "in-range"),
    band("high", span(t?.targetTop, t?.veryHigh), "high"),
    band("veryHigh", beyond(">", t?.veryHigh), "very-high"),
  ];
}
