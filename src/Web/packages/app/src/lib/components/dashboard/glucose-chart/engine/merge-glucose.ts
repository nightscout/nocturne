import type { Entry } from "$lib/websocket/types";
import type { TransformedChartData } from "$lib/utils/chart-data-transform";
import { getGlucoseColor } from "$lib/utils/chart-colors";
import { resolveGlucoseThresholds } from "$lib/constants/glucose-thresholds";
import type { GlucosePoint } from "./chart-data-engine.svelte";

/** The server's glucose series with the realtime readings in [fromMs, toMs] it lacks, by time. */
export function mergeRealtimeGlucose(
  chartData: TransformedChartData | null,
  entries: readonly Entry[],
  fromMs: number,
  toMs: number
): GlucosePoint[] {
  const base = chartData?.glucoseData ?? [];
  if (!chartData) return base as GlucosePoint[];

  const thresholds = resolveGlucoseThresholds(chartData.thresholds);

  const byMills = new Map<number, GlucosePoint>();
  for (const p of base) byMills.set(p.time.getTime(), p);

  for (const e of entries) {
    if (
      e.type !== "sgv" ||
      e.mills == null ||
      e.sgv == null ||
      e.mills < fromMs ||
      e.mills > toMs ||
      byMills.has(e.mills)
    ) {
      continue;
    }
    byMills.set(e.mills, {
      time: new Date(e.mills),
      sgv: e.sgv,
      direction: e.direction,
      dataSource: e.data_source,
      color: getGlucoseColor(e.sgv, thresholds),
    });
  }

  return [...byMills.values()].sort(
    (a, b) => a.time.getTime() - b.time.getTime()
  ) as GlucosePoint[];
}
