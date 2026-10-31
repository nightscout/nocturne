<!--
  Bars over a category axis from a report panel's rows: one or more fields per category, side by
  side or stacked, up from the bottom or across from the side (`orientation`). Every colour is the
  panel's (barLayout): a glucose band's bar takes the colour its row carries, another field's bar
  its theme colour, the rest are neutral. One stacked category laid across is a single bar of parts
  of a whole, drawn as the reports' time in range bar (StackedBar) with each part's value in its key.
-->
<script lang="ts">
  import { Axis, Chart, Rect, Svg, Tooltip } from "layerchart";
  import { scaleBand, scaleLinear } from "d3-scale";
  import type { ChartFormat } from "../format.js";
  import StackedBar from "./StackedBar.svelte";
  import StaticBars from "./static/StaticBars.svelte";
  import type { StaticRender } from "../theme/static-theme.js";
  import { bandScale, barLayout, valueScale, type BarSegment } from "./bar-layout.js";
  import { formatReportValue, type ReportField, type ReportPanel } from "./types.js";

  interface Props {
    panel: ReportPanel;
    format: ChartFormat;
    heightClass?: string;
    /** Drawn on a canvas in the theme's colours, with no tooltip, for an image. */
    static?: StaticRender;
  }

  let { panel, format, heightClass = "h-[280px]", static: still }: Props = $props();

  const layout = $derived(barLayout(panel));
  const data = $derived(layout.categories.map((category) => ({ category })));
  const single = $derived(layout.horizontal && layout.stacked && layout.categories.length === 1);

  function segmentsOf(category: unknown): BarSegment[] {
    return layout.segments.filter((s) => s.category === category);
  }

  const withUnit = (value: number, field: ReportField) => {
    const text = formatReportValue(value, field, format);
    return field.unit ? `${text}${field.unit === "%" ? "%" : ` ${field.unit}`}` : text;
  };
  const valueText = (s: BarSegment) => withUnit(s.value, s.field);
  /** The whole a single bar's parts are out of: 100 for percents, else their sum. */
  const whole = $derived(layout.fields.every((f) => f.unit === "%") ? 100 : 0);
</script>

{#snippet bars(context: { xScale: unknown; yScale: unknown })}
  {@const band = bandScale(layout.horizontal ? context.yScale : context.xScale)}
  {@const value = valueScale(layout.horizontal ? context.xScale : context.yScale)}
  {@const slots = layout.stacked ? 1 : Math.max(layout.fields.length, 1)}
  {@const width = band.bandwidth() / slots}
  <Svg>
    <Axis placement={layout.horizontal ? "bottom" : "left"} ticks={5} tickLabelProps={{ class: "text-xs fill-muted-foreground" }} />
    <Axis placement={layout.horizontal ? "left" : "bottom"} tickOcclusion tickLabelProps={{ class: "text-xs fill-muted-foreground", fontSize: 12 }} />
    {#each layout.segments as segment (segment.key)}
      {@const across = (band(segment.category) ?? 0) + segment.slot * width}
      {@const low = value(segment.start)}
      {@const high = value(segment.end)}
      <Rect
        x={layout.horizontal ? low : across}
        y={layout.horizontal ? across : high}
        width={layout.horizontal ? Math.max(0, high - low) : width}
        height={layout.horizontal ? width : Math.max(0, low - high)}
        fill={segment.color}
        data-testid="report-bar-segment"
        data-color={segment.color}
        data-category={segment.category}
      />
    {/each}
  </Svg>

  <Tooltip.Root class="z-50 rounded-lg border border-border text-xs shadow-xl">
    {#snippet children({ data: hovered })}
      <Tooltip.Header value={hovered?.category} class="mb-1 border-b border-border pb-1 font-semibold" />
      <Tooltip.List>
        {#each segmentsOf(hovered?.category) as segment (segment.key)}
          <Tooltip.Item label={segment.field.label} value={valueText(segment)} color={segment.color} />
        {/each}
      </Tooltip.List>
    {/snippet}
  </Tooltip.Root>
{/snippet}

{#if still}
  <StaticBars {still} {layout} {single} {whole} {withUnit} title={panel.title ?? panel.dataset} context={panel.context} />
{:else if layout.segments.length === 0}
  <p class="flex {heightClass} items-center justify-center text-sm text-muted-foreground">No data in this range.</p>
{:else if single}
  <div class="w-full py-2" data-testid="report-bar" data-orientation="horizontal">
    <StackedBar
      segments={layout.segments.map((s) => ({ key: s.field.name, label: s.field.label, value: s.value, color: s.color, pattern: s.pattern }))}
      orientation="horizontal"
      max={whole}
      formatValue={(value) => withUnit(value, layout.fields[0])}
      keyOnScreen
    />
  </div>
{:else}
  <div class="{heightClass} w-full" data-testid="report-bar" data-orientation={layout.horizontal ? "horizontal" : "vertical"}>
    {#if layout.horizontal}
      <Chart
        {data}
        x={() => 0}
        xScale={scaleLinear()}
        xDomain={[0, layout.max]}
        y="category"
        yScale={scaleBand().padding(0.2)}
        yDomain={layout.categories}
        padding={{ left: 96, right: 12, top: 8, bottom: 24 }}
        tooltipContext={{ mode: "band" }}
      >
        {#snippet children({ context })}
          {@render bars(context)}
        {/snippet}
      </Chart>
    {:else}
      <Chart
        {data}
        x="category"
        xScale={scaleBand().padding(0.2)}
        xDomain={layout.categories}
        y={() => 0}
        yScale={scaleLinear()}
        yDomain={[0, layout.max]}
        padding={{ left: 40, right: 12, top: 8, bottom: 24 }}
        tooltipContext={{ mode: "band" }}
      >
        {#snippet children({ context })}
          {@render bars(context)}
        {/snippet}
      </Chart>
    {/if}
  </div>
  {#if layout.fields.length > 1}
    <ul class="mt-1 flex flex-wrap gap-3 text-xs text-muted-foreground">
      {#each layout.fields as field (field.name)}
        {@const swatch = layout.segments.find((s) => s.field.name === field.name)?.color}
        <li class="flex items-center gap-1">
          <span class="inline-block size-2 rounded-sm bg-(--swatch)" style:--swatch={swatch}></span>
          {field.label}{field.unit ? ` (${field.unit})` : ""}
        </li>
      {/each}
    </ul>
  {/if}
{/if}
