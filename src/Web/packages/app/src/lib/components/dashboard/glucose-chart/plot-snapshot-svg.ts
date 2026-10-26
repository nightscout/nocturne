import { scaleLinear, scaleTime } from "d3-scale";
import { curveMonotoneX, line } from "d3-shape";
import { createChartDataView } from "./engine/chart-data-view.svelte";
import { mergeRealtimeGlucose } from "./engine/merge-glucose";
import { computeTrackLayout } from "./engine/track-layout";
import { plotPadding } from "./plot-padding";
import { lineStopsFor, type AreaMode, type LineColorMode } from "./utils/glucose-gradient-stops";
import { dashOf } from "#lib/components/charts/print/chart-print-patterns.js";
import { getGlucoseColor, getGlucoseColorContinuous } from "#lib/utils/chart-colors.js";
import { bg } from "#lib/utils/formatting.js";
import type { TransformedChartData } from "#lib/utils/chart-data-transform.js";
import type { GlucoseUnits } from "#lib/stores/appearance-store.svelte.js";

const HOUR_MS = 60 * 60 * 1000;
/** Matches GLUCOSE_CHART_FETCH_HOURS: the window the live chart merges realtime readings over. */
const FETCH_HOURS = 48;

export interface PlotSnapshotOptions {
  chartData: TransformedChartData;
  nowMs: number;
  lookbackHours: number;
  /** Minutes of forecast the window leaves room for, or 0 when the chart draws none. */
  forecastMinutes: number;
  size: { width: number; height: number };
  compact: boolean;
  style: {
    lineColorMode: LineColorMode;
    lineColor: string;
    pointColorMode: LineColorMode | undefined;
    pointColor: string | undefined;
    showPoints: boolean;
    areaMode: AreaMode;
    units: GlucoseUnits;
  };
}

const round = (value: number) => Math.round(value * 100) / 100;
const attr = (value: string) => value.replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;");
const text = (value: string) => value.replaceAll("&", "&amp;").replaceAll("<", "&lt;");

/**
 * The dashboard's main plot as the live chart first draws it (GlucoseChartShell with ThresholdRules
 * and GlucoseTrack, at the starting legend, its window leaving the forecast's room before the
 * forecast itself has loaded), written straight to SVG markup:
 * the same engine view, track layout, scales, curve and colours, without LayerChart, whose server
 * render costs the document about 100 ms. The live chart's line lands on this one when it takes
 * over; the time axis is left to it, since its ticks fall on the viewer's local hours.
 *
 * Null for a style it does not draw (an area under the line).
 */
