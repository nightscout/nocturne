<!--
  RangeBandsChart's static mode: the same bands, medians, comparison outlines, single-event traces
  and glucose limits, painted on a canvas in the theme's colours, with the series named in a key.
-->
<script lang="ts" module>
  import type { BandColors, PercentileAggregate } from "../percentile-bands.js";
  import type { ReportField, ReportThresholds, ReportTrace } from "../types.js";

  export interface StaticBandSlot {
    aggregate: PercentileAggregate | null;
  }

  export interface StaticRangeBandsModel {
    slotCount: number;
    groups: { key: string; name: string; colors: BandColors; slots: StaticBandSlot[]; comparison: StaticBandSlot[] | null }[];
    median: ReportField;
    outer: boolean;
    named: boolean;
    hasComparison: boolean;
    rangeColoured: boolean;
    thresholds: ReportThresholds | null;
    yDomain: [number, number];
    ticks: number[];
    tickLabel: (slot: number) => string;
    valueLabel: (value: number) => string;
    zeroSlot: number;
    traces: ReportTrace[];
    slotOf: (minute: number) => number;
    traceColor: (trace: ReportTrace) => string;
  }
</script>

<script lang="ts">
  import { Area, Axis, Canvas, Chart, Path, Rect, Rule, Spline } from "layerchart";
  import { curveLinear } from "d3";
  import { gradientStroke, paint, staticText, type StaticRender } from "../../theme/static-theme.js";
  import type { LineColorMode } from "../../view/glucose-gradient-stops.js";
  import { reportGlucoseStops } from "../glucose-scale.js";
  import { aggregateRuns, type AggregatePoint } from "../percentile-bands.js";
  import StaticFrame, { staticPadding, type StaticKeyItem } from "./StaticFrame.svelte";
  import { linePath } from "./static-marks.js";

  interface Props {
    still: StaticRender;
    model: StaticRangeBandsModel;
    title: string;
    context?: string | null;
    lineColorMode: Exclude<LineColorMode, "single">;
    /** The words for the comparison range's dashed outline in the key. */
    comparisonLabel: string;
  }

  let { still, model, title, context = null, lineColorMode, comparisonLabel }: Props = $props();

  const theme = $derived(still.theme);
  const text = $derived(staticText(theme));
  const unit = $derived(model.median.unit ? ` (${model.median.unit})` : "");

  const key = $derived.by((): StaticKeyItem[] => {
    const items: StaticKeyItem[] = model.named
      ? model.groups.map((g) => ({ label: g.name, color: paint(theme, g.colors.median) }))
      : [{ label: `${model.median.label}${unit}`, color: model.rangeColoured ? null : paint(theme, model.groups[0]?.colors.median), shape: "line" }];
    if (model.hasComparison) items.push({ label: comparisonLabel, color: text.muted, shape: "dashed" });
    return items;
  });
  const padding = $derived(staticPadding({ key: true, left: 52 }));

  const pick = (s: StaticBandSlot) => s.aggregate;
  const x = (p: AggregatePoint) => p.slot + 0.5;
  const at = (k: "p10" | "p25" | "p50" | "p75" | "p90") => (p: AggregatePoint) => Number(p.value[k] ?? 0);

  function medianStroke(colors: BandColors, yScale: (v: number) => number, height: number): string {
    if (!model.rangeColoured || !model.thresholds) return paint(theme, colors.median);
    const stops = reportGlucoseStops(model.thresholds, lineColorMode, yScale, height).map(
      ([offset, color]): [number, string] => [offset, paint(theme, color)],
    );
    const gradient = still.gradient?.(0, height, stops);
    return gradient ? gradientStroke(gradient) : paint(theme, colors.median);
  }
</script>

<Chart
  ssr
  width={still.width}
  height={still.height}
  data={[]}
  x={(d: number) => d}
  xDomain={[0, model.slotCount]}
  yDomain={model.yDomain}
  {padding}
>
  {#snippet children({ context: chart })}
    <Canvas ssrCapture={still.capture}>
      <StaticFrame {theme} {title} {context} {key} />
      <Axis
        placement="left"
        ticks={4}
        grid={{ stroke: text.border }}
        rule={false}
        format={model.valueLabel}
        tickLabelProps={{ fill: text.muted, fontSize: 12, style: text.font }}
      />
      {#if model.thresholds}
        {@const top = chart.yScale(model.thresholds.high)}
        {@const bottom = chart.yScale(model.thresholds.low)}
        <Rect x={0} y={top} width={chart.width} height={Math.max(0, bottom - top)} fill={paint(theme, "var(--glucose-in-range)")} fillOpacity={0.1} />
        <Rule y={model.thresholds.low} stroke={paint(theme, "var(--glucose-low)")} strokeOpacity={0.6} dashArray="4 4" />
        <Rule y={model.thresholds.high} stroke={paint(theme, "var(--glucose-high)")} strokeOpacity={0.6} dashArray="4 4" />
      {/if}
      {#if model.zeroSlot >= 0}
        <Rule x={model.zeroSlot} stroke={text.foreground} strokeOpacity={0.5} dashArray="2 2" />
      {/if}
      {#if model.yDomain[0] < 0}
        <Rule y={0} stroke={text.foreground} strokeOpacity={0.4} />
      {/if}
      {#each model.traces as trace (`${trace.cohort}-${trace.at}`)}
        <Spline
          data={trace.points}
          x={(p: [number, number]) => model.slotOf(p[0])}
          y={(p: [number, number]) => p[1]}
          stroke={paint(theme, model.traceColor(trace))}
          strokeWidth={1}
          strokeOpacity={0.28}
          fill="none"
        />
      {/each}
      {#each model.groups as group (group.key)}
        {@const lineStroke = medianStroke(group.colors, chart.yScale, chart.height)}
        {#each aggregateRuns(group.slots, pick) as run, i (`p${i}`)}
          {@const opacity = run.sparse ? 0.35 : 1}
          {#if model.outer}
            <Area data={run.points} {x} y0={at("p10")} y1={at("p90")} fill={paint(theme, group.colors.outer)} fillOpacity={opacity} stroke="none" />
          {/if}
          <Area data={run.points} {x} y0={at("p25")} y1={at("p75")} fill={paint(theme, group.colors.inner)} fillOpacity={opacity} stroke="none" />
          <Path
            pathData={linePath(run.points, (p) => chart.xScale(x(p)), (p) => chart.yScale(at("p50")(p)), curveLinear)}
            stroke={lineStroke}
            strokeWidth={2}
            strokeOpacity={opacity}
            fill="none"
          />
        {/each}
        {#each group.comparison ? aggregateRuns(group.comparison, pick) : [] as run, i (`c${i}`)}
          {#each ["p25", "p75"] as const as edge (edge)}
            <Spline data={run.points} {x} y={at(edge)} stroke={paint(theme, group.colors.median)} strokeWidth={1} strokeOpacity={0.55} style="stroke-dasharray: 2 3" fill="none" />
          {/each}
          <Spline data={run.points} {x} y={at("p50")} stroke={paint(theme, group.colors.median)} strokeWidth={1.5} strokeOpacity={0.85} style="stroke-dasharray: 5 3" fill="none" />
        {/each}
      {/each}
      <Axis
        placement="bottom"
        rule={{ stroke: text.border }}
        ticks={model.ticks}
        format={model.tickLabel}
        tickLabelProps={{ fill: text.muted, fontSize: 12, style: text.font }}
      />
    </Canvas>
  {/snippet}
</Chart>
