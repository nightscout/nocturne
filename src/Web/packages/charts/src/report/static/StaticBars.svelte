<!--
  CategoryBars' static mode: the same segments in the same colours (barLayout), painted on a canvas,
  with the fields in a key. One stacked category laid across is drawn as the stacked bar with each
  part's value in its key.
-->
<script lang="ts">
  import { Axis, Canvas, Chart, Rect } from "layerchart";
  import { scaleBand, scaleLinear } from "d3-scale";
  import { paint, staticText, type StaticRender } from "../../theme/static-theme.js";
  import { bandScale, valueScale, type BarLayout } from "../bar-layout.js";
  import type { ReportField } from "../types.js";
  import StaticFrame, { staticPadding, type StaticKeyItem } from "./StaticFrame.svelte";
  import StaticStackedBar from "./StaticStackedBar.svelte";

  interface Props {
    still: StaticRender;
    layout: BarLayout;
    single: boolean;
    whole: number;
    title: string;
    context?: string | null;
    withUnit: (value: number, field: ReportField) => string;
  }

  let { still, layout, single, whole, title, context = null, withUnit }: Props = $props();

  const theme = $derived(still.theme);
  const text = $derived(staticText(theme));
  const key = $derived<StaticKeyItem[]>(
    !single && layout.fields.length > 1
      ? layout.fields.map((field) => ({
          label: `${field.label}${field.unit ? ` (${field.unit})` : ""}`,
          color: paint(theme, layout.segments.find((s) => s.field.name === field.name)?.color),
        }))
      : [],
  );
  const padding = $derived(staticPadding({ key: key.length > 0, left: layout.horizontal ? 104 : 48 }));
  const data = $derived(layout.categories.map((category) => ({ category })));
  const tickProps = $derived({ fill: text.muted, fontSize: 12, style: text.font });
</script>

{#snippet bars(chart: { xScale: unknown; yScale: unknown })}
  {@const band = bandScale(layout.horizontal ? chart.yScale : chart.xScale)}
  {@const value = valueScale(layout.horizontal ? chart.xScale : chart.yScale)}
  {@const slots = layout.stacked ? 1 : Math.max(layout.fields.length, 1)}
  {@const size = band.bandwidth() / slots}
  <Axis placement={layout.horizontal ? "bottom" : "left"} ticks={5} grid={{ stroke: text.border }} rule={false} tickLabelProps={tickProps} />
  <Axis placement={layout.horizontal ? "left" : "bottom"} rule={{ stroke: text.border }} tickLabelProps={tickProps} />
  {#each layout.segments as segment (segment.key)}
    {@const across = (band(segment.category) ?? 0) + segment.slot * size}
    {@const low = value(segment.start)}
    {@const high = value(segment.end)}
    <Rect
      x={layout.horizontal ? low : across}
      y={layout.horizontal ? across : high}
      width={layout.horizontal ? Math.max(0, high - low) : size}
      height={layout.horizontal ? size : Math.max(0, low - high)}
      fill={paint(theme, segment.color)}
    />
  {/each}
{/snippet}

{#if single}
  <Chart ssr width={still.width} height={still.height} data={[]} padding={staticPadding({ key: false, left: 16, right: 16 })}>
    {#snippet children({ context: chart })}
      <Canvas ssrCapture={still.capture}>
        <StaticFrame {theme} {title} {context} />
        <StaticStackedBar
          {theme}
          segments={layout.segments.map((s) => ({ key: s.field.name, label: s.field.label, value: s.value, color: s.color }))}
          x={0}
          y={Math.max(8, chart.height * 0.2)}
          width={chart.width}
          height={Math.min(56, Math.max(24, chart.height * 0.25))}
          max={whole}
          formatValue={(v) => withUnit(v, layout.fields[0])}
        />
      </Canvas>
    {/snippet}
  </Chart>
{:else if layout.horizontal}
  <Chart
    ssr
    width={still.width}
    height={still.height}
    {data}
    x={() => 0}
    xScale={scaleLinear()}
    xDomain={[0, layout.max]}
    y="category"
    yScale={scaleBand().padding(0.2)}
    yDomain={layout.categories}
    {padding}
  >
    {#snippet children({ context: chart })}
      <Canvas ssrCapture={still.capture}>
        <StaticFrame {theme} {title} {context} {key} />
        {@render bars(chart)}
      </Canvas>
    {/snippet}
  </Chart>
{:else}
  <Chart
    ssr
    width={still.width}
    height={still.height}
    {data}
    x="category"
    xScale={scaleBand().padding(0.2)}
    xDomain={layout.categories}
    y={() => 0}
    yScale={scaleLinear()}
    yDomain={[0, layout.max]}
    {padding}
  >
    {#snippet children({ context: chart })}
      <Canvas ssrCapture={still.capture}>
        <StaticFrame {theme} {title} {context} {key} />
        {@render bars(chart)}
      </Canvas>
    {/snippet}
  </Chart>
{/if}
