<!--
  A chart panel: its tracks one above another over one horizontal axis, each with up to two value
  axes, every layer drawn as its mark (LayeredPlot), one tooltip that lists each layer's row at the
  slot under the pointer, and a legend of the layers. Over categories each track's bars are drawn by
  CategoryBars. Every value, word and colour is the API's.
-->
<script lang="ts">
  import { Chart, Svg, Tooltip } from "layerchart";
  import { scaleLinear, scaleTime } from "d3-scale";
  import type { ChartFormat } from "../format.js";
  import type { ChartLabels } from "../labels.js";
  import type { LineColorMode } from "../view/glucose-gradient-stops.js";
  import CategoryBars from "./CategoryBars.svelte";
  import LayeredPlot from "./layered/LayeredPlot.svelte";
  import { formatAxisValue } from "./layered/axis-format.js";
  import {
    drawnLayers,
    fieldColor,
    isTimeAxis,
    legend,
    rowMatch,
    rowsAt,
    type SharedSides,
    slots,
    toDomain,
    valueFields,
    xExtent,
  } from "./layered/layered-layout.js";
  import { formatReportValue, rowSentence, type ReportField, type ReportPanel, type ReportRow } from "./types.js";

  interface Props {
    panel: ReportPanel;
    format: ChartFormat;
    /** The report's glucose colouring, from the API. */
    lineColorMode?: Exclude<LineColorMode, "single">;
    /** The chart's words, which treatment markers write their amounts with; no markers are drawn without them. */
    labels?: ChartLabels;
    /** The plot's height, in pixels, for one track of normal height; a chart of more tracks is taller. */
    trackHeight?: number;
    /** Draw the key of the layers under the plot; a host that names the layers itself leaves it out. */
    legend?: boolean;
    /** Values each track's sides cover beyond their own, by track id, so small multiples share one scale. */
    valueExtents?: Record<string, SharedSides>;
  }

  let { panel, format, lineColorMode = "continuous", labels, trackHeight = 240, legend: showLegend = true, valueExtents }: Props = $props();

  const kind = $derived(panel.xKind ?? "time");
  const weight = $derived(panel.tracks.reduce((sum, t) => sum + t.height, 0));
  const height = $derived(Math.round(Math.min(560, Math.max(trackHeight, trackHeight * 0.75 * weight + 40))));
  const hasRight = $derived(panel.tracks.some((t) => t.layers.some((l) => l.side === "right")));
  const extent = $derived(xExtent(panel));
  const points = $derived(slots(panel).map((raw) => ({ raw, x: toDomain(kind, raw) })));
  /** How far the pointer's slot may be from a layer's row and still be that row's: the widest gap between slots. */
  const tolerance = $derived.by(() => {
    let gap = 0;
    for (let i = 1; i < points.length; i++) gap = Math.max(gap, points[i].raw - points[i - 1].raw);
    return gap || 1;
  });
  const match = $derived(rowMatch(panel, tolerance));
  const entries = $derived(legend(panel));
  const xField = $derived.by((): ReportField | undefined => {
    const first = panel.tracks.flatMap((t) => drawnLayers(t))[0];
    return first?.fields.find((f) => f.name === first.bindings.x);
  });

  const tick = (value: unknown) => formatAxisValue(kind, value, format);
  const withUnit = (value: ReportRow[string], field: ReportField) =>
    `${formatReportValue(value, field, format)}${field.unit && value !== null && value !== undefined ? ` ${field.unit}` : ""}`;

  interface TipItem {
    key: string;
    label: string;
    value: string;
    color: string | null;
  }

  /** What the tooltip lists for a slot: each drawn layer's fields at its row there, or its spans over it. */
  function itemsAt(raw: number): TipItem[] {
    return panel.tracks.flatMap((track) =>
      drawnLayers(track).flatMap((layer): TipItem[] => {
        const rows = rowsAt(layer, raw, match).slice(0, 4);
        if (layer.mark === "spans") {
          const name = layer.fields.find((f) => f.colorField)?.name ?? layer.bindings.category ?? "";
          return rows.map((row, i) => ({ key: `${layer.id}-${i}`, label: layer.title, value: String(row[name] ?? ""), color: null }));
        }
        const fields = layer.mark === "markers" ? layer.fields.filter((f) => (layer.bindings.y ?? []).includes(f.name)) : valueFields(layer);
        return rows.flatMap((row, i) =>
          fields
            .filter((f) => row[f.name] !== null && row[f.name] !== undefined)
            .map((field) => ({
              key: `${layer.id}-${i}-${field.name}`,
              label: fields.length > 1 || layer.bindings.series ? `${layer.title} · ${layer.bindings.series ? `${row[layer.bindings.series] ?? ""} ` : ""}${field.label}` : layer.title,
              value: withUnit(row[field.name], field),
              color: fieldColor(layer, field),
            })),
        );
      }),
    );
  }

  function sentencesAt(raw: number): string[] {
    return panel.tracks.flatMap((track) =>
      drawnLayers(track).flatMap((layer) => rowsAt(layer, raw, match).map(rowSentence).filter((s): s is string => s !== null)),
    );
  }