export function plotSnapshotSvg(options: PlotSnapshotOptions): string | null {
  const { chartData, nowMs, lookbackHours, forecastMinutes, size, compact, style } = options;
  if (style.areaMode !== "off") return null;

  const nowMinute = Math.floor(nowMs / 60000) * 60000;
  const displayDateRange = { from: new Date(nowMinute - lookbackHours * HOUR_MS), to: new Date(nowMinute) };
  const fullDataRange = { from: new Date(nowMinute - FETCH_HOURS * HOUR_MS), to: new Date(nowMinute) };
  const withForecast = { from: displayDateRange.from, to: new Date(nowMinute + forecastMinutes * 60 * 1000) };
  const glucoseData = mergeRealtimeGlucose(chartData, [], fullDataRange.from.getTime(), fullDataRange.to.getTime());
  if (glucoseData.length === 0) return null;

  const engine = createChartDataView({
    serverChartData: chartData,
    glucoseData,
    predictionData: null,
    predictionError: null,
    chartDataError: null,
    predictionServiceAvailable: false,
    predictionAvailability: "unknown",
    effectiveShowPredictions: forecastMinutes > 0,
    nowMinute,
    lookbackHours,
    fullDataRange,
    displayDateRange,
    displayDateRangeWithPredictions: withForecast,
    fullXDomain: fullDataRange,
  });
  const thresholds = engine.thresholds;

  const padding = plotPadding(compact, false);
  const width = size.width - padding.left - padding.right;
  const height = size.height - padding.top - padding.bottom;
  // GlucoseChartCard's starting legend: basal, IOB and COB on; of the swim lanes, pump modes only.
  const layout = computeTrackLayout(
    height,
    thresholds.glucoseYMax,
    engine.maxBasalRate,
    engine.maxIOB,
    { basal: true, iob: true, cob: true },
    { pumpMode: engine.displayPumpModeSpans.length > 0, override: false, profile: false, activity: false },
    engine.minIOB
  );

  const x = scaleTime().domain([withForecast.from, withForecast.to]).range([0, width]);
  const y = scaleLinear().domain([0, thresholds.glucoseYMax]).range([height, 0]);
  const glucoseY = (sgv: number) => y(layout.glucose.scale(sgv));
  const axisScale = layout.glucose.axisScale;

  const id = "plot-snapshot";
  const parts: string[] = [];

  const rule = (value: number | null | undefined, className: string, dash: string | undefined) => {
    if (value == null) return;
    const ry = glucoseY(value);
    parts.push(
      `<line class="lc-line lc-rule-y-line ${className}" x1="0" y1="${ry}" x2="${width}" y2="${ry}"${dash ? ` stroke-dasharray="${dash}"` : ""}></line>`
    );
  };
  const limitDash = dashOf("target-range-limit");
  const targetDash = dashOf("glucose-target");
  rule(thresholds.high, "stroke-glucose-high/50", limitDash);
  rule(thresholds.low, "stroke-glucose-very-low/50", limitDash);
  rule(thresholds.targetLow, "stroke-glucose-in-range/70", targetDash);
  if (thresholds.targetHigh !== thresholds.targetLow) rule(thresholds.targetHigh, "stroke-glucose-in-range/70", targetDash);

  for (const tick of axisScale.ticks(5)) {
    const ty = axisScale(tick);
    parts.push(
      `<line class="lc-line lc-axis-tick" x1="0" y1="${ty}" x2="-4" y2="${ty}"></line>` +
        `<text x="-4" y="${ty}" text-anchor="end" class="lc-text lc-axis-tick-label text-xs fill-muted-foreground"><tspan x="-4" dy="3.5">${text(String(bg(tick, style.units)))}</tspan></text>`
    );
  }

  const stops = lineStopsFor(style.lineColorMode, thresholds, axisScale, height);
  let stroke = attr(style.lineColor);
  if (stops) {
    parts.push(
      `<defs><linearGradient id="${id}-line" x1="0%" y1="0" x2="0%" y2="${height}" gradientUnits="userSpaceOnUse">` +
        stops.map(([offset, color]) => `<stop offset="${offset}" stop-color="${attr(color)}"></stop>`).join("") +
        `</linearGradient></defs>`
    );
    stroke = `url(#${id}-line)`;
  }

  // Only the window shows through the clip. Two readings past each edge keep the visible curve the
  // live chart's: a monotone curve's tangent at a reading reads only its neighbours.
  const first = Math.max(0, glucoseData.findIndex((d) => d.time >= withForecast.from) - 2);
  const end = glucoseData.findLastIndex((d) => d.time <= withForecast.to);
  const visible = glucoseData.slice(first, end < 0 ? 0 : end + 3);

  const path = line<(typeof glucoseData)[number]>()
    .x((d) => x(d.time))
    .y((d) => glucoseY(d.sgv))
    .curve(curveMonotoneX)(visible);

  const pointMode = style.pointColorMode ?? (style.pointColor !== undefined ? "single" : style.lineColorMode);
  const pointColor = style.pointColor ?? style.lineColor;
  const pointFill = (sgv: number) =>
    pointMode === "single"
      ? pointColor
      : pointMode === "continuous"
        ? getGlucoseColorContinuous(sgv)
        : getGlucoseColor(sgv, thresholds);
  const points = style.showPoints
    ? visible
        .map((d) => `<circle cx="${round(x(d.time))}" cy="${round(glucoseY(d.sgv))}" r="3" fill="${attr(pointFill(d.sgv))}" class="lc-circle opacity-90"></circle>`)
        .join("")
    : "";

  parts.push(
    `<clipPath id="${id}-clip"><rect x="0" y="0" width="${width}" height="${height}"></rect></clipPath>` +
      `<g clip-path="url(#${id}-clip)"><path d="${path ?? ""}" stroke="${stroke}" class="lc-path stroke-2 fill-none"></path>${points}</g>`
  );

  return (
    `<svg viewBox="0 0 ${size.width} ${size.height}" preserveAspectRatio="none" role="presentation">` +
    `<g transform="translate(${padding.left}, ${padding.top})">${parts.join("")}</g></svg>`
  );
}
