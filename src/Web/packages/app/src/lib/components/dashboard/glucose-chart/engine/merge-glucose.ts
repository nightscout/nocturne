import type { Entry } from "$lib/websocket/types";
import type { TransformedChartData } from "$lib/utils/chart-data-transform";
import { getGlucoseColor, type GlucoseThresholds } from "$lib/utils/chart-colors";
import type { GlucosePoint } from "./chart-data-view.svelte";

/**
 * The server's glucose series with the realtime readings in [fromMs, toMs] it
 * lacks, by time. Added readings are coloured by `thresholds`, else the server
 * payload's; with neither there is nothing to colour by and the series is empty.
 * `thresholds` with no `chartData` draws the realtime readings alone.
 */
export function mergeRealtimeGlucose(
  chartData: TransformedChartData | null,
  entries: readonly Entry[],
  fromMs: number,
  toMs: number,
  thresholds?: GlucoseThresholds
): GlucosePoint[] {
  const base = chartData?.glucoseData ?? [];
  const colourBy = thresholds ?? chartData?.thresholds;
  if (!colourBy) return base;

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
      color: getGlucoseColor(e.sgv, colourBy),
    });
  }

  return [...byMills.values()].sort(
    (a, b) => a.time.getTime() - b.time.getTime()
  );
}