</script>

{#snippet plot()}
    <Svg>
      <LayeredPlot
        {panel}
        {labels}
        {lineColorMode}
        paint={(css) => css}
        tickLabels={{ class: "text-xs fill-muted-foreground", fontSize: 11 }}
        text={{ muted: { class: "fill-muted-foreground" }, strong: { class: "fill-foreground font-semibold" } }}
        formatTick={tick}
        {valueExtents}
        hover
      />
    </Svg>
    <Tooltip.Root class="z-50 max-w-80 rounded-lg border border-border text-xs shadow-xl">
      {#snippet children({ data })}
        <Tooltip.Header value={data?.x} format={tick} class="mb-1 border-b border-border pb-1 font-semibold" />
        <Tooltip.List>
          {#each itemsAt(data?.raw ?? 0) as item (item.key)}
            <Tooltip.Item label={item.label} value={item.value} color={item.color ?? undefined} />
          {/each}
        </Tooltip.List>
        {#each sentencesAt(data?.raw ?? 0) as sentence, i (i)}
          <p class="mt-1 max-w-72 text-pretty text-muted-foreground">{sentence}</p>
        {/each}
      {/snippet}
    </Tooltip.Root>
{/snippet}

{#if kind === "category"}
  <div class="flex flex-col gap-4" data-testid="report-layered-chart">
    {#each panel.tracks as track (track.id)}
      {#each drawnLayers(track).filter((l) => l.mark === "bars") as layer (layer.id)}
        <CategoryBars {layer} {format} />
      {/each}
    {/each}
  </div>
{:else if points.length === 0}
  <p class="flex h-[200px] items-center justify-center text-sm text-muted-foreground">No data in this range.</p>
{:else}
  <div class="h-(--plot-height) w-full" style:--plot-height="{height}px" data-testid="report-layered-chart" data-tracks={panel.tracks.length}>
    {#if isTimeAxis(kind)}
      <Chart
        data={points}
        x="x"
        xScale={scaleTime()}
        xDomain={[toDomain(kind, extent[0]), toDomain(kind, extent[1])]}
        yDomain={[0, 1]}
        padding={{ left: 52, right: hasRight ? 52 : 12, top: 8, bottom: 24 }}
        tooltipContext={{ mode: "bisect-x" }}
      >
        {@render plot()}
      </Chart>
    {:else}
      <Chart
        data={points}
        x="x"
        xScale={scaleLinear()}
        xDomain={[toDomain(kind, extent[0]), toDomain(kind, extent[1])]}
        yDomain={[0, 1]}
        padding={{ left: 52, right: hasRight ? 52 : 12, top: 8, bottom: 24 }}
        tooltipContext={{ mode: "bisect-x" }}
      >
        {@render plot()}
      </Chart>
    {/if}
  </div>
  {#if (kind === "value" || kind === "lag") && xField}
    <p class="text-center text-xs text-muted-foreground" data-testid="report-x-caption">{xField.label}{xField.unit ? ` (${xField.unit})` : ""}</p>
  {/if}
  {#if showLegend && (entries.length > 1 || entries.some((e) => e.context))}
    <ul class="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground" data-testid="report-legend">
      {#each entries as entry (entry.id)}
        <li class="flex items-center gap-1.5">
          {#if entry.color}
            <span class="inline-block h-0.5 w-3 bg-(--swatch)" style:--swatch={entry.color}></span>
          {/if}
          {entry.title}{entry.unit ? ` (${entry.unit})` : ""}
          {#if entry.context}
            <span class="text-muted-foreground/80" data-testid="report-legend-context">· {entry.context}</span>
          {/if}
        </li>
      {/each}
    </ul>
  {/if}
{/if}
