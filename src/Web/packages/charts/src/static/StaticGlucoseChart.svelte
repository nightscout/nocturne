<!--
  GlucoseChart's static mode: the dashboard chart's glucose line and readings in its colour mode,
  the target band and range limits, the delivered basal along the top, and the bolus and carb
  markers on the line, painted on a canvas. The other tracks (IOB and COB, pump modes, events,
  trackers, forecasts, heart rate, steps, sleep) are not drawn; the summary names what they hold.
-->
<script lang="ts">
  import { Axis, Canvas, Chart, Circle, Path, Rule, Text } from "layerchart";
  import { dashOf } from "../print/patterns.js";
  import { curveMonotoneX, scaleTime } from "d3";
  import { getGlucoseColor, getGlucoseColorContinuous } from "../colors.js";
  import { glucoseValue, formatClockTime, type ChartFormat, type GlucoseUnits } from "../format.js";
  import type { ChartLabels } from "../labels.js";
  import { gradientStroke, paint, staticText, type StaticRender } from "../theme/static-theme.js";
  import type { ChartDataView, GlucosePoint } from "../view/chart-data-view.svelte.js";
  import { lineStopsFor, type LineColorMode } from "../view/glucose-gradient-stops.js";
  import StaticFrame, { staticPadding, type StaticKeyItem } from "../report/static/StaticFrame.svelte";
  import { linePath, markerPath } from "../report/static/static-marks.js";

  interface Props {
    still: StaticRender;
    view: ChartDataView;
    glucoseUnits: GlucoseUnits;
    labels: ChartLabels;
    format: ChartFormat;
    lineColorMode: LineColorMode;
  }

  let { still, view, glucoseUnits, labels, format, lineColorMode }: Props = $props();

  const theme = $derived(still.theme);
  const text = $derived(staticText(theme));
  const thresholds = $derived(view.thresholds);
  const glucose = $derived(view.glucoseData);
  const basal = $derived(view.basalData);
  const domain = $derived<[Date, Date]>([view.displayDateRange.from, view.displayDateRange.to]);

  const key = $derived.by((): StaticKeyItem[] => {
    const items: StaticKeyItem[] = [
      { label: labels.glucose, color: lineColorMode === "single" ? paint(theme, "var(--glucose-in-range)") : null, shape: "line" },
    ];
    if (basal.length > 0) items.push({ label: labels.basal, color: paint(theme, basal[0].fillColor) });
    if (view.bolusMarkers.length > 0) items.push({ label: labels.bolus, color: paint(theme, "var(--insulin-bolus)") });
    if (view.carbMarkers.length > 0) items.push({ label: labels.carbs, color: paint(theme, "var(--carbs)") });
    return items;
  });
  const padding = $derived(staticPadding({ key: true }));
  const innerHeight = $derived(still.height - padding.top - padding.bottom);
  /** The basal lane's share of the plot, above the glucose. */
  const lane = $derived(basal.length > 0 ? Math.round(innerHeight * 0.16) : 0);
  const yMax = $derived(Math.max(thresholds.glucoseYMax, thresholds.veryHigh, ...glucose.map((g) => g.sgv)) * 1.05);
  const maxRate = $derived(Math.max(view.maxBasalRate, ...basal.map((p) => p.rate ?? 0), 0.1));

  function pointFill(sgv: number): string {
    if (lineColorMode === "single") return paint(theme, "var(--glucose-in-range)");
    return paint(theme, lineColorMode === "continuous" ? getGlucoseColorContinuous(sgv) : getGlucoseColor(sgv, thresholds));
  }

  function lineStroke(yScale: (mgdl: number) => number, height: number): string {
    const stops = lineStopsFor(lineColorMode, thresholds, yScale, height);
    if (!stops) return paint(theme, "var(--glucose-in-range)");
    const gradient = still.gradient?.(0, height, stops.map(([offset, color]): [number, string] => [offset, paint(theme, color)]));
    return gradient ? gradientStroke(gradient) : paint(theme, "var(--glucose-in-range)");
  }

  /** The delivered basal as steps hanging from the top of its lane, each rate held to the next point or the window's end. */
  function basalSteps(xScale: (d: Date) => number): { path: string; fill: string }[] {
    const steps: { path: string; fill: string }[] = [];
    for (const [i, point] of basal.entries()) {
      const until = basal[i + 1]?.timestamp ?? domain[1].getTime();
      if (!point.rate) continue;
      const x0 = xScale(new Date(point.timestamp ?? 0));
      const x1 = xScale(new Date(until));
      const depth = (point.rate / maxRate) * (lane - 4);
      steps.push({ path: `M${x0},0 L${x1},0 L${x1},${depth} L${x0},${depth} Z`, fill: paint(theme, point.fillColor) });
    }
    return steps;
  }

  /** Each bolus and carb entry at the reading nearest it, a carb raised above a bolus at the same time. */
  function markers(xScale: (d: Date) => number, yScale: (v: number) => number) {
    const nearest = (time: Date): GlucosePoint | undefined => {
      let best: GlucosePoint | undefined;
      for (const g of glucose)
        if (!best || Math.abs(g.time.getTime() - time.getTime()) < Math.abs(best.time.getTime() - time.getTime())) best = g;
      return best;
    };
    const at = (time: Date) => yScale(nearest(time)?.sgv ?? thresholds.low);
    return [
      ...view.bolusMarkers.map((m, i) => ({
        key: `b${i}`, kind: "bolus" as const, x: xScale(m.time), y: at(m.time),
        automatic: m.bolusType === "AutomaticBolus" || m.bolusType === "Smb",
        text: labels.insulinAmount((m.insulin ?? 0).toFixed(1)),
      })),
      ...view.carbMarkers.map((m, i) => ({
        key: `c${i}`, kind: "carbs" as const, x: xScale(m.time), y: at(m.time) - 34, automatic: false,
        text: labels.carbAmount(String(m.carbs ?? 0)),
      })),
    ];
  }
