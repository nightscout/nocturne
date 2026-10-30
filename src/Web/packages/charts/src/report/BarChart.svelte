<!--
  Bars over a category axis from a report panel's rows: one or more fields per category, side by
  side or stacked. Every colour is the panel's (barLayout): a glucose band's bar takes the colour its
  row carries, another field's bar its theme colour, the rest are neutral.
-->
<script lang="ts">
  import { Axis, Chart, Rect, Svg, Tooltip } from "layerchart";
  import { scaleBand, scaleLinear } from "d3-scale";
  import type { ChartFormat } from "../format.js";
  import { bandScale, barLayout, valueScale, type BarSegment } from "./bar-layout.js";
  import { formatReportValue, type ReportPanel } from "./types.js";

  interface Props {
    panel: ReportPanel;
    format: ChartFormat;
    heightClass?: string;
  }

  let { panel, format, heightClass = "h-[280px]" }: Props = $props();

  const layout = $derived(barLayout(panel));
  const data = $derived(layout.categories.map((category) => ({ category })));

  function segmentsOf(category: unknown): BarSegment[] {
    return layout.segments.filter((s) => s.category === category);
  }

  const valueText = (s: BarSegment) =>
    `${formatReportValue(s.value, s.field, format)}${s.field.unit ? ` ${s.field.unit}` : ""}`;
</script>

{#if layout.segments.length === 0}
  <p class="flex {heightClass} items-center justify-center text-sm text-muted-foreground">No data in this range.</p>
{:else}
  <div class="{heightClass} w-full" data-testid="report-bar">
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
        {@const band = bandScale(context.xScale)}
        {@const y = valueScale(context.yScale)}
        {@const slots = layout.stacked ? 1 : Math.max(layout.fields.length, 1)}
        {@const width = band.bandwidth() / slots}
        <Svg>
          <Axis placement="left" ticks={5} tickLabelProps={{ class: "text-xs fill-muted-foreground" }} />
          <Axis placement="bottom" tickOcclusion tickLabelProps={{ class: "text-xs fill-muted-foreground", fontSize: 12 }} />
          {#each layout.segments as segment (segment.key)}
            <Rect
              x={(band(segment.category) ?? 0) + segment.slot * width}
              y={y(segment.end)}
              width={width}
              height={Math.max(0, y(segment.start) - y(segment.end))}
              fill={segment.color}
              data-testid="report-bar-segment"
              data-color={segment.color}
              data-category={segment.category}
            />
          {/each}
        </Svg>

        <Tooltip.Root {context} class="z-50 rounded-lg border border-border text-xs shadow-xl">
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
    </Chart>
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
