import { render } from "svelte/server";
import type { UserDisplayPreferences } from "#lib/api/index.js";
import PlotSnapshot from "#lib/components/dashboard/glucose-chart/PlotSnapshot.svelte";
import {
  parsePlotSize,
  PLOT_SIZE_COOKIE,
} from "#lib/components/dashboard/glucose-chart/plot-size.js";
import {
  chartAreaMode,
  chartAreaOpacity,
  chartLineColor,
  chartLineColorMode,
  chartPointColor,
  chartPointColorMode,
  chartShowPoints,
  glucoseUnits,
  predictionMinutes,
} from "#lib/stores/appearance-store.svelte.js";
import type { TransformedChartData } from "#lib/utils/chart-data-transform.js";

type Cookies = { get(name: string): string | undefined };

/**
 * Whether this device has reported the plot's size, without which no snapshot
 * is rendered.
 */
export function hasPlotSize(cookies: Cookies): boolean {
  return parsePlotSize(cookies.get(PLOT_SIZE_COOKIE)) !== null;
}

/**
 * The dashboard's main plot as markup, for the document to paint before any
 * script runs (`PlotSnapshot`), drawn in the viewer's chart style and units.
 * Null when this device has not yet reported the plot's size, there is no
 * glucose to draw, or the render fails: the live chart then draws alone.
 */
export function renderPlotSnapshot(options: {
  chartData: TransformedChartData | null;
  nowMs: number;
  lookbackHours: number;
  /** Whether the chart leaves room for a forecast. */
  predictions: boolean;
  cookies: Cookies;
  preferenceLayers: readonly UserDisplayPreferences[];
}): string | null {
  const {
    chartData,
    nowMs,
    lookbackHours,
    predictions,
    cookies,
    preferenceLayers: layers,
  } = options;
  const size = parsePlotSize(cookies.get(PLOT_SIZE_COOKIE));
  if (!chartData?.glucoseData.length || !size) return null;

  try {
    return render(PlotSnapshot, {
      props: {
        chartData,
        nowMs,
        lookbackHours,
        forecastMinutes: predictions ? predictionMinutes.resolve(layers) : 0,
        size: { width: size.width, height: size.height },
        compact: size.compact,
        style: {
          lineColorMode: chartLineColorMode.resolve(layers),
          lineColor: chartLineColor.resolve(layers),
          pointColorMode: chartPointColorMode.resolve(layers),
          pointColor: chartPointColor.resolve(layers),
          showPoints: chartShowPoints.resolve(layers),
          areaMode: chartAreaMode.resolve(layers),
          areaOpacity: chartAreaOpacity.resolve(layers),
          units: glucoseUnits.resolve(layers),
        },
      },
      // Its gradient and clip ids must not collide with the page's own.
      idPrefix: "plot-snapshot-",
    }).body;
  } catch (err: unknown) {
    console.error("Error rendering the plot snapshot:", err);
    return null;
  }
}