</script>

<Chart
  ssr
  width={still.width}
  height={still.height}
  data={glucose}
  x={(d: GlucosePoint) => d.time}
  xScale={scaleTime()}
  xDomain={domain}
  y={(d: GlucosePoint) => d.sgv}
  yDomain={[0, yMax]}
  yRange={[innerHeight, lane + 6]}
  {padding}
>
  {#snippet children({ context: chart })}
    <Canvas ssrCapture={still.capture}>
      <StaticFrame {theme} title={still.title ?? labels.glucose} context={still.context} {key} />
      <Axis
        placement="left"
        ticks={5}
        grid={{ stroke: text.border }}
        rule={false}
        format={(v: number) => String(glucoseValue(v, glucoseUnits))}
        tickLabelProps={{ fill: text.muted, fontSize: 12, style: text.font }}
      />
      <Axis
        placement="bottom"
        format={(v: unknown) => (v instanceof Date ? formatClockTime(v, format) : String(v))}
        rule={{ stroke: text.border }}
        tickLabelProps={{ fill: text.muted, fontSize: 12, style: text.font }}
      />
      <Rule y={thresholds.high} stroke={paint(theme, "var(--glucose-high)", undefined, 0.5)} dashArray={dashOf("target-range-limit")} />
      <Rule y={thresholds.low} stroke={paint(theme, "var(--glucose-very-low)", undefined, 0.5)} dashArray={dashOf("target-range-limit")} />
      {#each [...new Set([thresholds.targetLow, thresholds.targetHigh])] as target (target)}
        {#if target !== null}
          <Rule y={target} stroke={paint(theme, "var(--glucose-in-range)", undefined, 0.7)} dashArray={dashOf("glucose-target")} />
        {/if}
      {/each}
      {#each basalSteps(chart.xScale) as step, i (i)}
        <Path pathData={step.path} fill={step.fill} stroke="none" />
      {/each}
      {#if lane > 0}
        <Text x={4} y={14} value={labels.basal} fill={text.muted} fontSize={11} style={text.font} />
      {/if}
      <Path
        pathData={linePath(glucose, (d) => chart.xScale(d.time), (d) => chart.yScale(d.sgv), curveMonotoneX)}
        stroke={lineStroke(chart.yScale, chart.height)}
        strokeWidth={2}
        fill="none"
      />
      {#if glucose.length / Math.max(chart.width, 1) < 0.5}
        <Circle
          data={glucose}
          cx={(d: GlucosePoint) => d.time}
          cy={(d: GlucosePoint) => d.sgv}
          r={3}
          fill={(d: GlucosePoint) => pointFill(d.sgv)}
        />
      {/if}
      {#each markers(chart.xScale, chart.yScale) as marker (marker.key)}
        {@const color = paint(theme, marker.kind === "bolus" ? "var(--insulin-bolus)" : "var(--carbs)")}
        <Path
          pathData={markerPath(marker.kind, marker.x, marker.y)}
          fill={marker.automatic ? "none" : color}
          stroke={marker.automatic ? color : "none"}
          strokeWidth={1.5}
          opacity={0.9}
        />
        <Text
          x={marker.x}
          y={marker.kind === "bolus" ? marker.y + 20 : marker.y - 11}
          value={marker.text}
          textAnchor="middle"
          fill={color}
          fontSize={10}
          style={text.strong}
        />
      {/each}
    </Canvas>
  {/snippet}
</Chart>
